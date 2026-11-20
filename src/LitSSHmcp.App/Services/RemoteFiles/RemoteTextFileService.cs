using System.IO;
using System.Security.Cryptography;
using System.Text;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.App.Services.RemoteFiles;

public sealed class RemoteTextFileReadResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool TooLarge { get; init; }
    public bool Binary { get; init; }
    public bool Symlink { get; init; }
    public string Content { get; init; } = string.Empty;
    public byte[] Bytes { get; init; } = Array.Empty<byte>();
    public Encoding Encoding { get; init; } = new UTF8Encoding(false);
    public bool HasBom { get; init; }
    public string LineEnding { get; init; } = "LF";
    public RemoteTextFileSnapshot? Snapshot { get; init; }

    public static RemoteTextFileReadResult Fail(string message, RemoteTextFileSnapshot? snapshot = null)
        => new() { Message = message, Snapshot = snapshot };
}

public sealed class RemoteTextFileSaveResult
{
    public bool Success { get; init; }
    public bool Conflict { get; init; }
    public bool AlreadyExists { get; init; }
    public bool MetadataChanged { get; init; }
    public string? BackupPath { get; init; }
    public string Message { get; init; } = string.Empty;
    public RemoteTextFileSnapshot? Snapshot { get; init; }

    public static RemoteTextFileSaveResult Blocked(string message, RemoteTextFileSnapshot? snapshot = null)
        => new() { Message = message, Snapshot = snapshot };
}

public sealed record RemoteTextFileSnapshot(
    long Size,
    DateTime LastModified,
    string Permissions,
    string Owner,
    bool IsSymbolicLink = false,
    string? Mode = null,
    uint? Uid = null,
    uint? Gid = null,
    string? Acl = null,
    string? Xattrs = null,
    string? Selinux = null,
    string? Sha256 = null);

/// <summary>
/// 远程文本文件读取/保存：SFTP 下载到本地临时文件后解码；保存通过原位 upload 覆盖，
/// 尽量保留远端文件的 inode/owner/mode，不做 chown。
/// </summary>
public sealed class RemoteTextFileService
{
    public const long SoftLimitBytes = 2L * 1024 * 1024;
    public const long HardLimitBytes = 10L * 1024 * 1024;

    private readonly ISshService _ssh;

    static RemoteTextFileService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public RemoteTextFileService(ISshService ssh) => _ssh = ssh;

    public async Task<RemoteTextFileReadResult> ReadAsync(
        SshServerConfig server, string path, long? knownSize = null, CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(server, path, ct, includeExtended: true);
        if (snapshot is null)
            return RemoteTextFileReadResult.Fail("无法读取远端文件属性。");

        if (snapshot.IsSymbolicLink)
            return new RemoteTextFileReadResult { Symlink = true, Snapshot = snapshot, Message = "符号链接不在第一版在线编辑范围内。" };

        var size = knownSize is > 0 ? knownSize.Value : snapshot.Size;
        if (size > HardLimitBytes)
            return new RemoteTextFileReadResult { TooLarge = true, Snapshot = snapshot, Message = "文件超过在线编辑硬限制。" };

        var local = NewTempPath();
        try
        {
            var download = await _ssh.DownloadFileAsync(server, path, local, ct: ct);
            if (!download.Success)
                return RemoteTextFileReadResult.Fail(download.Message, snapshot);
            if (!File.Exists(local))
                return RemoteTextFileReadResult.Fail("下载后的临时文件不存在。", snapshot);

            var bytes = await File.ReadAllBytesAsync(local, ct);
            if (bytes.Length > HardLimitBytes)
                return new RemoteTextFileReadResult { TooLarge = true, Snapshot = snapshot, Message = "文件超过在线编辑硬限制。" };
            if (LooksBinary(bytes))
                return new RemoteTextFileReadResult { Binary = true, Snapshot = snapshot, Message = "检测到二进制内容，不能在线编辑。" };

            var decoded = Decode(bytes);
            return new RemoteTextFileReadResult
            {
                Success = true,
                Content = decoded.Text,
                Bytes = bytes,
                Encoding = decoded.Encoding,
                HasBom = decoded.HasBom,
                LineEnding = DetectLineEnding(decoded.Text),
                Snapshot = snapshot with { Sha256 = HashBytes(bytes) },
                Message = size > SoftLimitBytes ? "文件较大，已按只读体验加载。" : string.Empty
            };
        }
        catch (Exception ex)
        {
            return RemoteTextFileReadResult.Fail(ex.Message, snapshot);
        }
        finally
        {
            TryDelete(local);
        }
    }

