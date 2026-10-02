using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Core.Services.Topology;

public interface ITopologyLayoutStore
{
    TopologyLayout Load();
    void Save(TopologyLayout layout);
    void Clear();
    string Path { get; }
}

/// <summary>手动拓扑布局的读写（JSON 文件，位于 %APPDATA%\LitSSH\topology-layout.json）。</summary>
public class TopologyLayoutStore : ITopologyLayoutStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();

    public TopologyLayoutStore() : this(ConfigPaths.TopologyLayoutFile)
    {
    }

    public TopologyLayoutStore(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public TopologyLayout Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(Path))
                    return JsonSerializer.Deserialize<TopologyLayout>(File.ReadAllText(Path), Json) ?? new TopologyLayout();
            }
            catch
            {
                // 损坏则以空布局开始
            }

            return new TopologyLayout();
        }
    }

    public void Save(TopologyLayout layout)
    {
        lock (_gate)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var tmp = Path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(layout, Json));
                File.Move(tmp, Path, overwrite: true);
            }
            catch
            {
                // 持久化失败不阻断界面
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try { if (File.Exists(Path)) File.Delete(Path); } catch { /* ignore */ }
        }
    }
}
