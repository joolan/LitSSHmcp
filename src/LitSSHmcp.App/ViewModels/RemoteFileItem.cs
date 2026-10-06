namespace LitSSHmcp.App.ViewModels;

/// <summary>远程文件/目录项。</summary>
public sealed class RemoteFileItem
{
    public required string Name { get; init; }
    public required string FullName { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsSymbolicLink { get; init; }
    public long Size { get; init; }
    public DateTime LastModified { get; init; }
    public string Permissions { get; init; } = string.Empty;
    public string Owner { get; init; } = string.Empty;

    public string Glyph => IsDirectory ? "📁" : "📄";

    public string SizeText => IsDirectory ? string.Empty : FormatSize(Size);

    public string ModifiedText => LastModified == default ? string.Empty : LastModified.ToString("yyyy-MM-dd HH:mm");

    public string OwnerText => string.IsNullOrEmpty(Owner) ? Permissions : $"{Permissions} {Owner}";

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.#") + " MB";
        return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.#") + " GB";
    }
}
