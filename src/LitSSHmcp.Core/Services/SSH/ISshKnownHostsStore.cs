using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Core.Services.SSH;

/// <summary>已知 SSH 主机密钥（TOFU）存储。</summary>
public interface ISshKnownHostsStore
{
    SshKnownHost? Find(string host, int port);
    IReadOnlyList<SshKnownHost> GetAll();
    void Save(SshKnownHost entry);
    bool Remove(string host, int port);
}

public sealed class FileSshKnownHostsStore : ISshKnownHostsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, SshKnownHost>? _entries;

    public FileSshKnownHostsStore() : this(ConfigPaths.KnownHostsFile)
    {
    }

    public FileSshKnownHostsStore(string path)
    {
        _path = path;
    }

    public SshKnownHost? Find(string host, int port)
    {
        lock (_gate)
        {
            return Load().TryGetValue(Key(host, port), out var entry) ? entry : null;
        }
    }

    public IReadOnlyList<SshKnownHost> GetAll()
    {
        lock (_gate)
        {
            return Load().Values.OrderBy(e => e.Host).ThenBy(e => e.Port).ToArray();
        }
    }

    public void Save(SshKnownHost entry)
    {
        lock (_gate)
        {
            Load()[Key(entry.Host, entry.Port)] = entry;
            Persist();
        }
    }

    public bool Remove(string host, int port)
    {
        lock (_gate)
        {
            var removed = Load().Remove(Key(host, port));
            if (removed)
                Persist();
            return removed;
        }
    }

    private static string Key(string host, int port) => $"{host}:{port}";

    private Dictionary<string, SshKnownHost> Load()
    {
        if (_entries != null)
            return _entries;

        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var list = JsonSerializer.Deserialize<SshKnownHost[]>(json, Json) ?? Array.Empty<SshKnownHost>();
                _entries = list.ToDictionary(e => Key(e.Host, e.Port), e => e);
                return _entries;
            }
        }
        catch
        {
            // 文件损坏时以空集开始
        }

        _entries = new Dictionary<string, SshKnownHost>();
        return _entries;
    }

    private void Persist()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(Load().Values.ToArray(), Json);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // 持久化失败不阻断连接流程
        }
    }
}
