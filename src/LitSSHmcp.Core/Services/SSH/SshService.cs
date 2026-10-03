using System.Diagnostics;
using System.Text;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using Renci.SshNet;

namespace LitSSHmcp.Core.Services.SSH;

public class SshService : ISshService
{
    private readonly string _logDir;
    private readonly object _logLock = new();
    private readonly ISshKnownHostsStore? _knownHosts;
    private readonly ISecurityOptionsProvider? _securityOptions;
    private readonly ITargetLimiter? _targetLimiter;

    public SshService()
    {
        _logDir = ConfigPaths.LogsDir;
        if (!Directory.Exists(_logDir))
            Directory.CreateDirectory(_logDir);
    }

    public SshService(ISshKnownHostsStore knownHosts, ISecurityOptionsProvider securityOptions, ITargetLimiter targetLimiter)
        : this()
    {
        _knownHosts = knownHosts;
        _securityOptions = securityOptions;
        _targetLimiter = targetLimiter;
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
        => (await ProbeConnectionAsync(server, ct)).Success;

    public async Task<ConnectionProbeResult> ProbeConnectionAsync(SshServerConfig server, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
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
                return new ConnectionProbeResult { Success = true, Duration = sw.Elapsed };
            }
            catch (Exception ex)
            {
                Log($"Connection to {server.Host}:{server.Port} failed: {ex.Message}", "ERROR");
                return new ConnectionProbeResult
                {
                    Success = false,
                    ErrorKind = ClassifySshException(ex),
                    Error = ex.Message,
                    Duration = sw.Elapsed
                };
            }
        }, ct);
    }

    /// <summary>
    /// 把底层异常归类成模型可处置的失败类型。
    /// 只回 bool 会让"密码错 / 超时 / 主机密钥被换"三者不可分——
    /// 其中主机密钥变化可能是中间人攻击，必须能单独暴露。
    /// </summary>
    public static string ClassifySshException(Exception? ex)
    {
        for (var depth = 0; ex != null && depth < 5; depth++, ex = ex.InnerException)
        {
            switch (ex)
            {
                case Renci.SshNet.Common.SshAuthenticationException:
                    return "auth";
                case Renci.SshNet.Common.SshOperationTimeoutException:
                    return "timeout";
                case System.Net.Sockets.SocketException:
                    return "network";
                case OperationCanceledException:
                    return "timeout";
            }

            var msg = ex.Message ?? string.Empty;
            if (ContainsAny(msg, "host key", "fingerprint", "key exchange"))
                return "host_key";
            if (ContainsAny(msg, "authentication failed", "permission denied", "username/password"))
                return "auth";
            if (ContainsAny(msg, "timed out", "timeout"))
                return "timeout";
            if (ContainsAny(msg, "no route", "refused", "unreachable", "name or service", "not known", "network is"))
                return "network";
        }

        return "unknown";
    }

    private static bool ContainsAny(string haystack, params string[] needles) =>
        needles.Any(n => haystack.Contains(n, StringComparison.OrdinalIgnoreCase));

    public async Task<CommandResult> ExecuteCommandAsync(SshServerConfig server, string command, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            if (!TryAcquireTarget($"ssh:{server.Id}", out var lease, out var limitReason))
                return RateLimitedCommand(limitReason);

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

                return RedactResult(new CommandResult
                {
                    Success = cmd.ExitStatus == 0,
                    Output = result,
                    Error = cmd.Error,
                    ExitCode = cmd.ExitStatus ?? -1,
                    Duration = sw.Elapsed
                }, server);
            }
            catch (Exception ex)
            {
                sw.Stop();
                Log($"Command execution failed: {ex.Message}", "ERROR");
                return RedactResult(new CommandResult
                {
                    Success = false,
                    Error = ex.Message,
                    ErrorKind = ClassifySshException(ex),
                    ExitCode = -1,
                    Duration = sw.Elapsed
                }, server);
            }
            finally
            {
                lease?.Dispose();
            }
        }, ct);
    }

    public async Task<CommandResult> ExecuteWithSudoAsync(SshServerConfig server, string command, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            if (!TryAcquireTarget($"ssh:{server.Id}", out var lease, out var limitReason))
                return RateLimitedCommand(limitReason);

            var sw = Stopwatch.StartNew();
            try
            {
                Log($"Executing sudo command on {server.Host}:{server.Port}");

                // 去掉 AI 可能在命令里自带的 `sudo` 前缀：否则在"已是 root/目标用户"的直连路径下会执行
                // 一个无 tty 的 `sudo ...`，sudo 会直接报 "a password is required"，而我们的提权密码注入
                // 只走 sudo/su 的交互式通道，管不到 AI 自带的那层 sudo。
                var effectiveCommand = StripSudoPrefix(command);

                using var client = CreateSshClient(server);
                client.Connect();

                CommandResult Execute() => server.SudoType switch
                {
                    SudoType.None => ExecuteCommandDirect(client, effectiveCommand, sw),
                    SudoType.CurrentUser => ExecuteWithCurrentUserSudo(client, server, effectiveCommand, sw),
                    SudoType.RootUser => server.Username == "root"
                        ? ExecuteCommandDirect(client, effectiveCommand, sw)
                        : ExecuteWithSuUser(client, server, "root", effectiveCommand, sw),
                    SudoType.CustomUser => string.IsNullOrEmpty(server.SudoUsername) || server.Username == server.SudoUsername
                        ? ExecuteCommandDirect(client, effectiveCommand, sw)
                        : ExecuteWithSuUser(client, server, server.SudoUsername, effectiveCommand, sw),
                    SudoType.Auto => ExecuteAutoSudo(client, server, effectiveCommand, sw),
                    _ => ExecuteCommandDirect(client, effectiveCommand, sw)
                };

                // 最终结果统一脱敏（即使内层已脱敏，这里再兜底，确保任何路径/异常都不外泄密码）
                var result = Execute();
                result.Escalation ??= server.SudoType switch
                {
                    SudoType.CurrentUser => "sudo",
                    SudoType.RootUser or SudoType.CustomUser => "su",
                    _ => "direct"
                };
                return RedactResult(result, server);
            }
            catch (Exception ex)
            {
                sw.Stop();
                Log($"Sudo command execution failed: {ex.Message}", "ERROR");
                return RedactResult(new CommandResult
                {
                    Success = false,
                    Error = ex.Message,
                    ExitCode = -1,
                    Duration = sw.Elapsed
                }, server);
            }
            finally
            {
                lease?.Dispose();
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
            Duration = sw.Elapsed,
            Escalation = "direct"
        };
    }

    private CommandResult ExecuteWithCurrentUserSudo(SshClient client, SshServerConfig server, string command, Stopwatch sw)
    {
        var password = server.SudoPassword ?? server.Password ?? string.Empty;
        // sudo -S 从 stdin 读取密码（不依赖 tty）；/bin/sh -c 兼容各发行版(不依赖 bash)；-p '' 关闭提示语。
        var sudoCmd = $"sudo -S -p '' /bin/sh -c '{command.Replace("'", "'\\''")}'";

        var result = ExecuteWithStdinPassword(client, sudoCmd, password, sw);

        // 部分发行版(RHEL/CentOS 的 Defaults requiretty 等)要求 sudo 必须有 tty，exec 通道无 tty 会被拒；
        // 此时回退到交互式 shell(pty) 重新执行（不兜底下发密码，避免误写到已运行命令的 stdin）。
        if (!result.Success && RequiresTty(result))
            result = ExecuteWithPasswordViaShell(client, sudoCmd, password, sw, fallbackSendPassword: false);

        result.Escalation = "sudo";
        return result;
    }

    /// <summary>判断失败是否因"需要 tty"（requiretty / 无终端）。</summary>
    public static bool RequiresTty(CommandResult result)
    {
        var text = ((result.Error ?? string.Empty) + "\n" + (result.Output ?? string.Empty)).ToLowerInvariant();
        return text.Contains("must have a tty")
            || text.Contains("you must have a tty")
            || text.Contains("a tty is required")
            || text.Contains("a terminal is required")
            || text.Contains("no tty present")
            || text.Contains("is not a terminal");
    }

    /// <summary>
    /// 通过 exec 通道执行命令，并把提权密码写入其 stdin（sudo -S 从 stdin 读密码）。
    /// 这是 SSH.NET 官方推荐的 sudo -S 用法，不依赖交互式 shell，也不会有密码回显。
    /// </summary>
    private CommandResult ExecuteWithStdinPassword(SshClient client, string command, string password, Stopwatch sw)
    {
        using var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromSeconds(SudoShellTimeoutSeconds);
        try
        {
            // 先启动执行，再获取 stdin（SSH.NET 2026 要求：输入流只能在执行期间使用）
            var async = cmd.BeginExecute();
            Stream? stdin = null;
            try
            {
                stdin = cmd.CreateInputStream();
                // 即使密码为空也写一行，避免 sudo 一直等 stdin 造成挂起
                var bytes = Encoding.UTF8.GetBytes((password ?? string.Empty) + "\n");
                stdin.Write(bytes, 0, bytes.Length);
                stdin.Flush();
            }
            catch (Exception ex)
            {
                Log($"写入 sudo 密码失败: {ex.Message}", "ERROR");
            }

            var stdout = new StringBuilder();
            using (var reader = new StreamReader(cmd.OutputStream, Encoding.UTF8))
            {
                var buffer = new char[4096];
                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                    stdout.Append(buffer, 0, read);
            }

            cmd.EndExecute(async);
            stdin?.Dispose();
            sw.Stop();

            var output = Redact(stdout.ToString(), password);
            var error = Redact(cmd.Error ?? string.Empty, password);
            var exit = cmd.ExitStatus ?? -1;
            return new CommandResult
            {
                Success = exit == 0,
                Output = output,
                Error = exit == 0 ? string.Empty : DescribeSudoFailure(error + "\n" + output),
                ErrorKind = cmd.ExitStatus is null ? "timeout" : null,
                ExitCode = exit,
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log($"sudo (stdin) execution failed: {ex.Message}", "ERROR");
            return new CommandResult { Success = false, Error = ex.Message, ExitCode = -1, Duration = sw.Elapsed };
        }
    }

    private CommandResult ExecuteWithSuUser(SshClient client, SshServerConfig server, string targetUser, string command, Stopwatch sw)
    {
        var password = server.SudoPassword ?? string.Empty;
        var escapedCmd = command.Replace("'", "'\\''");
        var shellCmd = $"su - {targetUser} -c '{escapedCmd}'";
        var result = ExecuteWithPasswordViaShell(client, shellCmd, password, sw);
        result.Escalation = "su";
        return result;
    }

    /// <summary>
    /// 自动提权：先试"当前用户 sudo"(用配置的提权密码)，失败(未授权/密码不通过)再回退 <c>su - root</c>。
    /// 适配"登录账号不在 sudoers、但可以 su 到 root"或反之的环境，无需用户事先判断。
    /// </summary>
    private CommandResult ExecuteAutoSudo(SshClient client, SshServerConfig server, string command, Stopwatch sw)
    {
        if (server.Username == "root")
            return ExecuteCommandDirect(client, command, sw);

        var viaSudo = ExecuteWithCurrentUserSudo(client, server, command, sw);
        if (viaSudo.Success)
        {
            viaSudo.Escalation = "auto:sudo";
            return viaSudo;
        }

        var viaSu = ExecuteWithSuUser(client, server, "root", command, sw);
        if (viaSu.Success)
        {
            viaSu.Escalation = "auto:su";
            return viaSu;
        }

        var sudoErr = string.IsNullOrWhiteSpace(viaSudo.Error) ? "(无)" : viaSudo.Error.Trim();
        var suErr = string.IsNullOrWhiteSpace(viaSu.Error) ? "(无)" : viaSu.Error.Trim();
        return new CommandResult
        {
            Success = false,
            Output = viaSu.Output.Length >= viaSudo.Output.Length ? viaSu.Output : viaSudo.Output,
            Escalation = "auto:failed",
            Error = $"自动提权失败。sudo: {sudoErr}；su: {suErr}",
            ErrorKind = viaSudo.ErrorKind ?? viaSu.ErrorKind,
            ExitCode = viaSu.ExitCode,
            Duration = sw.Elapsed
        };
    }

    // sudo/su 交互式提权的整体等待上限（秒）。提权命令可能较慢（装包/重启服务），必须给足时间；
    // 旧实现把"未取到退出码(-1)"也当作成功，导致失败被吞掉，这里改为明确超时。
    private const int SudoShellTimeoutSeconds = 120;

    private CommandResult ExecuteWithPasswordViaShell(SshClient client, string command, string password, Stopwatch sw, bool fallbackSendPassword = true)
    {
        try
        {
            using var shell = client.CreateShellStream("xterm", 80, 24, 800, 600, 4096);
            
            var output = new StringBuilder();
            var passwordSent = false;
            var startTime = DateTime.UtcNow;
            var lastOutputTime = DateTime.UtcNow;

            // 关键：必须由外层 shell 回传退出码标记。此前从未写入过标记，
            // 导致 exitCode 恒为 -1 且被判定为成功 —— sudo 命令无论成败都报成功。
            shell.WriteLine(command + "; echo LITSSH_EXIT:$?");
            Thread.Sleep(300);

            while ((DateTime.UtcNow - startTime).TotalSeconds < SudoShellTimeoutSeconds)
            {
                if (shell.DataAvailable)
                {
                    var before = output.Length;
                    output.Append(shell.Read());
                    if (output.Length > before)
                        lastOutputTime = DateTime.UtcNow;

                    if (TryParseExitMarker(output.ToString(), out _))
                        break;

                    if (!passwordSent && LooksLikePasswordPrompt(output.ToString()))
                    {
                        shell.Write(password + "\n");
                        passwordSent = true;
                        Thread.Sleep(200);
                    }
                }

                var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                var idle = (DateTime.UtcNow - lastOutputTime).TotalSeconds;

                // 兜底 1：等待 3 秒仍没识别到密码提示（提示被吞/本地化/自定义）且配置了密码 → 主动下发一次，
                // 避免 su/sudo 一直等密码把整个调用拖到超时。
                if (!passwordSent && !string.IsNullOrEmpty(password) && elapsed > 3 && fallbackSendPassword)
                {
                    shell.Write(password + "\n");
                    passwordSent = true;
                    Thread.Sleep(200);
                }
                // 兜底 2：没配置提权密码，无法交互式提权 → 5 秒后不再空等
                else if (!passwordSent && string.IsNullOrEmpty(password) && elapsed > 5)
                {
                    break;
                }
                // 已喂过密码且 30 秒无新输出：远端大概率已结束(标记丢失)或卡住，按超时退出
                else if (passwordSent && idle > 30)
                {
                    break;
                }

                Thread.Sleep(50);
            }

            var fullOutput = output.ToString();
            // 返回给 AI 的输出必须脱敏：即使远端回显了密码，也绝不外泄
            var safeOutput = Redact(fullOutput, password);
            var exitCode = TryParseExitMarker(fullOutput, out var parsedCode) ? parsedCode : -1;

            sw.Stop();
            Log($"Sudo command executed via shell, exit code: {exitCode}, duration: {sw.ElapsedMilliseconds}ms");

            var timedOut = exitCode < 0;
            string? error = null;
            string? errorKind = null;
            if (timedOut)
            {
                errorKind = "timeout";
                error = string.IsNullOrEmpty(password)
                    ? "未配置提权密码(su/sudo 需要密码), 无法提权; 请在服务器配置里填写提权密码(SudoPassword)。"
                    : $"sudo/su 执行超时或未取得退出码({SudoShellTimeoutSeconds}秒内未回传 LITSSH_EXIT); " +
                      "若因 requiretty/提示未识别导致, 请检查该服务器的提权方式, 或改用 ssh_execute_command。";
            }
            else if (exitCode != 0)
            {
                error = DescribeSudoFailure(safeOutput);
            }

            return new CommandResult
            {
                Success = exitCode == 0,
                Output = StripExitMarker(safeOutput),
                Error = error ?? string.Empty,
                ErrorKind = errorKind,
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

    /// <summary>
    /// 识别 sudo / su 的密码提示。此前只匹配 "[sudo]"/"password for"，导致 `su` 的 "Password:"
    /// 提示不被识别、密码从未发送，最终 30 秒超时（旧实现又把超时当成功，掩盖了这个 bug）。
    /// </summary>
    public static bool LooksLikePasswordPrompt(string text)
    {
        var t = text.ToLowerInvariant();
        return t.Contains("[sudo]")
            || t.Contains("password for")
            || t.Contains("password:")
            || t.Contains("密码")
            || t.Contains("passwort")          // 德语
            || t.Contains("mot de passe")      // 法语
            || t.Contains("contraseña")        // 西语
            || t.Contains("kennwort");         // 德语(密码的另一种写法)
    }

    /// <summary>去掉 AI 命令里自带的 `sudo` 前缀（`sudo cmd` 形式），避免与外层提权叠加、以及无 tty 时报错。</summary>
    public static string StripSudoPrefix(string command)
    {
        var t = command.TrimStart();
        if (t == "sudo")
            return string.Empty;

        if (t.StartsWith("sudo ", StringComparison.Ordinal))
        {
            var rest = t[5..].TrimStart();
            // 仅剥离"sudo 后直接是命令"的情形；若后随 sudo 选项（以 '-' 开头）则保留，避免误删 -u/-S 等
            if (rest.Length > 0 && !rest.StartsWith('-'))
                return rest;
        }

        return command;
    }

    private static string Redact(string text, string? password) =>
        string.IsNullOrEmpty(password) ? text : text.Replace(password, "******");

    /// <summary>对一段文本脱敏：替换该服务器所有非空口令/密钥（SSH 密码、密钥口令、提权密码）。</summary>
    private static string RedactSecrets(string? text, SshServerConfig server)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        var result = text;
        foreach (var secret in new[] { server.Password, server.KeyFilePassphrase, server.SudoPassword })
            if (!string.IsNullOrEmpty(secret))
                result = result.Replace(secret, "******");
        return result;
    }

    /// <summary>对返回结果做最终脱敏（Output/Error），保证任何路径（含异常）都不把密码带给调用方/AI 客户端。</summary>
    private static CommandResult RedactResult(CommandResult result, SshServerConfig server)
    {
        result.Output = RedactSecrets(result.Output, server);
        result.Error = RedactSecrets(result.Error, server);
        return result;
    }

    private const string ExitMarkerPrefix = "LITSSH_EXIT:";

    /// <summary>
    /// 解析退出码标记。必须"行首 + 紧跟数字"，以排除交互式 shell**回显命令行**里
    /// 出现的字面量 <c>LITSSH_EXIT:$?</c>（否则 shell 一收到回显就误判为已结束）。
    /// </summary>
    public static bool TryParseExitMarker(string output, out int code)
    {
        code = -1;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(ExitMarkerPrefix, StringComparison.Ordinal))
                continue;

            var rest = line[ExitMarkerPrefix.Length..].Trim();
            if (rest.Length == 0 || !char.IsDigit(rest[0]))
                continue;

            var digits = new string(rest.TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(digits, out code))
                return true;
        }

        return false;
    }

    private static string DescribeSudoFailure(string output)
    {
        if (output.Contains("a password is required", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("a terminal is required", StringComparison.OrdinalIgnoreCase))
            return "提权失败：sudo/su 未获得密码（请检查该服务器的 SudoType / SudoPassword 配置是否完整）。";

        var last = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        return string.IsNullOrEmpty(last) ? "提权命令执行失败。" : last;
    }

    public async Task<FileTransferResult> UploadFileAsync(SshServerConfig server, string localPath, string remotePath, IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            if (!TryAcquireTarget($"ssh:{server.Id}", out var lease, out var limitReason))
                return RateLimitedFile(limitReason);

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
            finally
            {
                lease?.Dispose();
            }
        }, ct);
    }

    public async Task<FileTransferResult> DownloadFileAsync(SshServerConfig server, string remotePath, string localPath, IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            if (!TryAcquireTarget($"ssh:{server.Id}", out var lease, out var limitReason))
                return RateLimitedFile(limitReason);

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
            finally
            {
                lease?.Dispose();
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

    /// <summary>单次列目录最多返回的条目数，避免 /proc、node_modules 之类的目录打爆上下文。</summary>
    private const int MaxListEntries = 500;

    public async Task<RemoteFileListResult> ListRemoteFilesAsync(SshServerConfig server, string remotePath, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            Exception? sftpFailure = null;
            try
            {
                Log($"Listing files in {server.Host}:{remotePath}");
                using var sftp = CreateSftpClient(server);
                sftp.Connect();

                var files = sftp.ListDirectory(remotePath);
                Log($"Listed {files.Count()} files via SFTP");
                var mapped = files
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

                return new RemoteFileListResult
                {
                    Success = true,
                    Files = mapped.Take(MaxListEntries).ToArray(),
                    Truncated = mapped.Length > MaxListEntries
                };
            }
            catch (Exception ex)
            {
                sftpFailure = ex;
                Log($"SFTP list failed: {ex.Message}, trying SSH exec fallback", "WARN");
            }

            try
            {
                using var client = CreateSshClient(server);
                client.Connect();

                var escapedPath = remotePath.Replace("'", "'\\''");
                using var cmd = client.CreateCommand($"ls -la '{escapedPath}'");
                cmd.CommandTimeout = TimeSpan.FromSeconds(30);
                var result = cmd.Execute();

                if (cmd.ExitStatus != 0)
                {
                    var detail = string.IsNullOrWhiteSpace(cmd.Error) ? "ls 返回非零退出码" : cmd.Error;
                    return new RemoteFileListResult { Success = false, ErrorKind = "list_failed", Error = detail };
                }

                Log($"Listed files via SSH exec fallback");
                var entries = result.Split('\n')
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

                return new RemoteFileListResult
                {
                    Success = true,
                    Files = entries.Take(MaxListEntries).ToArray(),
                    Truncated = entries.Length > MaxListEntries
                };
            }
            catch (Exception ex)
            {
                Log($"SSH exec list failed: {ex.Message}", "ERROR");
                // 关键：绝不返回空数组冒充"空目录"。列目录失败必须让调用方看到。
                return new RemoteFileListResult
                {
                    Success = false,
                    ErrorKind = ClassifySshException(ex),
                    Error = string.IsNullOrWhiteSpace(ex.Message)
                        ? (sftpFailure?.Message ?? "列目录失败")
                        : ex.Message
                };
            }
        }, ct);
    }

    private bool TryAcquireTarget(string key, out IDisposable? lease, out string? reason)
    {
        lease = null;
        reason = null;
        if (_targetLimiter == null)
            return true;

        return _targetLimiter.TryAcquire(key, out lease, out reason);
    }

    private static CommandResult RateLimitedCommand(string? reason) => new()
    {
        Success = false,
        Error = $"操作被限流({reason})。该目标调用过于频繁或并发过高，请稍后退避重试(建议等待5秒)。",
        ErrorKind = "rate_limited",
        ExitCode = -1
    };

    private static FileTransferResult RateLimitedFile(string? reason) => new()
    {
        Success = false,
        Message = $"操作被限流({reason})。该目标调用过于频繁或并发过高，请稍后退避重试(建议等待5秒)。"
    };

    /// <summary>去掉远端回传的退出码标记行，避免污染给模型的输出。</summary>
    public static string StripExitMarker(string output)
    {
        var lines = output.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (!t.StartsWith(ExitMarkerPrefix, StringComparison.Ordinal))
                continue;

            var rest = t[ExitMarkerPrefix.Length..].Trim();
            if (rest.Length > 0 && char.IsDigit(rest[0]))
                return string.Join('\n', lines.Take(i));
        }

        return output;
    }

    private SshClient CreateSshClient(SshServerConfig server) =>        SshClientFactory.Create(server, _knownHosts, _securityOptions?.SshHostKey.Mode ?? SshHostKeyMode.Tofu);

    private SftpClient CreateSftpClient(SshServerConfig server) =>
        SshClientFactory.CreateSftp(server, _knownHosts, _securityOptions?.SshHostKey.Mode ?? SshHostKeyMode.Tofu);
}