using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LitSSHmcp.App.ViewModels;

/// <summary>
/// Redis 键浏览器的一行：键名必有，类型 / TTL / 大小按需补全（批量 TYPE+TTL+MEMORY USAGE）。
/// 补全前显示 "—"，避免打开即对上千个键发起数千次往返。
/// </summary>
public sealed class RedisKeyRow : INotifyPropertyChanged
{
    private string? _type;
    private long? _ttlSeconds;
    private long? _sizeBytes;

    public RedisKeyRow(string key)
    {
        Key = key;
    }

    public string Key { get; }

    /// <summary>键类型（string/hash/list/set/zset/stream/none）；null = 未补全。</summary>
    public string? Type
    {
        get => _type;
        set
        {
            if (!Set(ref _type, value)) return;
            OnPropertyChanged(nameof(TypeDisplay));
            OnPropertyChanged(nameof(MetaLoaded));
        }
    }

    /// <summary>生存时间（秒）：-1 永不过期，-2 键不存在；null = 未补全。</summary>
    public long? TtlSeconds
    {
        get => _ttlSeconds;
        set { if (Set(ref _ttlSeconds, value)) OnPropertyChanged(nameof(TtlDisplay)); }
    }

    /// <summary>内存占用字节；null = 未补全或服务端不支持 MEMORY USAGE。</summary>
    public long? SizeBytes
    {
        get => _sizeBytes;
        set { if (Set(ref _sizeBytes, value)) OnPropertyChanged(nameof(SizeDisplay)); }
    }

    public bool MetaLoaded => _type is not null;

    public string TypeDisplay => _type ?? "—";

    public string TtlDisplay => _ttlSeconds switch
    {
        null => "—",
        -1 => "永不过期",
        -2 => "—",
        var seconds => $"{seconds} s"
    };

    public string SizeDisplay => _sizeBytes is null ? "—" : FormatSize(_sizeBytes.Value);

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1073741824L => $"{bytes / 1073741824d:0.##} GB",
        >= 1048576L => $"{bytes / 1048576d:0.##} MB",
        >= 1024L => $"{bytes / 1024d:0.##} KB",
        _ => $"{bytes} B"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
