using System.IO;
using System.Security.Cryptography;
using System.Text;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Scheduling;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.ViewModels;

public sealed class ScriptEditContext
{
    public required ScheduledTaskInfo Task { get; init; }
    public required string FilePath { get; init; }
    public required string Content { get; init; }
    public required string Meta { get; init; }
    public required string Interpreter { get; init; }
    public required Encoding Encoding { get; init; }
    public required Func<string, Task<ScriptSaveResult>> SaveAsync { get; init; }
}

public sealed record ScriptSaveResult(bool Success, string Message);

public sealed partial class ScheduledTasksViewModel
{
    public async Task<ScriptEditContext?> CreateScriptEditContextAsync()
    {
        var task = SelectedTask;
        if (task?.ScriptPath is null)
        {
            return null;
        }

        if (_scriptContent is null)
        {
            await LoadSelectedScriptAsync(force: true);
        }

        if (!ReferenceEquals(task, SelectedTask))
        {
            return null;
        }

        var content = _scriptContent;
        if (content is null || _scriptIsBinary)
        {
            _dialogs.Warn("脚本内容不可编辑：" + (ScriptMetaText.Length > 0 ? ScriptMetaText : "读取失败"), "编辑脚本");
            return null;
        }

        return new ScriptEditContext
        {
            Task = task,
            FilePath = task.ScriptPath,
            Content = content,
            Meta = ScriptMetaText,
            Interpreter = ScriptInterpreterText,
            Encoding = _scriptEncoding,
            SaveAsync = text => SaveScriptAsync(task, text)
        };
    }