    public async Task<RemoteTextFileSaveResult> SaveAsync(
        SshServerConfig server,
        string path,
        string content,
        Encoding encoding,
        bool hasBom,
        string lineEnding,
        RemoteTextFileSnapshot? expected,
        bool force,
        bool isNew,
        CancellationToken ct = default)
    {
        try
        {
            var current = await GetSnapshotAsync(server, path, ct, includeExtended: true);
            if (isNew && current is not null && !force)
            {
                return new RemoteTextFileSaveResult
                {
                    AlreadyExists = true,
                    Message = "目标文件已存在。",
                    Snapshot = current
                };
            }

            if (!isNew && expected is not null && current is not null && !force)
            {
                var versionChanged = !SameVersion(expected, current);
                if (!versionChanged && !string.IsNullOrWhiteSpace(expected.Sha256))
                {
                    var currentHash = await ComputeHashAsync(server, path, ct);
                    if (currentHash is not null)
                    {
                        current = current with { Sha256 = currentHash };
                        versionChanged = !string.Equals(expected.Sha256, currentHash, StringComparison.OrdinalIgnoreCase);
                    }
                }

                if (versionChanged)
                {
                    return new RemoteTextFileSaveResult
                    {
                        Conflict = true,
                        Message = "远端文件已被其他程序修改。",
                        Snapshot = current
                    };
                }
            }

            string? backupPath = null;
            if (!isNew)
                backupPath = await CreateBackupAsync(server, path, ct);

            var normalized = NormalizeLineEnding(content, lineEnding);
            var body = encoding.GetBytes(normalized);
            var preamble = hasBom ? encoding.GetPreamble() : Array.Empty<byte>();
            if (preamble.Length + body.Length > HardLimitBytes)
                return RemoteTextFileSaveResult.Blocked("内容超过在线编辑保存硬限制。", current);

            var local = NewTempPath();
            try
            {
                var bytes = new byte[preamble.Length + body.Length];
                Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
                Buffer.BlockCopy(body, 0, bytes, preamble.Length, body.Length);
                await File.WriteAllBytesAsync(local, bytes, ct);

                var upload = await _ssh.UploadFileAsync(server, local, path, ct: ct);
                if (!upload.Success)
                    return RemoteTextFileSaveResult.Blocked(upload.Message, current);

                var after = await GetSnapshotAsync(server, path, ct, includeExtended: true);
                if (after is not null)
                    after = after with { Sha256 = HashBytes(bytes) };
                var metadataChanged = expected is not null && after is not null &&
                                      MetadataDiffers(expected, after);
                if (metadataChanged && !isNew && expected is not null)
                {
                    var restored = await RestoreMetadataAsync(server, path, expected, ct);
                    after = await GetSnapshotAsync(server, path, ct, includeExtended: true);
                    var stillDifferent = after is null || MetadataDiffers(expected, after);
                    return new RemoteTextFileSaveResult
                    {
                        Success = true,
                        MetadataChanged = stillDifferent,
                        BackupPath = backupPath,
                        Snapshot = after,
                        Message = stillDifferent
                            ? "保存成功，但 ACL/xattr/SELinux 或权限未能完全恢复：" +
                              string.Join("；", restored.Failures.Take(3))
                            : "保存成功，已恢复 ACL/xattr/SELinux 与权限。"
                    };
                }
                return new RemoteTextFileSaveResult
                {
                    Success = true,
                    MetadataChanged = metadataChanged,
                    BackupPath = backupPath,
                    Snapshot = after,
                    Message = metadataChanged
                        ? "保存成功，但远端权限/属主发生变化，请检查文件属性。"
                        : "保存成功。"
                };
            }
            finally
            {
                TryDelete(local);
            }
        }
        catch (Exception ex)
        {
            return RemoteTextFileSaveResult.Blocked(ex.Message);
        }
    }

