using System.IO;
using System.Text.Json;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services.Toolbox;

/// <summary>工具箱本机 JSON 存储：原子写入，损坏时尝试回退备份。</summary>
public sealed class ToolboxStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly string _backupPath;

    public ToolboxStore(string? path = null)
    {
        _path = path ?? Path.Combine(ConfigPaths.AppDataDir, "toolbox.json");
        _backupPath = _path + ".bak";
    }

    public async Task<ToolboxDocument> LoadAsync()
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

            return new ToolboxDocument();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(ToolboxDocument document)
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

    private static async Task<ToolboxDocument?> TryReadAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<ToolboxDocument>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static ToolboxDocument Normalize(ToolboxDocument document)
    {
        document.SchemaVersion = ToolboxDocument.CurrentSchemaVersion;
        document.Items ??= new List<ToolboxItem>();
        foreach (var item in document.Items)
        {
            item.Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id;
            item.Name = item.Name?.Trim() ?? string.Empty;
            item.Target = item.Target?.Trim() ?? string.Empty;
            item.Description ??= string.Empty;
            item.Arguments ??= string.Empty;
            item.WorkingDirectory ??= string.Empty;
            item.Group = item.Group?.Trim() ?? string.Empty;
            item.Tags = item.Tags?.Trim() ?? string.Empty;
            item.ScriptShell = string.IsNullOrWhiteSpace(item.ScriptShell) ? "cmd" : item.ScriptShell;
        }
        return document;
    }
}
