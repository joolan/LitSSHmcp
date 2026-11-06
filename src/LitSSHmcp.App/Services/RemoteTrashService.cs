using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.App.Services;

public sealed class RemoteTrashItem
{
    public string Id { get; init; } = string.Empty;
    public string OriginalPath { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTimeOffset DeletedAt { get; init; }

    public string TypeText => IsDirectory ? "文件夹" : "文件";
    public string SizeText => FormatSize(Size);
    public string DeletedAtText => DeletedAt == DateTimeOffset.MinValue
        ? "未知"
        : DeletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string AgeText => DeletedAt == DateTimeOffset.MinValue
        ? "未知"
        : FormatAge(DateTimeOffset.Now - DeletedAt);

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.##} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.##} MB",
        >= 1024L => $"{bytes / 1024d:0.##} KB",
        _ => $"{bytes} B"
    };

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        if (age.TotalDays >= 1)
            return $"{(int)age.TotalDays} 天";
        if (age.TotalHours >= 1)
            return $"{(int)age.TotalHours} 小时";
        if (age.TotalMinutes >= 1)
            return $"{(int)age.TotalMinutes} 分钟";
        return "刚刚";
    }
}

public sealed record RemoteTrashMoveResult(int Moved, IReadOnlyList<string> Errors);

public sealed record RemoteTrashRestoreResult(bool Success, string Message, string? RestoredPath = null);

/// <summary>
/// 远程回收站：删除时 mv 到 ~/.litssh-trash，不自动清理，不做 rm 降级。
/// </summary>
public sealed class RemoteTrashService
{
    private static readonly Regex SafeId = new("^[A-Za-z0-9-]+$", RegexOptions.Compiled);

    private readonly SshServerConfig _server;
    private readonly ISshService _ssh;

    public RemoteTrashService(SshServerConfig server, ISshService ssh)
    {
        _server = server;
        _ssh = ssh;
    }

