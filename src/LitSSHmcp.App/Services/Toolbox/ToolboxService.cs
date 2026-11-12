using System.Collections.ObjectModel;
using System.IO;

namespace LitSSHmcp.App.Services.Toolbox;

/// <summary>工具箱数据与启动服务：页面和独立窗口共享同一实例。</summary>
public sealed class ToolboxService
{
    private static readonly Guid HostsBuiltinId = Guid.Parse("4F0C7D6E-7E2B-4D6D-9D1C-83C62B0E3A11");
    private static readonly Guid PasswordBuiltinId = Guid.Parse("7A8D42D7-6E4B-46C9-91FE-8A0B67D5C221");
    private static readonly Guid VaultBuiltinId = Guid.Parse("8B917E9D-5B7C-4B1B-B98D-4A2E62A0D4C3");
    private static readonly Guid PasswordVaultBuiltinId = Guid.Parse("A1D6C4B8-3E5F-4A7B-8C9D-0E1F2A3B4C5D");
    private static readonly Guid CertificateBuiltinId = Guid.Parse("C4E8A2F1-9B3D-4F6A-8C2E-7D1B5A9F0E63");
    private static readonly Guid TextCodecBuiltinId = Guid.Parse("D5F9B3A2-0C4E-4B7F-9D3A-8E2C6B0F1A74");
    private static readonly Guid SshKeyBuiltinId = Guid.Parse("E6A0C4B3-1D5F-4C8A-9E4B-8F3D7C1A2B85");
    private static readonly Guid LocalPortBuiltinId = Guid.Parse("F7B1D5C4-2E6A-4D9B-8F5C-9A4E8D2B3C96");

    private readonly ToolboxStore _store;
    private readonly ToolboxLauncher _launcher;
    private bool _initialized;

    public ToolboxService(IUserDialogService dialogs, string? storePath = null)
    {
        _launcher = new ToolboxLauncher(dialogs);
        _store = new ToolboxStore(storePath);
    }

    public ObservableCollection<ToolboxItem> Items { get; } = new();

    public async Task InitializeAsync()
    {
        if (_initialized)
            return;
        _initialized = true;

        var document = await _store.LoadAsync();
        Items.Clear();
        var ordered = document.Items
            .OrderBy(i => i.SortOrder)
            .ThenBy(i => i.CreatedAt)
            .ToList();

        var changed = false;
        var order = 0;
        foreach (var item in ordered)
        {
            if (item.SortOrder != order)
            {
                item.SortOrder = order;
                changed = true;
            }
            order += 10;
            Items.Add(item);
        }

        changed |= EnsureBuiltin("hosts", HostsBuiltinId, "Hosts 修改工具",
            "编辑、备份与刷新本机 hosts 文件", order);
        changed |= EnsureBuiltin("password", PasswordBuiltinId, "随机密码生成器",
            "生成高强度随机密码并复制到剪贴板", order + 10);
        changed |= EnsureBuiltin("vault", VaultBuiltinId, "加密文件柜",
            "本机加密保存敏感文件，解锁后可导入导出", order + 20);
        changed |= EnsureBuiltin("password-vault", PasswordVaultBuiltinId, "密码保管箱",
            "加密保存账号密码、TOTP 验证码和附件", order + 30);
        changed |= EnsureBuiltin("certificate", CertificateBuiltinId, "证书 / TLS 检测",
            "批量检测 URL 证书、有效期、证书链、SAN 与 TLS 版本", order + 40);
        changed |= EnsureBuiltin("text-codec", TextCodecBuiltinId, "文本 / 编码转换",
            "JSON/XML 格式化、Base64/URL/HTML、JWT、时间戳、哈希与 UUID", order + 50);
        changed |= EnsureBuiltin("ssh-key", SshKeyBuiltinId, "SSH 密钥助手",
            "生成 OpenSSH 密钥、查看公钥类型、注释与 SHA256 指纹", order + 60);
        changed |= EnsureBuiltin("local-port", LocalPortBuiltinId, "本地端口 / 进程检查",
            "查看本机监听端口、活动连接、PID、进程与路径", order + 70);

        if (changed)
            await PersistAsync();
    }

    private bool EnsureBuiltin(string id, Guid itemId, string name, string description, int sortOrder)
    {
        if (Items.Any(i => i.Type == ToolboxItemType.Builtin && i.BuiltinId == id))
            return false;

        Items.Add(new ToolboxItem
        {
            Id = itemId,
            Name = name,
            Description = description,
            Type = ToolboxItemType.Builtin,
            BuiltinId = id,
            IsPinned = true,
            SortOrder = sortOrder
        });
        return true;
    }