    private async Task LoadSelectedScriptAsync(bool force = false)
    {
        _scriptCts?.Cancel();
        _scriptCts?.Dispose();
        var cts = new CancellationTokenSource();
        _scriptCts = cts;

        ResetScriptState();
        var task = SelectedTask;
        if (task?.ScriptPath is null)
        {
            return;
        }

        IsScriptLoading = true;
        try
        {
            var result = await ReadScriptAsync(task, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(task, SelectedTask))
            {
                return;
            }

            if (!result.Success)
            {
                ScriptMetaText = "读取失败：" + result.Error;
                ScriptPreview = result.Error;
                return;
            }

            ScriptMetaText = result.Meta;
            ScriptInterpreterText = result.Interpreter;
            _scriptContent = result.Content;
            _scriptOriginalBytes = result.Bytes;
            _scriptEncoding = result.Encoding;
            _scriptIsBinary = result.IsBinary;
            OnPropertyChanged(nameof(ScriptIsBinary));
            ScriptPreview = result.Preview;
            ScriptSyntaxText = result.IsBinary ? "二进制文件，不支持编辑" : "保存时会进行语法检查";
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
            {
                ScriptMetaText = "读取失败：" + ex.Message;
                ScriptPreview = ex.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_scriptCts, cts))
            {
                IsScriptLoading = false;
            }
        }
    }

    private void ResetScriptState()
    {
        _scriptContent = null;
        _scriptOriginalBytes = null;
        _scriptEncoding = new UTF8Encoding(false);
        _scriptIsBinary = false;
        ScriptMetaText = string.Empty;
        ScriptInterpreterText = string.Empty;
        ScriptPreview = string.Empty;
        ScriptSyntaxText = string.Empty;
        OnPropertyChanged(nameof(ScriptIsBinary));
    }

    private async Task<ScriptReadResult> ReadScriptAsync(ScheduledTaskInfo task, CancellationToken ct)
    {
        var path = task.ScriptPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return ScriptReadResult.Fail("未识别到脚本路径");
        }

        var quoted = ShellQuote.Single(path);
        var stat = await ExecuteAsync($"stat -c '%s|%a|%U|%G|%Y' -- {quoted} 2>&1", 30, ct);
        if (!stat.Success)
        {
            return ScriptReadResult.Fail(string.IsNullOrWhiteSpace(stat.Error) ? stat.Output : stat.Error);
        }

        var statParts = stat.Output.Trim().Split('|');
        if (statParts.Length < 5 || !long.TryParse(statParts[0], out var size))
        {
            return ScriptReadResult.Fail("无法读取脚本属性");
        }

        var mode = statParts[1];
        var owner = statParts[2];
        var group = statParts[3];
        var serverOffset = _data?.ServerNow.Offset ?? TimeSpan.Zero;
        var modified = long.TryParse(statParts[4], out var unix) && unix > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unix).ToOffset(serverOffset).ToString("yyyy-MM-dd HH:mm")
            : "未知";
        var meta = $"{owner}:{group} · 权限 {mode} · {FormatSize(size)} · 修改 {modified}";

        if (size > 2 * 1024 * 1024)
        {
            return ScriptReadResult.Fail($"脚本过大（{FormatSize(size)}），仅支持查看 2 MB 以内的文件");
        }

        var contentResult = await ExecuteAsync(
            $"base64 -w 0 -- {quoted} 2>/dev/null || base64 {quoted} 2>/dev/null", 60, ct);
        byte[] bytes;
        if (contentResult.Success && contentResult.Output.Trim().Length > 0)
        {
            try
            {
                var base64 = string.Concat(contentResult.Output.Where(c => !char.IsWhiteSpace(c)));
                bytes = Convert.FromBase64String(base64);
            }
            catch
            {
                var cat = await ExecuteAsync($"cat -- {quoted}", 60, ct);
                bytes = Encoding.UTF8.GetBytes(cat.Output);
            }
        }
        else if (size == 0)
        {
            bytes = Array.Empty<byte>();
        }
        else
        {
            var cat = await ExecuteAsync($"cat -- {quoted}", 60, ct);
            if (!cat.Success)
            {
                return ScriptReadResult.Fail(string.IsNullOrWhiteSpace(cat.Error) ? cat.Output : cat.Error);
            }

            bytes = Encoding.UTF8.GetBytes(cat.Output);
        }

        var (encoding, bomLength) = DetectEncoding(bytes);
        var isBinary = encoding is not UnicodeEncoding &&
                       bytes.AsSpan(Math.Min(bomLength, bytes.Length)).Contains((byte)0);
        var content = isBinary
            ? string.Empty
            : encoding.GetString(bytes, Math.Min(bomLength, bytes.Length),
                Math.Max(0, bytes.Length - Math.Min(bomLength, bytes.Length)));
        var interpreter = DetectInterpreter(task.ScriptInterpreter, path, content);

        return new ScriptReadResult
        {
            Success = true,
            Meta = meta,
            Content = content,
            Bytes = bytes,
            Encoding = encoding,
            BomLength = bomLength,
            IsBinary = isBinary,
            Preview = isBinary ? "（二进制文件，无法预览）" : TruncatePreview(content),
            Interpreter = interpreter
        };
    }

    private async Task<(bool Success, string Message)> ValidateSyntaxAsync(ScheduledTaskInfo task, string text)
    {
        var interpreter = DetectInterpreter(task.ScriptInterpreter, task.ScriptPath ?? string.Empty, text);
        var name = Path.GetFileName(interpreter).ToLowerInvariant();
        if (name is not ("bash" or "sh" or "dash" or "zsh" or "ksh"))
        {
            return (true, "非 shell 脚本，跳过语法检查");
        }

        var temp = await UploadTempTextAsync(text);
        if (temp is null)
        {
            return (true, "无法上传临时文件，跳过语法检查");
        }

        try
        {
            var shell = name switch
            {
                "bash" => "bash",
                "zsh" => "zsh",
                "ksh" => "ksh",
                "dash" => "dash",
                _ => "sh"
            };
            var result = await ExecuteAsync($"{shell} -n -- {ShellQuote.Single(temp)} 2>&1", 30);
            if (result.Success)
            {
                return (true, "语法检查通过");
            }

            var message = string.IsNullOrWhiteSpace(result.Output) ? result.Error : result.Output;
            return (false, message.Trim());
        }
        finally
        {
            await DeleteRemoteTempAsync(temp);
        }
    }

    private async Task<ScriptSaveResult> SaveScriptAsync(ScheduledTaskInfo task, string text)
    {
        var path = task.ScriptPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return new ScriptSaveResult(false, "未识别到脚本路径");
        }

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (string.Equals(normalized, _scriptContent, StringComparison.Ordinal))
        {
            return new ScriptSaveResult(true, "内容未变化");
        }

        var syntax = await ValidateSyntaxAsync(task, normalized);
        if (!syntax.Success)
        {
            var confirm = _dialogs.Confirm(
                $"脚本语法检查失败：\n\n{syntax.Message}\n\n仍然保存？",
                "脚本语法检查",
                warning: true);
            if (!confirm)
            {
                return new ScriptSaveResult(false, "已取消保存");
            }
        }

        var backupPath = WriteLocalBackup(path, _scriptOriginalBytes);
        var encoded = _scriptEncoding.GetBytes(normalized);
        var preamble = _scriptEncoding.GetPreamble();
        var bytes = preamble.Length == 0 ? encoded : preamble.Concat(encoded).ToArray();

        var temp = await UploadTempBytesAsync(bytes);
        if (temp is null)
        {
            return new ScriptSaveResult(false, "上传临时文件失败");
        }

        try
        {
            var result = await ExecuteAsync(
                $"cat -- {ShellQuote.Single(temp)} > {ShellQuote.Single(path)} 2>&1", 60);
            if (!result.Success)
            {
                return new ScriptSaveResult(false, string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
            }

            var verify = await VerifyRemoteHashAsync(path, bytes);
            if (!verify.Success)
            {
                return new ScriptSaveResult(false, verify.Message);
            }

            _scriptContent = normalized;
            _scriptOriginalBytes = bytes;
            ScriptPreview = TruncatePreview(normalized);
            ScriptSyntaxText = syntax.Success ? "语法检查通过" : "语法检查失败（已强制保存）";
            await AuditAsync("scheduled_task_script_save",
                $"write {path}", true,
                $"大小 {bytes.Length} 字节；备份 {backupPath}", path, bytes.Length);
            return new ScriptSaveResult(true, "保存成功" + (backupPath.Length > 0 ? "，原文件已备份" : string.Empty));
        }
        catch (Exception ex)
        {
            return new ScriptSaveResult(false, ex.Message);
        }
        finally
        {
            await DeleteRemoteTempAsync(temp);
        }
    }

    private static string DetectInterpreter(string? knownInterpreter, string path, string content)
    {
        if (!string.IsNullOrWhiteSpace(knownInterpreter))
        {
            return knownInterpreter;
        }

        if (content.StartsWith("#!", StringComparison.Ordinal))
        {
            var firstLine = content.Split('\n')[0][2..].Trim();
            var parts = firstLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                var name = Path.GetFileName(parts[0]);
                if (name.Equals("env", StringComparison.OrdinalIgnoreCase) && parts.Length > 1)
                {
                    return Path.GetFileName(parts[1]);
                }

                return name;
            }
        }

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".sh" => "bash",
            ".bash" => "bash",
            ".py" => "python3",
            ".pl" => "perl",
            ".rb" => "ruby",
            ".php" => "php",
            ".js" => "node",
            ".ps1" => "pwsh",
            _ => string.Empty
        };
    }

    private static (Encoding Encoding, int BomLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (new UTF8Encoding(true), 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (Encoding.Unicode, 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (Encoding.BigEndianUnicode, 2);
        }

        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            return (new UTF8Encoding(false), 0);
        }
        catch
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return (Encoding.GetEncoding(936), 0);
            }
            catch
            {
                return (Encoding.Latin1, 0);
            }
        }
    }

    private async Task<string?> UploadTempBytesAsync(byte[] bytes)
    {
        var local = Path.Combine(Path.GetTempPath(), "litssh-script-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(local, bytes);
            var remote = "/tmp/litssh-script-" + Guid.NewGuid().ToString("N") + ".tmp";
            var result = await _ssh.UploadFileAsync(_server, local, remote);
            return result.Success ? remote : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { File.Delete(local); } catch { }
        }
    }

    private async Task<(bool Success, string Message)> VerifyRemoteHashAsync(string path, byte[] expected)
    {
        var localHash = Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant();
        var result = await ExecuteAsync($"sha256sum -- {ShellQuote.Single(path)} 2>/dev/null", 30);
        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
        {
            return (true, "已写入（服务器无 sha256sum，跳过校验）");
        }

        var remoteHash = result.Output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.Equals(remoteHash, localHash, StringComparison.OrdinalIgnoreCase)
            ? (true, "校验通过")
            : (false, "写入后校验失败，服务器文件内容与本地不一致");
    }

    private static string WriteLocalBackup(string path, byte[]? bytes)
    {
        if (bytes is null)
        {
            return string.Empty;
        }

        try
        {
            var dir = Path.Combine(ConfigPaths.AppDataDir, "scheduled-task-backups");
            Directory.CreateDirectory(dir);
            var name = $"{DateTime.Now:yyyyMMdd-HHmmss}-{SafeFileName(Path.GetFileName(path))}";
            var file = Path.Combine(dir, name);
            File.WriteAllBytes(file, bytes);
            return file;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string WriteLocalTextBackup(string source, string text)
    {
        try
        {
            var dir = Path.Combine(ConfigPaths.AppDataDir, "scheduled-task-backups");
            Directory.CreateDirectory(dir);
            var name = $"{DateTime.Now:yyyyMMdd-HHmmss}-{SafeFileName(Path.GetFileName(source))}.bak";
            var file = Path.Combine(dir, name);
            File.WriteAllText(file, text, new UTF8Encoding(false));
            return file;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name;
    }

    private static string TruncatePreview(string content)
    {
        const int maxLines = 300;
        const int maxChars = 30000;
        var lines = content.Split('\n');
        var preview = lines.Length <= maxLines ? content : string.Join('\n', lines.Take(maxLines));
        if (preview.Length > maxChars)
        {
            preview = preview[..maxChars];
        }

        return lines.Length > maxLines
            ? preview + $"\n\n… 已截断，共 {lines.Length} 行"
            : preview;
    }

    private static string FormatSize(long size) => size switch
    {
        < 1024 => $"{size} B",
        < 1024 * 1024 => $"{size / 1024.0:0.0} KB",
        _ => $"{size / 1024.0 / 1024.0:0.0} MB"
    };

    private sealed class ScriptReadResult
    {
        public bool Success { get; init; }
        public string Error { get; init; } = string.Empty;
        public string Meta { get; init; } = string.Empty;
        public string Content { get; init; } = string.Empty;
        public byte[] Bytes { get; init; } = Array.Empty<byte>();
        public Encoding Encoding { get; init; } = new UTF8Encoding(false);
        public int BomLength { get; init; }
        public bool IsBinary { get; init; }
        public string Preview { get; init; } = string.Empty;
        public string Interpreter { get; init; } = string.Empty;

        public static ScriptReadResult Fail(string error) => new()
        {
            Success = false,
            Error = error
        };
    }
}