    public async Task<RemoteTrashMoveResult> MoveToTrashAsync(
        IReadOnlyList<RemoteFileItem> items, CancellationToken ct = default)
    {
        var moved = 0;
        var errors = new List<string>();
        foreach (var item in items)
        {
            var id = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) +
                     "-" + Guid.NewGuid().ToString("N")[..12];
            var command = BuildMoveCommand(item, id);
            var result = await _ssh.ExecuteCommandAsync(_server, command, ct, timeoutSeconds: 300);
            if (result.Success)
            {
                moved++;
                continue;
            }

            var error = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
            errors.Add($"{item.Name}: {(string.IsNullOrWhiteSpace(error) ? "移入回收站失败" : error.Trim())}");
        }
        return new RemoteTrashMoveResult(moved, errors);
    }

    public async Task<IReadOnlyList<RemoteTrashItem>> ListAsync(CancellationToken ct = default)
    {
        var command =
            "ROOT=\"$HOME/.litssh-trash\"; [ -d \"$ROOT/info\" ] || exit 0; " +
            "for f in \"$ROOT\"/info/*.trashinfo; do " +
            "[ -f \"$f\" ] || continue; " +
            "id=${f##*/}; id=${id%.trashinfo}; " +
            "printf '##TRASH:%s\\n' \"$id\"; cat -- \"$f\"; printf '##END\\n'; done";
        var result = await _ssh.ExecuteCommandAsync(_server, command, ct, timeoutSeconds: 60);
        if (!result.Success)
            throw new InvalidOperationException("读取回收站失败: " + ErrorOf(result));
        return ParseList(result.Output);
    }

    public async Task<RemoteTrashRestoreResult> RestoreAsync(RemoteTrashItem item,
        CancellationToken ct = default)
    {
        if (!SafeId.IsMatch(item.Id))
            return new RemoteTrashRestoreResult(false, "回收站条目 ID 无效。");
        var suffix = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var command =
            "ROOT=\"$HOME/.litssh-trash\"; SRC=\"$ROOT/files/" + item.Id + "\"; " +
            "INFO=\"$ROOT/info/" + item.Id + ".trashinfo\"; DST=" + Quote(item.OriginalPath) + "; " +
            "[ -e \"$SRC\" ] || { printf '##ERROR:回收站文件不存在\\n'; exit 4; }; " +
            "DIR=$(dirname -- \"$DST\"); BASE=$(basename -- \"$DST\"); " +
            "if [ -e \"$DST\" ] || [ -L \"$DST\" ]; then " +
            "  N=1; DST=\"$DIR/${BASE}.restored-" + suffix + "\"; " +
            "  while [ -e \"$DST\" ] || [ -L \"$DST\" ]; do " +
            "    N=$((N+1)); DST=\"$DIR/${BASE}.restored-" + suffix + "-$N\"; done; " +
            "fi; " +
            "[ -d \"$DIR\" ] || { printf '##ERROR:原目录不存在: %s\\n' \"$DIR\"; exit 5; }; " +
            "mv -- \"$SRC\" \"$DST\" || { printf '##ERROR:恢复失败\\n'; exit 6; }; " +
            "rm -f -- \"$INFO\"; printf '##RESTORED:%s\\n' \"$DST\"";
        var result = await _ssh.ExecuteCommandAsync(_server, command, ct, timeoutSeconds: 300);
        var error = ExtractMarked(result.Output, "##ERROR:");
        if (error is not null)
            return new RemoteTrashRestoreResult(false, error);
        if (!result.Success)
            return new RemoteTrashRestoreResult(false, ErrorOf(result));

        var restored = ExtractMarked(result.Output, "##RESTORED:");
        return restored is null
            ? new RemoteTrashRestoreResult(true, "已恢复。")
            : new RemoteTrashRestoreResult(true, "已恢复到: " + restored, restored);
    }

    public async Task DeletePermanentlyAsync(RemoteTrashItem item, CancellationToken ct = default)
    {
        if (!SafeId.IsMatch(item.Id))
            throw new InvalidOperationException("回收站条目 ID 无效。");
        var command =
            "ROOT=\"$HOME/.litssh-trash\"; " +
            "rm -rf -- \"$ROOT/files/" + item.Id + "\"; " +
            "rm -f -- \"$ROOT/info/" + item.Id + ".trashinfo\"";
        var result = await _ssh.ExecuteCommandAsync(_server, command, ct, timeoutSeconds: 120);
        if (!result.Success)
            throw new InvalidOperationException("彻底删除失败: " + ErrorOf(result));
    }

    public async Task EmptyAsync(CancellationToken ct = default)
    {
        var command =
            "ROOT=\"$HOME/.litssh-trash\"; " +
            "[ -d \"$ROOT\" ] || exit 0; " +
            "rm -rf -- \"$ROOT/files\" \"$ROOT/info\"; " +
            "mkdir -p -- \"$ROOT/files\" \"$ROOT/info\"; " +
            "chmod 700 -- \"$ROOT\" \"$ROOT/files\" \"$ROOT/info\"";
        var result = await _ssh.ExecuteCommandAsync(_server, command, ct, timeoutSeconds: 300);
        if (!result.Success)
            throw new InvalidOperationException("清空回收站失败: " + ErrorOf(result));
    }

    private string BuildMoveCommand(RemoteFileItem item, string id)
    {
        var path = Quote(item.FullName);
        var pathB64 = Base64(item.FullName);
        var nameB64 = Base64(item.Name);
        var type = item.IsDirectory ? "dir" : "file";
        var deletedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        return
            "ROOT=\"$HOME/.litssh-trash\"; " +
            "mkdir -p -- \"$ROOT/files\" \"$ROOT/info\" && " +
            "chmod 700 -- \"$ROOT\" \"$ROOT/files\" \"$ROOT/info\" || exit 1; " +
            "SIZE=$(du -sb -- " + path + " 2>/dev/null | cut -f1); SIZE=${SIZE:-0}; " +
            "mv -- " + path + " \"$ROOT/files/" + id + "\" || exit 2; " +
            "printf '%s\\n' 'Version=1' 'PathB64=" + pathB64 + "' 'NameB64=" + nameB64 +
            "' 'Type=" + type + "' \"Size=$SIZE\" 'DeletionDate=" + deletedAt +
            "' > \"$ROOT/info/" + id + ".trashinfo\" || { " +
            "mv -- \"$ROOT/files/" + id + "\" " + path + "; exit 3; }";
    }

    private static List<RemoteTrashItem> ParseList(string output)
    {
        var items = new List<RemoteTrashItem>();
        Dictionary<string, string>? current = null;
        var currentId = string.Empty;
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("##TRASH:", StringComparison.Ordinal))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                currentId = line["##TRASH:".Length..].Trim();
                continue;
            }
            if (line == "##END")
            {
                if (current is not null && SafeId.IsMatch(currentId))
                    items.Add(ToItem(currentId, current));
                current = null;
                continue;
            }
            if (current is null)
                continue;
            var index = line.IndexOf('=');
            if (index > 0)
                current[line[..index]] = line[(index + 1)..];
        }
        return items.OrderByDescending(item => item.DeletedAt).ToList();
    }

    private static RemoteTrashItem ToItem(string id, IReadOnlyDictionary<string, string> values)
    {
        var path = Decode(values.GetValueOrDefault("PathB64"));
        var name = Decode(values.GetValueOrDefault("NameB64"));
        _ = long.TryParse(values.GetValueOrDefault("Size"), out var size);
        _ = DateTimeOffset.TryParse(values.GetValueOrDefault("DeletionDate"),
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var deletedAt);
        return new RemoteTrashItem
        {
            Id = id,
            OriginalPath = path,
            Name = string.IsNullOrEmpty(name) ? BaseName(path) : name,
            IsDirectory = string.Equals(values.GetValueOrDefault("Type"), "dir",
                StringComparison.OrdinalIgnoreCase),
            Size = Math.Max(0, size),
            DeletedAt = deletedAt
        };
    }

    private static string? ExtractMarked(string output, string marker)
    {
        var line = output.Replace("\r\n", "\n").Split('\n')
            .FirstOrDefault(value => value.StartsWith(marker, StringComparison.Ordinal));
        return line is null ? null : line[marker.Length..].Trim();
    }

    private static string ErrorOf(CommandResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        return string.IsNullOrWhiteSpace(text) ? "远程命令执行失败。" : text.Trim();
    }

    private static string Base64(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
        catch { return string.Empty; }
    }

    private static string BaseName(string path)
    {
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }

    private static string Quote(string path) => "'" + path.Replace("'", "'\\''") + "'";
}
