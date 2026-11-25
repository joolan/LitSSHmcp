using System.IO;
using System.Text.Json;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Core.Services.DnsServer;

/// <summary>
/// dns-server.json 的原子读写：临时文件落盘后再替换，并保留 .bak。
/// 文件损坏时回退备份，避免 DNS 功能因配置损坏而无法打开。
/// </summary>
public sealed class DnsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly string _backupPath;

    public DnsStore(string? path = null)
    {
        _path = path ?? ConfigPaths.DnsConfigFile;
        _backupPath = _path + ".bak";
    }

    public string Path => _path;
    public string BackupPath => _backupPath;

    public async Task<DnsDocument> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var document = await TryReadAsync(_path, ct).ConfigureAwait(false);
            if (document is not null)
                return document;

            var backup = await TryReadAsync(_backupPath, ct).ConfigureAwait(false);
            return backup ?? new DnsDocument();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(DnsDocument document, CancellationToken ct = default)
    {
        document.Normalize();
        var json = JsonSerializer.Serialize(document, JsonOptions);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var temp = _path + ".tmp";
            await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
            if (File.Exists(_path))
            {
                try { File.Copy(_path, _backupPath, overwrite: true); }
                catch { /* 备份失败不阻断保存 */ }
            }
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<DnsDocument?> TryReadAsync(string path, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize<DnsDocument>(json, JsonOptions);
            if (document is null)
                return null;
            document.Normalize();
            return document;
        }
        catch
        {
            return null;
        }
    }
}
