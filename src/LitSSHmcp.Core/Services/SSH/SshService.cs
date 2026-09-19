using System.Diagnostics;
using System.Text;
using LitSSHmcp.Core.Models;
using Renci.SshNet;

namespace LitSSHmcp.Core.Services.SSH;

public class SshService : ISshService
{
    private readonly string _logDir;
    private readonly object _logLock = new();

    public SshService()
    {
        _logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        if (!Directory.Exists(_logDir))
            Directory.CreateDirectory(_logDir);
    }

    private void Log(string message, string level = "INFO")
    {
        try
        {
            var logFile = Path.Combine(_logDir, $"ssh_{DateTime.Now:yyyyMMdd}.log");
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var logEntry = $"[{timestamp}] [{level}] {message}{Environment.NewLine}";

            lock (_logLock)
            {
                File.AppendAllText(logFile, logEntry, Encoding.UTF8);
            }
        }
        catch { }
    }

    public async Task<bool> TestConnectionAsync(SshServerConfig server, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                Log($"Testing connection to {server.Host}:{server.Port} as {server.Username}");
                using var client = CreateSshClient(server);
                client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(10);
                client.Connect();
                Log($"Connection to {server.Host}:{server.Port} successful");
                client.Disconnect();
                return true;
            }
            catch (Exception ex)
            {
                Log($"Connection to {server.Host}:{server.Port} failed: {ex.Message}", "ERROR");
                return false;
            }
        }, ct);
    }

    public async Task<CommandResult> ExecuteCommandAsync(SshServerConfig server, string command, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                Log($"Executing command on {server.Host}:{server.Port}: {command}");
                using var client = CreateSshClient(server);
                client.Connect();
                using var cmd = client.CreateCommand(command);
                cmd.CommandTimeout = TimeSpan.FromSeconds(60);
                var result = cmd.Execute();
                sw.Stop();

                Log($"Command executed, exit code: {cmd.ExitStatus}, duration: {sw.ElapsedMilliseconds}ms");
                if (!string.IsNullOrEmpty(result))
                    Log($"Command output: {result.Substring(0, Math.Min(result.Length, 500))}");

                return new CommandResult
                {
                    Success = cmd.ExitStatus == 0,
                    Output = result,
                    Error = cmd.Error,
                    ExitCode = cmd.ExitStatus ?? -1,
                    Duration = sw.Elapsed
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                Log($"Command execution failed: {ex.Message}", "ERROR");
                return new CommandResult
                {
                    Success = false,
                    Error = ex.Message,
                    ExitCode = -1,
                    Duration = sw.Elapsed
                };
            }
        }, ct);
    }

    public async Task<CommandResult> ExecuteWithSudoAsync(SshServerConfig server, string command, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                Log($"Executing sudo command on {server.Host}:{server.Port}");
                using var client = CreateSshClient(server);
                client.Connect();

                switch (server.SudoType)
                {
                    case SudoType.None:
                        return ExecuteCommandDirect(client, command, sw);

                    case SudoType.CurrentUser:
                        return ExecuteWithCurrentUserSudo(client, server, command, sw);

                    case SudoType.RootUser:
                        if (server.Username == "root")
                            return ExecuteCommandDirect(client, command, sw);
                        return ExecuteWithSuUser(client, server, "root", command, sw);

                    case SudoType.CustomUser:
                        if (string.IsNullOrEmpty(server.SudoUsername))
                            return ExecuteCommandDirect(client, command, sw);
                        if (server.Username == server.SudoUsername)
                            return ExecuteCommandDirect(client, command, sw);
                        return ExecuteWithSuUser(client, server, server.SudoUsername, command, sw);

                    default:
                        return ExecuteCommandDirect(client, command, sw);
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                Log($"Sudo command execution failed: {ex.Message}", "ERROR");
                return new CommandResult
                {
                    Success = false,
                    Error = ex.Message,
                    ExitCode = -1,
                    Duration = sw.Elapsed
                };
            }
        }, ct);
    }

    private CommandResult ExecuteCommandDirect(SshClient client, string command, Stopwatch sw)
    {
        using var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromSeconds(60);
        var result = cmd.Execute();
        sw.Stop();

        Log($"Command executed, exit code: {cmd.ExitStatus}, duration: {sw.ElapsedMilliseconds}ms");

        return new CommandResult
        {
            Success = cmd.ExitStatus == 0,
            Output = result,
            Error = cmd.Error,
            ExitCode = cmd.ExitStatus ?? -1,
            Duration = sw.Elapsed
        };
    }

    private CommandResult ExecuteWithCurrentUserSudo(SshClient client, SshServerConfig server, string command, Stopwatch sw)
    {
        var password = server.SudoPassword ?? server.Password ?? string.Empty;
        var sudoCmd = $"sudo -S bash -c '{command.Replace("'", "'\\''")}'";
        return ExecuteWithPasswordViaShell(client, sudoCmd, password, sw);
    }

    private CommandResult ExecuteWithSuUser(SshClient client, SshServerConfig server, string targetUser, string command, Stopwatch sw)
    {
        var password = server.SudoPassword ?? string.Empty;
        var escapedCmd = command.Replace("'", "'\\''");
        var shellCmd = $"su - {targetUser} -c '{escapedCmd}'";
        return ExecuteWithPasswordViaShell(client, shellCmd, password, sw);
    }

    private CommandResult ExecuteWithPasswordViaShell(SshClient client, string command, string password, Stopwatch sw)
    {
        try
        {
            using var shell = client.CreateShellStream("xterm", 80, 24, 800, 600, 4096);
            
            var output = new StringBuilder();
            var passwordSent = false;
            var startTime = DateTime.UtcNow;
            var lastOutputTime = DateTime.UtcNow;

            shell.WriteLine(command);
            Thread.Sleep(300);

            while ((DateTime.UtcNow - startTime).TotalSeconds < 30)
            {
                if (shell.DataAvailable)
                {
                    var data = shell.Read();
                    output.Append(data);
                    lastOutputTime = DateTime.UtcNow;

                    if (!passwordSent)
                    {
                        var currentOutput = output.ToString().ToLower();
                        if (currentOutput.Contains("[sudo]") || currentOutput.Contains("password for"))
                        {
                            shell.Write(password + "\n");
                            passwordSent = true;
                            Thread.Sleep(200);
                        }
                    }
                }
                else
                {
                    if ((DateTime.UtcNow - lastOutputTime).TotalMilliseconds > 500)
                    {
                        if (passwordSent)
                        {
                            Thread.Sleep(300);
                            if (!shell.DataAvailable)
                                break;
                        }
                    }
                    Thread.Sleep(50);
                }
            }

            var fullOutput = output.ToString();
            var lines = fullOutput.Split('\n');
            var exitCode = -1;

            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("LITSSH_EXIT:"))
                {
                    var parts = line.Split(':');
                    if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out var code))
                    {
                        exitCode = code;
                        break;
                    }
                }
            }

            sw.Stop();
            Log($"Sudo command executed via shell, duration: {sw.ElapsedMilliseconds}ms");

            return new CommandResult
            {
                Success = exitCode == 0 || exitCode == -1,
                Output = fullOutput,
                ExitCode = exitCode,
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log($"Shell execution failed: {ex.Message}", "ERROR");
            return new CommandResult
            {
                Success = false,
                Error = ex.Message,
                ExitCode = -1,
                Duration = sw.Elapsed
            };
        }
    }

    public async Task<FileTransferResult> UploadFileAsync(SshServerConfig server, string localPath, string remotePath, IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var fileInfo = new FileInfo(localPath);
                if (!fileInfo.Exists)
                {
                    Log($"Upload failed: local file not found: {localPath}", "ERROR");
                    return new FileTransferResult { Success = false, Message = $"File not found: {localPath}" };
                }

                var totalBytes = fileInfo.Length;
                Log($"Uploading {localPath} ({totalBytes} bytes) to {server.Host}:{remotePath}");

                try
                {
                    using var sftp = CreateSftpClient(server);
                    sftp.Connect();
                    Log("SFTP connection established");

                    var dir = Path.GetDirectoryName(remotePath)?.Replace('\\', '/');
                    if (!string.IsNullOrEmpty(dir))
                    {
                        try { sftp.CreateDirectory(dir); } catch { }
                    }

                    using var fileStream = File.OpenRead(localPath);
                    long uploaded = 0;

                    sftp.UploadFile(fileStream, remotePath, (uploadedBytes) =>
                    {
                        uploaded += (long)uploadedBytes;
                        progress?.Report(new FileTransferProgress
                        {
                            BytesTransferred = uploaded,
                            TotalBytes = totalBytes
                        });
                    });

                    sw.Stop();
                    Log($"Upload completed via SFTP, duration: {sw.ElapsedMilliseconds}ms");
                    return new FileTransferResult
                    {
                        Success = true,
                        Message = "Upload completed (SFTP)",
                        BytesTransferred = totalBytes,
                        Duration = sw.Elapsed
                    };
                }
                catch (Exception sftpEx)
                {
                    Log($"SFTP upload failed: {sftpEx.Message}, trying SSH exec fallback", "WARN");
                    return UploadViaSshExec(server, localPath, remotePath, totalBytes, sw);
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                Log($"Upload failed: {ex.Message}", "ERROR");
                return new FileTransferResult
                {
                    Success = false,
                    Message = $"Upload failed: {ex.Message}",
                    Duration = sw.Elapsed
                };
            }
        }, ct);
    }

    public async Task<FileTransferResult> DownloadFileAsync(SshServerConfig server, string remotePath, string localPath, IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var tempPath = localPath + ".tmp";

            try
            {
                var dir = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                Log($"Downloading {server.Host}:{remotePath} to {localPath}");

                try
                {
                    using var sftp = CreateSftpClient(server);
                    sftp.Connect();
                    Log("SFTP connection established");

                    if (!sftp.Exists(remotePath))
                    {
                        Log($"Remote file not found: {remotePath}", "ERROR");
                        return new FileTransferResult { Success = false, Message = $"Remote file not found: {remotePath}" };
                    }

                    var fileInfo = sftp.GetAttributes(remotePath);
                    var totalBytes = (long)fileInfo.Size;
                    Log($"Remote file size: {totalBytes} bytes");

                    using (var fileStream = File.Create(tempPath))
                    {
                        long downloaded = 0;

                        sftp.DownloadFile(remotePath, fileStream, (downloadedBytes) =>
                        {
                            downloaded += (long)downloadedBytes;
                            progress?.Report(new FileTransferProgress
                            {
                                BytesTransferred = downloaded,
                                TotalBytes = totalBytes
                            });
                        });
                    }

                    if (File.Exists(localPath))
                        File.Delete(localPath);
                    File.Move(tempPath, localPath);

                    sw.Stop();
                    Log($"Download completed via SFTP, duration: {sw.ElapsedMilliseconds}ms");
                    return new FileTransferResult
                    {
                        Success = true,
                        Message = "Download completed (SFTP)",
                        BytesTransferred = totalBytes,
                        Duration = sw.Elapsed
                    };
                }
                catch (Exception sftpEx)
                {
                    Log($"SFTP download failed: {sftpEx.Message}, trying SSH exec fallback", "WARN");
                    if (File.Exists(tempPath))
                    {
                        try { File.Delete(tempPath); } catch { }
                    }

                    return DownloadViaSshExec(server, remotePath, localPath, sw);
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                Log($"Download failed: {ex.Message}", "ERROR");
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
                return new FileTransferResult
                {
                    Success = false,
                    Message = $"Download failed: {ex.Message}",
                    Duration = sw.Elapsed
                };
            }
        }, ct);
    }

    private FileTransferResult UploadViaSshExec(SshServerConfig server, string localPath, string remotePath, long totalBytes, Stopwatch sw)
    {
        try
        {
            using var client = CreateSshClient(server);
            client.Connect();

            var dir = Path.GetDirectoryName(remotePath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir))
            {
                using var mkdirCmd = client.CreateCommand($"mkdir -p '{dir}'");
                mkdirCmd.Execute();
            }

            var fileContent = File.ReadAllText(localPath);
            var escapedPath = remotePath.Replace("'", "'\\''");

            using var cmd = client.CreateCommand($"cat > '{escapedPath}' << 'LITSSH_EOF'\n{fileContent}\nLITSSH_EOF");
            cmd.CommandTimeout = TimeSpan.FromSeconds(60);
            var result = cmd.Execute();

            sw.Stop();

            if (cmd.ExitStatus == 0)
            {
                Log($"Upload completed via SSH exec fallback, duration: {sw.ElapsedMilliseconds}ms");
                return new FileTransferResult
                {
                    Success = true,
                    Message = "Upload completed (SSH exec fallback)",
                    BytesTransferred = totalBytes,
                    Duration = sw.Elapsed
                };
            }
            else
            {
                Log($"SSH exec upload failed: {cmd.Error}", "ERROR");
                return new FileTransferResult
                {
                    Success = false,
                    Message = $"Upload failed via SSH exec: {cmd.Error}",
                    Duration = sw.Elapsed
                };
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log($"SSH exec upload failed: {ex.Message}", "ERROR");
            return new FileTransferResult
            {
                Success = false,
                Message = $"Upload failed (both SFTP and SSH exec): {ex.Message}",
                Duration = sw.Elapsed
            };
        }
    }

    private FileTransferResult DownloadViaSshExec(SshServerConfig server, string remotePath, string localPath, Stopwatch sw)
    {
        try
        {
            using var client = CreateSshClient(server);
            client.Connect();

            var escapedPath = remotePath.Replace("'", "'\\''");

            using var cmd = client.CreateCommand($"cat '{escapedPath}'");
            cmd.CommandTimeout = TimeSpan.FromSeconds(60);
            var content = cmd.Execute();

            sw.Stop();

            if (cmd.ExitStatus == 0)
            {
                var dir = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(localPath, content);

                Log($"Download completed via SSH exec fallback, duration: {sw.ElapsedMilliseconds}ms");
                return new FileTransferResult
                {
                    Success = true,
                    Message = "Download completed (SSH exec fallback)",
                    BytesTransferred = content.Length,
                    Duration = sw.Elapsed
                };
            }
            else
            {
                Log($"SSH exec download failed: {cmd.Error}", "ERROR");
                return new FileTransferResult
                {
                    Success = false,
                    Message = $"Download failed via SSH exec: {cmd.Error}",
                    Duration = sw.Elapsed
                };
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log($"SSH exec download failed: {ex.Message}", "ERROR");
            return new FileTransferResult
            {
                Success = false,
                Message = $"Download failed (both SFTP and SSH exec): {ex.Message}",
                Duration = sw.Elapsed
            };
        }
    }

    public async Task<RemoteFileInfo[]> ListRemoteFilesAsync(SshServerConfig server, string remotePath, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                Log($"Listing files in {server.Host}:{remotePath}");
                using var sftp = CreateSftpClient(server);
                sftp.Connect();

                var files = sftp.ListDirectory(remotePath);
                Log($"Listed {files.Count()} files via SFTP");
                return files
                    .Where(f => f.Name != "." && f.Name != "..")
                    .Select(f => new RemoteFileInfo
                    {
                        Name = f.Name,
                        FullName = f.FullName,
                        Size = (long)f.Length,
                        LastModified = f.LastWriteTime,
                        IsDirectory = f.IsDirectory,
                        IsSymbolicLink = f.IsSymbolicLink
                    })
                    .ToArray();
            }
            catch (Exception sftpEx)
            {
                Log($"SFTP list failed: {sftpEx.Message}, trying SSH exec fallback", "WARN");
                try
                {
                    using var client = CreateSshClient(server);
                    client.Connect();

                    var escapedPath = remotePath.Replace("'", "'\\''");
                    using var cmd = client.CreateCommand($"ls -la '{escapedPath}'");
                    cmd.CommandTimeout = TimeSpan.FromSeconds(30);
                    var result = cmd.Execute();

                    if (cmd.ExitStatus == 0)
                    {
                        Log($"Listed files via SSH exec fallback");
                        return result.Split('\n')
                            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith("total"))
                            .Select(line =>
                            {
                                var parts = line.Split(new[] { ' ' }, 9, StringSplitOptions.RemoveEmptyEntries);
                                if (parts.Length >= 9)
                                {
                                    var name = parts[8];
                                    var isDir = parts[0].StartsWith("d");
                                    var isLink = parts[0].StartsWith("l");
                                    long.TryParse(parts[4], out var size);

                                    return new RemoteFileInfo
                                    {
                                        Name = name,
                                        FullName = remotePath.TrimEnd('/') + "/" + name,
                                        Size = size,
                                        IsDirectory = isDir,
                                        IsSymbolicLink = isLink
                                    };
                                }
                                return null;
                            })
                            .Where(f => f != null && f.Name != "." && f.Name != "..")
                            .Cast<RemoteFileInfo>()
                            .ToArray();
                    }
                }
                catch (Exception ex)
                {
                    Log($"SSH exec list failed: {ex.Message}", "ERROR");
                }
                return Array.Empty<RemoteFileInfo>();
            }
        }, ct);
    }

    private SshClient CreateSshClient(SshServerConfig server)
    {
        if (server.AuthType == AuthType.KeyFile && !string.IsNullOrEmpty(server.KeyFilePath))
        {
            var keyFile = string.IsNullOrEmpty(server.KeyFilePassphrase)
                ? new PrivateKeyFile(server.KeyFilePath)
                : new PrivateKeyFile(server.KeyFilePath, server.KeyFilePassphrase);
            return new SshClient(server.Host, server.Port, server.Username, keyFile);
        }

        return new SshClient(server.Host, server.Port, server.Username, server.Password ?? string.Empty);
    }

    private SftpClient CreateSftpClient(SshServerConfig server)
    {
        SftpClient client;
        if (server.AuthType == AuthType.KeyFile && !string.IsNullOrEmpty(server.KeyFilePath))
        {
            var keyFile = string.IsNullOrEmpty(server.KeyFilePassphrase)
                ? new PrivateKeyFile(server.KeyFilePath)
                : new PrivateKeyFile(server.KeyFilePath, server.KeyFilePassphrase);
            client = new SftpClient(server.Host, server.Port, server.Username, keyFile);
        }
        else
        {
            client = new SftpClient(server.Host, server.Port, server.Username, server.Password ?? string.Empty);
        }

        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }
}