    public async Task<ToolboxItem> AddAsync(ToolboxItem item)
    {
        item.Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id;
        item.SortOrder = NextSortOrder();
        item.UpdatedAt = DateTimeOffset.Now;
        Items.Add(item);
        await PersistAsync();
        return item;
    }

    public Task MoveBeforeAsync(Guid sourceId, Guid targetId) =>
        MoveRelativeAsync(sourceId, targetId, after: false);

    public async Task MoveRelativeAsync(Guid sourceId, Guid targetId, bool after)
    {
        if (sourceId == targetId)
            return;

        var ordered = Items.OrderBy(i => i.SortOrder).ThenBy(i => i.CreatedAt).ToList();
        var source = ordered.FirstOrDefault(i => i.Id == sourceId);
        var target = ordered.FirstOrDefault(i => i.Id == targetId);
        if (source is null || target is null)
            return;

        ordered.Remove(source);
        var index = ordered.IndexOf(target);
        if (index < 0)
            return;
        ordered.Insert(index + (after ? 1 : 0), source);

        for (var i = 0; i < ordered.Count; i++)
            ordered[i].SortOrder = i * 10;

        Items.Clear();
        foreach (var item in ordered)
            Items.Add(item);
        await PersistAsync();
    }

    private int NextSortOrder() => Items.Count == 0 ? 0 : Items.Max(i => i.SortOrder) + 10;

    public int AddFromPaths(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
        {
            var item = CreateFromPath(path);
            if (item is null)
                continue;
            if (Items.Any(i => string.Equals(i.Target, item.Target, StringComparison.OrdinalIgnoreCase) && i.Type == item.Type))
                continue;

            item.SortOrder = NextSortOrder();
            Items.Add(item);
            added++;
        }

        if (added > 0)
            _ = PersistAsync();
        return added;
    }

    public async Task UpdateAsync(ToolboxItem item)
    {
        var existing = Items.FirstOrDefault(i => i.Id == item.Id);
        if (existing is null)
            return;

        var index = Items.IndexOf(existing);
        item.UpdatedAt = DateTimeOffset.Now;
        Items[index] = item;
        await PersistAsync();
    }

    public async Task RemoveAsync(ToolboxItem item)
    {
        if (item.Type == ToolboxItemType.Builtin)
            return;
        Items.Remove(item);
        await PersistAsync();
    }

    public async Task TogglePinAsync(ToolboxItem item)
    {
        item.IsPinned = !item.IsPinned;
        item.UpdatedAt = DateTimeOffset.Now;
        await PersistAsync();
    }

    public async Task<ToolboxLaunchResult> LaunchAsync(ToolboxItem item)
    {
        var result = await _launcher.LaunchAsync(item);
        if (result.Success)
        {
            item.LastUsedAt = DateTimeOffset.Now;
            item.UsageCount++;
            _ = PersistAsync();
        }
        return result;
    }

    private async Task PersistAsync()
    {
        try
        {
            await _store.SaveAsync(new ToolboxDocument { Items = Items.ToList() });
        }
        catch
        {
            // 持久化失败不阻断当前操作；下次修改会再次尝试
        }
    }

    private static ToolboxItem? CreateFromPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var folderName = new DirectoryInfo(path).Name;
                return new ToolboxItem
                {
                    Name = string.IsNullOrWhiteSpace(folderName) ? path : folderName,
                    Type = ToolboxItemType.Folder,
                    Target = path
                };
            }

            if (!File.Exists(path))
                return null;

            var extension = Path.GetExtension(path).ToLowerInvariant();
            var fileType = extension switch
            {
                ".exe" or ".lnk" => ToolboxItemType.Application,
                ".bat" or ".cmd" or ".ps1" => ToolboxItemType.Script,
                ".url" => ToolboxItemType.File,
                _ => ToolboxItemType.File
            };

            var name = fileType is ToolboxItemType.Application or ToolboxItemType.Script
                ? Path.GetFileNameWithoutExtension(path)
                : Path.GetFileName(path);

            return new ToolboxItem
            {
                Name = string.IsNullOrWhiteSpace(name) ? path : name,
                Type = fileType,
                Target = path,
                ScriptShell = extension == ".ps1" ? "powershell" : "cmd"
            };
        }
        catch
        {
            return null;
        }
    }
}