    public async Task<RemoteTextFileSnapshot?> GetSnapshotAsync(
        SshServerConfig server, string path, CancellationToken ct = default, bool includeExtended = false)
    {
        try
        {
            var parent = ParentOf(path);
            var name = BaseName(path);
            var list = await _ssh.ListRemoteFilesAsync(server, parent, ct);
            if (!list.Success)
                return null;
            var item = list.Files.FirstOrDefault(f =>
                string.Equals(f.Name, name, StringComparison.Ordinal) ||
                string.Equals(f.FullName, path, StringComparison.Ordinal));
            if (item is null)
                return null;

            var meta = includeExtended
                ? await ReadMetadataAsync(server, path, ct)
                : default;
            return new RemoteTextFileSnapshot(
                item.Size,
                item.LastModified,
                item.Permissions,
                item.Owner,
                item.IsSymbolicLink,
                meta.Mode,
                meta.Uid,
                meta.Gid,
                meta.Acl,
                meta.Xattrs,
                meta.Selinux);
        }
        catch
        {
            return null;
        }
    }

    private async Task<(string? Mode, uint? Uid, uint? Gid, string? Acl, string? Xattrs, string? Selinux)>
        ReadMetadataAsync(SshServerConfig server, string path, CancellationToken ct)
    {
        string? mode = null;
        uint? uid = null;
        uint? gid = null;
        string? acl = null;
        string? xattrs = null;
        string? selinux = null;

        try
        {
            var stat = await _ssh.ExecuteCommandAsync(server,
                $"stat -c '%a %u %g' -- {Quote(path)} 2>/dev/null", ct, 10);
            if (stat.Success && !string.IsNullOrWhiteSpace(stat.Output))
            {
                var parts = stat.Output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1) mode = parts[0];
                if (parts.Length >= 2 && uint.TryParse(parts[1], out var u)) uid = u;
                if (parts.Length >= 3 && uint.TryParse(parts[2], out var g)) gid = g;
            }
        }
        catch { }

        try
        {
            var result = await _ssh.ExecuteCommandAsync(server,
                $"getfacl -p -- {Quote(path)} 2>/dev/null", ct, 10);
            if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
                acl = result.Output.Trim();
        }
        catch { }

        try
        {
            var result = await _ssh.ExecuteCommandAsync(server,
                $"getfattr -d -m - -- {Quote(path)} 2>/dev/null", ct, 10);
            if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
                xattrs = result.Output.Trim();
        }
        catch { }

        if (string.Equals(server.Username, "root", StringComparison.Ordinal))
        {
            try
            {
                var result = await _ssh.ExecuteCommandAsync(server,
                    $"stat -c '%C' -- {Quote(path)} 2>/dev/null", ct, 10);
                if (result.Success && !string.IsNullOrWhiteSpace(result.Output) &&
                    !string.Equals(result.Output.Trim(), "?", StringComparison.Ordinal))
                    selinux = result.Output.Trim();
            }
            catch { }
        }

