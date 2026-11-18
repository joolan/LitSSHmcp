using System.IO;
using System.Text.Json;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services.Toolbox;

/// <summary>网络唤醒目标的本机 JSON 存储：原子写入，损坏时回退备份。</summary>
public sealed class WakeOnLanStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly string _backupPath;

    public WakeOnLanStore(string? path = null)
    {
        _path = path ?? Path.Combine(ConfigPaths.AppDataDir, "wake-on-lan.json");
        _backupPath = _path + ".bak";
    }

    public async Task<WakeOnLanDocument> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var document = await TryReadAsync(_path);
            if (document is not null)
                return Normalize(document);

            var backup = await TryReadAsync(_backupPath);
            if (backup is not null)
                return Normalize(backup);

            return new WakeOnLanDocument();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(WakeOnLanDocument document)
    {
        await _gate.WaitAsync();
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(document, JsonOptions);
            var temp = _path + ".tmp";
            await File.WriteAllTextAsync(temp, json);

            if (File.Exists(_path))
            {
                try { File.Copy(_path, _backupPath, overwrite: true); } catch { /* ignore */ }
            }

            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<WakeOnLanDocument?> TryReadAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<WakeOnLanDocument>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static WakeOnLanDocument Normalize(WakeOnLanDocument document)
    {
        document.SchemaVersion = WakeOnLanDocument.CurrentSchemaVersion;
        document.Hosts ??= new List<WakeOnLanHost>();
        foreach (var host in document.Hosts)
        {
            host.Id = host.Id == Guid.Empty ? Guid.NewGuid() : host.Id;
            host.Name = host.Name?.Trim() ?? string.Empty;
            host.MacAddress = host.MacAddress?.Trim() ?? string.Empty;
            host.IpAddress = host.IpAddress?.Trim() ?? string.Empty;
            host.BroadcastAddress = host.BroadcastAddress?.Trim() ?? string.Empty;
            host.SecureOnPassword = host.SecureOnPassword?.Trim() ?? string.Empty;
            host.Notes ??= string.Empty;
            host.ScheduleTime = host.ScheduleTime?.Trim() ?? string.Empty;
            host.LastScheduledWakeDate = host.LastScheduledWakeDate?.Trim() ?? string.Empty;
            host.Port = host.Port is < 1 or > 65535 ? WakeOnLanService.DefaultPort : host.Port;
        }
        return document;
    }
}