        return (mode, uid, gid, acl, xattrs, selinux);
    }

    private static string Quote(string path) => "'" + path.Replace("'", "'\\''") + "'";

    private static bool MetadataDiffers(RemoteTextFileSnapshot expected, RemoteTextFileSnapshot actual)
    {
        if (!string.Equals(expected.Permissions, actual.Permissions, StringComparison.Ordinal) ||
            !string.Equals(expected.Owner, actual.Owner, StringComparison.Ordinal))
            return true;
        if (expected.Mode is not null && !string.Equals(expected.Mode, actual.Mode, StringComparison.Ordinal))
            return true;
        if (expected.Uid is not null && expected.Uid != actual.Uid)
            return true;
        if (expected.Gid is not null && expected.Gid != actual.Gid)
            return true;
        if (expected.Acl is not null && !string.Equals(expected.Acl, actual.Acl, StringComparison.Ordinal))
            return true;
        if (expected.Xattrs is not null && !string.Equals(expected.Xattrs, actual.Xattrs, StringComparison.Ordinal))
            return true;
        if (expected.Selinux is not null && !string.Equals(expected.Selinux, actual.Selinux, StringComparison.Ordinal))
            return true;
        return false;
    }

    private async Task<(bool Attempted, List<string> Failures)> RestoreMetadataAsync(
        SshServerConfig server, string path, RemoteTextFileSnapshot expected, CancellationToken ct)
    {
        var failures = new List<string>();
        var attempted = false;

        async Task RunAsync(string command)
        {
            attempted = true;
            try
            {
                var result = await _ssh.ExecuteCommandAsync(server, command, ct, 15);
                if (!result.Success)
                    failures.Add($"{(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error).Trim()}");
            }
            catch (Exception ex)
            {
                failures.Add(ex.Message);
            }
        }

        if (!string.IsNullOrWhiteSpace(expected.Mode))
            await RunAsync($"chmod {expected.Mode} -- {Quote(path)} 2>/dev/null");

        if (string.Equals(server.Username, "root", StringComparison.Ordinal) &&
            expected.Uid is uint uid && expected.Gid is uint gid)
            await RunAsync($"chown {uid}:{gid} -- {Quote(path)} 2>/dev/null");

        if (!string.IsNullOrWhiteSpace(expected.Acl))
        {
            attempted = true;
            try
            {
                var result = await _ssh.ExecuteStreamingAsync(server,
                    "setfacl --restore=- 2>/dev/null", stdinText: expected.Acl, ct: ct, timeoutSeconds: 15);
                if (!result.Success)
                    failures.Add("ACL: " + (string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error).Trim());
            }
            catch (Exception ex)
            {
                failures.Add("ACL: " + ex.Message);
            }
        }

        if (!string.IsNullOrWhiteSpace(expected.Xattrs))
        {
            attempted = true;
            try
            {
                var result = await _ssh.ExecuteStreamingAsync(server,
                    "setfattr --restore=- 2>/dev/null", stdinText: expected.Xattrs, ct: ct, timeoutSeconds: 15);
                if (!result.Success)
                    failures.Add("xattr: " + (string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error).Trim());
            }
            catch (Exception ex)
            {
                failures.Add("xattr: " + ex.Message);
            }
        }

        if (string.Equals(server.Username, "root", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(expected.Selinux))
        {
            await RunAsync($"chcon {Quote(expected.Selinux)} -- {Quote(path)} 2>/dev/null");
        }

        return (attempted, failures);
    }

    public async Task<string?> CreateBackupAsync(
        SshServerConfig server, string path, CancellationToken ct = default)
    {
        try
        {
            var dir = BackupDirShell(path);
            var safe = new string(BaseName(path)
                .Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_')
                .ToArray());
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var command =
                $"dir=\"{dir}\"; mkdir -p \"$dir\"; " +
                $"name=\"{stamp}-{safe}\"; cp -p -- {Quote(path)} \"$dir/$name\" && printf '%s' \"$dir/$name\"";
            var result = await _ssh.ExecuteCommandAsync(server, command, ct, 20);
            return result.Success ? result.Output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> ListBackupsAsync(
        SshServerConfig server, string path, CancellationToken ct = default)
    {
        try
        {
            var dir = BackupDirShell(path);
            var command =
                $"dir=\"{dir}\"; [ -d \"$dir\" ] || exit 0; " +
                "for f in \"$dir\"/*; do [ -f \"$f\" ] && printf '%s\\n' \"$f\"; done";
            var result = await _ssh.ExecuteCommandAsync(server, command, ct, 15);
            if (!result.Success)
                return Array.Empty<string>();
            return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public Task<CommandResult> RestoreBackupAsync(
        SshServerConfig server, string backupPath, string path, CancellationToken ct = default)
        => _ssh.ExecuteCommandAsync(server,
            $"cp -p -- {Quote(backupPath)} {Quote(path)}", ct, 20);

    private static string BackupDirShell(string path)
        => "$HOME/.litssh-backups/" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant()[..16];

    private static string HashBytes(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private async Task<string?> ComputeHashAsync(
        SshServerConfig server, string path, CancellationToken ct)
    {
        var local = NewTempPath();
        try
        {
            var result = await _ssh.DownloadFileAsync(server, path, local, ct: ct);
            if (!result.Success || !File.Exists(local))
                return null;
            return HashBytes(await File.ReadAllBytesAsync(local, ct));
        }
        catch
        {
            return null;
        }
        finally
        {
            TryDelete(local);
        }
    }

    public static string DecodeBytes(byte[] bytes, Encoding encoding)
    {
        var offset = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF &&
            encoding.CodePage == Encoding.UTF8.CodePage)
            offset = 3;
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE &&
                 encoding.CodePage is 1200 or 1201)
            offset = 2;
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF &&
                 encoding.CodePage is 1200 or 1201)
            offset = 2;
        return encoding.GetString(bytes, offset, bytes.Length - offset);
    }

    public static Encoding EncodingForName(string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return name switch
        {
            "gb18030" => Encoding.GetEncoding(54936),
            "gbk" => Encoding.GetEncoding(936),
            "utf-8 bom" => new UTF8Encoding(true),
            "utf-8" => new UTF8Encoding(false),
            "utf-16 le" => new UnicodeEncoding(false, true),
            "latin1" => Encoding.Latin1,
            _ => new UTF8Encoding(false)
        };
    }

    public static string NormalizeLineEnding(string content, string lineEnding)
    {
        var text = content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return lineEnding switch
        {
            "CRLF" => text.Replace("\n", "\r\n", StringComparison.Ordinal),
            "CR" => text.Replace("\n", "\r", StringComparison.Ordinal),
            _ => text
        };
    }

    private static bool SameVersion(RemoteTextFileSnapshot a, RemoteTextFileSnapshot b)
        => a.Size == b.Size && a.LastModified == b.LastModified;

    private static bool LooksBinary(byte[] bytes)
    {
        if (bytes.Length == 0)
            return false;
        if (bytes.Length >= 2 &&
            ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            return false;
        var control = 0;
        foreach (var b in bytes)
        {
            if (b == 0)
                return true;
            if (b < 0x09 || (b > 0x0D && b < 0x20))
                control++;
        }
        return control > Math.Max(32, bytes.Length / 100);
    }

    private static (string Text, Encoding Encoding, bool HasBom) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(false), true);

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(false, true), true);

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(true, true), true);

        try
        {
            var strict = new UTF8Encoding(false, true);
            return (strict.GetString(bytes), new UTF8Encoding(false), false);
        }
        catch (DecoderFallbackException)
        {
            // 继续尝试中文服务器常见编码
        }

        try
        {
            var gb18030 = Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            return (gb18030.GetString(bytes), Encoding.GetEncoding(54936), false);
        }
        catch (DecoderFallbackException)
        {
        }

        return (Encoding.Latin1.GetString(bytes), Encoding.Latin1, false);
    }

    private static string DetectLineEnding(string text)
    {
        var crlf = 0;
        var lf = 0;
        var cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (text[i] == '\n')
            {
                lf++;
            }
        }
        return crlf >= lf && crlf >= cr ? "CRLF" : cr > lf ? "CR" : "LF";
    }

    private static string NewTempPath()
        => Path.Combine(Path.GetTempPath(), "litssh-editor-" + Guid.NewGuid().ToString("N") + ".tmp");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* ignore */ }
    }

    private static string BaseName(string path)
    {
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }

    private static string ParentOf(string path)
    {
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index <= 0 ? "/" : trimmed[..index];
    }
}
