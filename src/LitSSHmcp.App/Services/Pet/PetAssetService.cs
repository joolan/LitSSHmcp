using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows.Media.Imaging;
using LitSSHmcp.App.Services.Pet;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services.Pet;

public sealed class PetAssetInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required bool IsBuiltin { get; init; }
    public string DirectoryPath { get; init; } = string.Empty;
    public override string ToString() => Name;
}

public sealed class PetAssetPack
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required PetFrameSet Frames { get; init; }
    public string Source { get; init; } = string.Empty;
    public string License { get; init; } = string.Empty;
}

/// <summary>PNG 序列帧 / GIF 宠物素材包导入与加载。</summary>
public sealed class PetAssetService
{
    public const string BuiltinId = "builtin";
    private const int CurrentVersion = 1;
    private const long MaxAssetBytes = 50L * 1024 * 1024;
    private const int MaxFiles = 500;
    private const int MaxFramesPerAction = 300;
    private const int MaxFrameSize = 1024;
    private const long MaxDecodedPixels = 64L * 1024 * 1024;
    private static readonly TimeSpan ListCacheTtl = TimeSpan.FromSeconds(5);
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif" };
    private static readonly string[] AllowedExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".txt", ".md" };

    private readonly string _root;
    private static readonly ConcurrentDictionary<string, WeakReference<PetAssetPack>> PackCache = new();
    private static long _cacheHits;
    private static long _cacheMisses;

    public readonly record struct PetAssetCacheStats(int Entries, int LiveEntries, long EstimatedBytes, long Hits, long Misses);

    public static PetAssetCacheStats GetCacheStats()
    {
        var live = 0;
        long bytes = 0;
        foreach (var pair in PackCache)
        {
            if (!pair.Value.TryGetTarget(out var pack))
                continue;
            live++;
            bytes += pack.Frames.EstimatedBytes;
        }
        return new PetAssetCacheStats(PackCache.Count, live, bytes,
            Interlocked.Read(ref _cacheHits), Interlocked.Read(ref _cacheMisses));
    }
    private readonly object _listGate = new();
    private IReadOnlyList<PetAssetInfo>? _listCache;
    private DateTime _listCacheAt;

    public PetAssetService(string? root = null)
    {
        _root = root ?? Path.Combine(ConfigPaths.AppDataDir, "pet-assets");
    }

    public IReadOnlyList<PetAssetInfo> List()
    {
        lock (_listGate)
        {
            if (_listCache is not null && DateTime.UtcNow - _listCacheAt < ListCacheTtl)
                return _listCache;
            var result = BuildList();
            _listCache = result;
            _listCacheAt = DateTime.UtcNow;
            return result;
        }
    }

    public Task<IReadOnlyList<PetAssetInfo>> ListAsync() => Task.Run(List);

    private IReadOnlyList<PetAssetInfo> BuildList()
    {
        var result = new List<PetAssetInfo>
        {
            new() { Id = BuiltinId, Name = "内置占位宠物", IsBuiltin = true }
        };
        try
        {
            if (!Directory.Exists(_root))
                return result;
            foreach (var directory in Directory.EnumerateDirectories(_root))
            {
                try
                {
                    var manifest = Path.Combine(directory, "pet.json");
                    if (!File.Exists(manifest))
                        continue;
                    var name = ReadName(manifest, Path.GetFileName(directory));
                    result.Add(new PetAssetInfo
                    {
                        Id = Path.GetFileName(directory),
                        Name = name,
                        IsBuiltin = false,
                        DirectoryPath = directory
                    });
                }
                catch
                {
                    // 单个损坏素材不影响列表
                }
            }
        }
        catch
        {
            // 忽略素材目录不可读
        }
        return result.OrderBy(item => item.IsBuiltin ? 0 : 1)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private void InvalidateListCache()
    {
        lock (_listGate)
            _listCache = null;
        PackCache.Clear();
    }

    public Task<PetAssetPack> LoadAsync(string? assetId)
        => Task.Run(() => Load(assetId));

    public Task<PetAssetPack> LoadPreviewAsync(string? assetId, CancellationToken cancellationToken = default)
        => Task.Run(() => LoadPreview(assetId, cancellationToken), cancellationToken);

    public PetAssetPack Load(string? assetId)
    {
        if (string.IsNullOrWhiteSpace(assetId) || assetId == BuiltinId)
            return CreateBuiltinPack();

        var directory = ResolveDirectory(assetId);
        if (!Directory.Exists(directory))
            return CreateBuiltinPack();
        var cacheKey = GetPackCacheKey(directory, assetId);
        if (PackCache.TryGetValue(cacheKey, out var weak) && weak.TryGetTarget(out var cached))
        {
            Interlocked.Increment(ref _cacheHits);
            return cached;
        }
        Interlocked.Increment(ref _cacheMisses);
        var pack = LoadDirectory(directory, assetId);
        PackCache[cacheKey] = new WeakReference<PetAssetPack>(pack);
        if (PackCache.Count > 32)
        {
            foreach (var pair in PackCache)
            {
                if (!pair.Value.TryGetTarget(out _))
                    PackCache.TryRemove(pair.Key, out _);
            }
        }
        return pack;
    }

    public Task<PetAssetDraft> LoadDraftAsync(string? assetId)
        => Task.Run(() => LoadDraft(assetId));

    public PetAssetDraft LoadDraft(string? assetId)
    {
        if (string.IsNullOrWhiteSpace(assetId) || assetId == BuiltinId)
            return CreateBuiltinDraft();

        var directory = ResolveDirectory(assetId);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("素材不存在。");
        var manifestPath = Path.Combine(directory, "pet.json");
        if (!File.Exists(manifestPath))
            throw new InvalidDataException("素材包缺少 pet.json。");

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        var fps = ReadFps(root, 10);
        var draft = new PetAssetDraft
        {
            Name = ReadString(root, "name", "未命名宠物"),
            Source = ReadString(root, "source", string.Empty),
            License = ReadString(root, "license", string.Empty),
            Facing = ReadFacing(root),
            Fps = fps
        };
        if (!root.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("素材包缺少 actions。");
        long decodedPixels = 0;
        foreach (var action in Enum.GetValues<PetAction>())
        {
            if (!actions.TryGetProperty(ActionName(action), out var element))
                continue;
            var (frames, delays) = LoadAction(directory, element, fps);
            decodedPixels += frames.Sum(frame => (long)frame.PixelWidth * frame.PixelHeight);
            if (decodedPixels > MaxDecodedPixels)
                throw new InvalidDataException("素材解码后的像素总量超过限制。");
            var target = draft.Actions[action];
            target.Fps = ReadFps(element, fps);
            target.Loop = ReadLoop(element, true);
            for (var i = 0; i < frames.Count; i++)
            {
                target.Frames.Add(new PetAssetDraftFrame
                {
                    Image = frames[i],
                    DelayMs = i < delays.Count ? delays[i] : Math.Max(10, (int)Math.Round(1000.0 / target.Fps)),
                    Label = $"{ActionLabel(action)} {i + 1:000}"
                });
            }
        }
        return draft;
    }

    public PetAssetDraft CreateBuiltinDraft()
    {
        var frames = PetPlaceholder.Create();
        var draft = new PetAssetDraft
        {
            Name = "内置示例宠物",
            Source = "LitSSH built-in placeholder",
            License = "MIT",
            Fps = frames.Fps
        };
        foreach (var action in Enum.GetValues<PetAction>())
        {
            var target = draft.Actions[action];
            target.Fps = frames.Fps;
            target.Loop = true;
            var actionFrames = frames[action];
            for (var i = 0; i < actionFrames.Count; i++)
            {
                target.Frames.Add(new PetAssetDraftFrame
                {
                    Image = actionFrames[i],
                    DelayMs = frames.GetDelay(action, i),
                    Label = $"{ActionLabel(action)} {i + 1:000}"
                });
            }
        }
        return draft;
    }

    private string GetPackCacheKey(string directory, string assetId)
    {
        var manifest = Path.Combine(directory, "pet.json");
        var stamp = File.Exists(manifest)
            ? File.GetLastWriteTimeUtc(manifest).Ticks + ":" + new FileInfo(manifest).Length
            : "0";
        return _root + "|" + assetId + "|" + stamp;
    }

    private PetAssetPack LoadPreview(string? assetId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(assetId) || assetId == BuiltinId)
            return CreateBuiltinPack();

        var directory = ResolveDirectory(assetId);
        if (!Directory.Exists(directory))
            return CreateBuiltinPack();
        var cacheKey = GetPackCacheKey(directory, assetId);
        if (PackCache.TryGetValue(cacheKey, out var cachedWeak) &&
            cachedWeak.TryGetTarget(out var cachedPack))
        {
            Interlocked.Increment(ref _cacheHits);
            return cachedPack;
        }

        var manifestPath = Path.Combine(directory, "pet.json");
        if (!File.Exists(manifestPath))
            throw new InvalidDataException("素材包缺少 pet.json。");

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        if (root.TryGetProperty("version", out var versionElement) &&
            versionElement.TryGetInt32(out var version) && version > CurrentVersion)
            throw new InvalidDataException("素材包版本高于当前程序支持的版本。");
        if (!root.TryGetProperty("actions", out var actions) ||
            actions.ValueKind != JsonValueKind.Object ||
            !actions.TryGetProperty("idle", out var idleElement))
            throw new InvalidDataException("素材包必须包含 idle 动画。");

        cancellationToken.ThrowIfCancellationRequested();
        var fps = ReadFps(root, 10);
        var (frames, delays) = LoadAction(directory, idleElement, fps, cancellationToken);
        if (frames.Count == 0)
            throw new InvalidDataException("素材包必须包含 idle 动画。");
        EnsurePixelBudget(frames);
        return new PetAssetPack
        {
            Id = assetId,
            Name = ReadString(root, "name", "未命名宠物"),
            Source = ReadString(root, "source", string.Empty),
            License = ReadString(root, "license", string.Empty),
            Frames = new PetFrameSet(
                new Dictionary<PetAction, IReadOnlyList<BitmapSource>> { [PetAction.Idle] = frames },
                fps,
                new Dictionary<PetAction, IReadOnlyList<int>> { [PetAction.Idle] = delays },
                ReadFacing(root))
        };
    }

    private static void EnsurePixelBudget(IEnumerable<BitmapSource> frames)
    {
        long pixels = 0;
        foreach (var frame in frames)
        {
            pixels += (long)frame.PixelWidth * frame.PixelHeight;
            if (pixels > MaxDecodedPixels)
                throw new InvalidDataException("素材解码后的像素总量超过限制。");
        }
    }

    private static PetAssetPack CreateBuiltinPack() => new()
    {
        Id = BuiltinId,
        Name = "内置占位宠物",
        Frames = PetPlaceholder.Create(),
        Source = "LitSSH built-in placeholder",
        License = "MIT"
    };

    private static PetAssetPack LoadDirectory(string directory, string assetId)
    {
        var manifestPath = Path.Combine(directory, "pet.json");
        if (!File.Exists(manifestPath))
            throw new InvalidDataException("素材包缺少 pet.json。");

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        var name = ReadString(root, "name", "未命名宠物");
        var source = ReadString(root, "source", string.Empty);
        var license = ReadString(root, "license", string.Empty);
        var facing = ReadFacing(root);
        var fps = ReadFps(root, 10);
        if (root.TryGetProperty("version", out var versionElement) &&
            versionElement.TryGetInt32(out var version) && version > CurrentVersion)
            throw new InvalidDataException("素材包版本高于当前程序支持的版本。");
        if (!root.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("素材包缺少 actions。");

        var frames = new Dictionary<PetAction, IReadOnlyList<BitmapSource>>();
        var delays = new Dictionary<PetAction, IReadOnlyList<int>>();
        foreach (var action in Enum.GetValues<PetAction>())
        {
            if (!actions.TryGetProperty(ActionName(action), out var element))
                continue;
            var (list, frameDelays) = LoadAction(directory, element, fps);
            if (list.Count > 0)
            {
                frames[action] = list;
                delays[action] = frameDelays;
            }
        }
        EnsurePixelBudget(frames.Values.SelectMany(list => list));
        if (!frames.TryGetValue(PetAction.Idle, out var idle) || idle.Count == 0)
            throw new InvalidDataException("素材包必须包含 idle 动画。");
        foreach (var action in Enum.GetValues<PetAction>())
        {
            if (frames.ContainsKey(action))
                continue;
            if (action == PetAction.Work && frames.TryGetValue(PetAction.Stretch, out var stretch))
            {
                frames[action] = stretch;
                if (delays.TryGetValue(PetAction.Stretch, out var stretchDelays))
                    delays[action] = stretchDelays;
                continue;
            }
            frames[action] = idle;
        }
        return new PetAssetPack
        {
            Id = assetId,
            Name = name,
            Frames = new PetFrameSet(frames, fps, delays, facing),
            Source = source,
            License = license
        };
    }

    public Task<PetAssetInfo> ImportFolderAsync(string sourceDirectory)
        => Task.Run(() => ImportFolder(sourceDirectory));

    public Task<PetAssetInfo> ImportZipAsync(string zipPath)
        => Task.Run(() => ImportZip(zipPath));

    public Task<PetAssetInfo> SaveDraftAsync(PetAssetDraft draft, string? existingId = null)
        => Task.Run(() => SaveDraft(draft, existingId));

    private PetAssetInfo SaveDraft(PetAssetDraft draft, string? existingId)
    {
        if (string.IsNullOrWhiteSpace(draft.Name))
            throw new InvalidDataException("请填写素材包名称。");
        if (draft.Actions[PetAction.Idle].Frames.Count == 0)
            throw new InvalidDataException("idle 动作至少需要一帧。");

        var id = string.IsNullOrWhiteSpace(existingId) || existingId == BuiltinId
            ? Guid.NewGuid().ToString("N")
            : existingId;
        var target = ResolveDirectory(id);
        var temp = Path.Combine(_root, id + ".tmp-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(_root, id + ".bak-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            WriteDraft(draft, temp);
            _ = LoadDirectory(temp, id);
            Directory.CreateDirectory(_root);
            var hadExisting = Directory.Exists(target);
            if (hadExisting)
                Directory.Move(target, backup);
            try
            {
                Directory.Move(temp, target);
            }
            catch
            {
                if (hadExisting && !Directory.Exists(target) && Directory.Exists(backup))
                    Directory.Move(backup, target);
                throw;
            }
            try { if (hadExisting && Directory.Exists(backup)) Directory.Delete(backup, recursive: true); }
            catch { /* 旧备份清理失败不影响保存结果 */ }
            InvalidateListCache();
            return new PetAssetInfo { Id = id, Name = draft.Name.Trim(), IsBuiltin = false, DirectoryPath = target };
        }
        catch
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { /* ignore */ }
            throw;
        }
    }

    private static void WriteDraft(PetAssetDraft draft, string target)
    {
        Directory.CreateDirectory(target);
        var actions = new Dictionary<string, object>();
        foreach (var (action, source) in draft.Actions)
        {
            if (source.Frames.Count == 0)
                continue;
            var actionName = ActionName(action);
            var directory = Path.Combine(target, actionName);
            Directory.CreateDirectory(directory);
            var paths = new List<string>();
            var delays = new List<int>();
            for (var i = 0; i < source.Frames.Count; i++)
            {
                var frame = source.Frames[i];
                var relative = actionName + "/" + (i + 1).ToString("D4") + ".png";
                var path = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
                using (var stream = File.Create(path))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(frame.Image));
                    encoder.Save(stream);
                }
                paths.Add(relative.Replace('\\', '/'));
                delays.Add(Math.Max(10, frame.DelayMs));
            }
            actions[actionName] = new
            {
                fps = Math.Clamp(source.Fps, 1, 24),
                loop = source.Loop,
                frames = paths,
                delays
            };
        }
        var manifest = new
        {
            name = draft.Name.Trim(),
            source = draft.Source.Trim(),
            license = draft.License.Trim(),
            facing = draft.Facing == PetFacing.Left ? "left" : "right",
            version = CurrentVersion,
            fps = Math.Clamp(draft.Fps, 1, 24),
            actions
        };
        if (!string.IsNullOrWhiteSpace(draft.Source))
            File.WriteAllText(Path.Combine(target, "SOURCE.txt"), draft.Source.Trim());
        if (!string.IsNullOrWhiteSpace(draft.License))
            File.WriteAllText(Path.Combine(target, "LICENSE.txt"), draft.License.Trim());
        File.WriteAllText(Path.Combine(target, "pet.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    public Task<PetAssetInfo> CopyAsync(string assetId, string? newName = null)
        => Task.Run(() =>
        {
            var draft = LoadDraft(assetId);
            draft.Name = string.IsNullOrWhiteSpace(newName)
                ? draft.Name + " 副本"
                : newName.Trim();
            return SaveDraft(draft, null);
        });

    public Task<PetAssetInfo> RenameAsync(string assetId, string newName)
        => Task.Run(() =>
        {
            if (assetId == BuiltinId)
                throw new InvalidOperationException("内置示例不能直接重命名，请先复制。");
            if (string.IsNullOrWhiteSpace(newName))
                throw new InvalidDataException("素材包名称不能为空。");
            var draft = LoadDraft(assetId);
            draft.Name = newName.Trim();
            return SaveDraft(draft, assetId);
        });

    public Task ExportZipAsync(string assetId, string zipPath)
        => Task.Run(() =>
        {
            var zipDirectory = Path.GetDirectoryName(Path.GetFullPath(zipPath));
            if (!string.IsNullOrEmpty(zipDirectory))
                Directory.CreateDirectory(zipDirectory);
            if (assetId == BuiltinId)
            {
                var temp = Path.Combine(Path.GetTempPath(), "litssh-pet-export-" + Guid.NewGuid().ToString("N"));
                try
                {
                    WriteDraft(CreateBuiltinDraft(), temp);
                    File.Delete(zipPath);
                    ZipFile.CreateFromDirectory(temp, zipPath);
                }
                finally
                {
                    try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { /* ignore */ }
                }
                return;
            }
            var directory = ResolveDirectory(assetId);
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException("素材不存在。");
            File.Delete(zipPath);
            ZipFile.CreateFromDirectory(directory, zipPath);
        });

    public Task DeleteAsync(string assetId)
        => Task.Run(() =>
        {
            if (assetId == BuiltinId)
                return;
            var directory = Path.Combine(_root, assetId);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
            InvalidateListCache();
        });

    private PetAssetInfo ImportFolder(string sourceDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException("素材目录不存在。");
        var manifestRoot = FindManifestRoot(sourceDirectory);
        if (manifestRoot is null)
            throw new InvalidDataException("素材目录缺少 pet.json。");

        var id = Guid.NewGuid().ToString("N");
        var target = Path.Combine(_root, id);
        try
        {
            Directory.CreateDirectory(target);
            CopyValidated(manifestRoot, target);
            var manifest = Path.Combine(target, "pet.json");
            var name = ReadName(manifest, Path.GetFileName(manifestRoot));
            _ = Load(id); // 导入时立即验证素材可加载
            InvalidateListCache();
            return new PetAssetInfo { Id = id, Name = name, IsBuiltin = false, DirectoryPath = target };
        }
        catch
        {
            try { if (Directory.Exists(target)) Directory.Delete(target, true); } catch { /* ignore */ }
            throw;
        }
    }

    private PetAssetInfo ImportZip(string zipPath)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("素材包不存在。", zipPath);
        var temp = Path.Combine(Path.GetTempPath(), "litssh-pet-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                if (archive.Entries.Count > MaxFiles)
                    throw new InvalidDataException("素材包文件数量过多。");
                long total = 0;
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith('/'))
                        continue;
                    var relative = entry.FullName.Replace('\\', '/').TrimStart('/');
                    var destination = Path.GetFullPath(Path.Combine(temp, relative));
                    if (!IsInside(destination, temp))
                        throw new InvalidDataException("素材包包含非法路径。");
                    var extension = Path.GetExtension(relative).ToLowerInvariant();
                    if (relative != "pet.json" && !AllowedExtensions.Contains(extension))
                        throw new InvalidDataException("素材包包含不支持的文件：" + relative);
                    total += entry.Length;
                    if (total > MaxAssetBytes)
                        throw new InvalidDataException("素材包超过 50MB。");
                    var directory = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);
                    entry.ExtractToFile(destination, overwrite: true);
                }
            }
            return ImportFolder(temp);
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { /* ignore */ }
        }
    }

    private static string? FindManifestRoot(string directory)
    {
        if (File.Exists(Path.Combine(directory, "pet.json")))
            return directory;
        var children = Directory.GetDirectories(directory);
        return children.Length == 1 && File.Exists(Path.Combine(children[0], "pet.json"))
            ? children[0]
            : null;
    }

    private static void CopyValidated(string source, string target)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        };
        var files = Directory.EnumerateFiles(source, "*", options).ToArray();
        if (files.Length > MaxFiles)
            throw new InvalidDataException("素材包文件数量过多。");
        long total = 0;
        foreach (var file in files)
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            var relative = Path.GetRelativePath(source, file);
            if (relative != "pet.json" && !AllowedExtensions.Contains(extension))
                throw new InvalidDataException("素材包包含不支持的文件：" + relative);
            total += new FileInfo(file).Length;
            if (total > MaxAssetBytes)
                throw new InvalidDataException("素材包超过 50MB。");
            var destination = Path.Combine(target, relative);
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static (List<BitmapSource> Frames, List<int> Delays) LoadAction(
        string directory, JsonElement element, int defaultFps, CancellationToken cancellationToken = default)
    {
        var fps = Math.Clamp(defaultFps, 1, 24);
        var framesElement = element;
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("fps", out var fpsElement) && fpsElement.TryGetInt32(out var parsedFps))
                fps = Math.Clamp(parsedFps, 1, 24);
            if (!element.TryGetProperty("frames", out framesElement))
                throw new InvalidDataException("动作对象缺少 frames。");
        }

        var files = new List<string>();
        if (framesElement.ValueKind == JsonValueKind.String)
        {
            var path = framesElement.GetString();
            if (!string.IsNullOrWhiteSpace(path))
                files.Add(path);
        }
        else if (framesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in framesElement.EnumerateArray())
            {
                var path = item.GetString();
                if (!string.IsNullOrWhiteSpace(path))
                    files.Add(path);
            }
        }
        if (files.Count > MaxFramesPerAction)
            throw new InvalidDataException("单个动作帧数超过 300。");
        var frameDelay = Math.Max(10, (int)Math.Round(1000.0 / fps));
        var explicitDelays = new List<int>();
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("delays", out var delaysElement) &&
            delaysElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var delay in delaysElement.EnumerateArray())
                explicitDelays.Add(delay.TryGetInt32(out var value) ? Math.Max(10, value) : frameDelay);
        }

        var result = new List<BitmapSource>();
        var delays = new List<int>();
        for (var fileIndex = 0; fileIndex < files.Count; fileIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = files[fileIndex];
            var explicitDelay = fileIndex < explicitDelays.Count ? explicitDelays[fileIndex] : frameDelay;
            var path = Path.GetFullPath(Path.Combine(directory, relative));
            if (!IsInside(path, directory) || !File.Exists(path))
                throw new InvalidDataException("素材路径无效：" + relative);
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (!ImageExtensions.Contains(extension))
                throw new InvalidDataException("不支持的素材格式：" + extension);
            if (path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            {
                var decoder = new GifBitmapDecoder(new Uri(path),
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                foreach (var frame in decoder.Frames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (frame.PixelWidth > MaxFrameSize || frame.PixelHeight > MaxFrameSize)
                        throw new InvalidDataException("单帧尺寸不能超过 1024×1024。");
                    if (frame.CanFreeze)
                        frame.Freeze();
                    result.Add(frame);
                    delays.Add(ReadGifDelay(frame, frameDelay));
                }
            }
            else
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                if (bitmap.PixelWidth > MaxFrameSize || bitmap.PixelHeight > MaxFrameSize)
                    throw new InvalidDataException("单帧尺寸不能超过 1024×1024。");
                bitmap.Freeze();
                result.Add(bitmap);
                delays.Add(explicitDelay);
            }
        }
        return (result, delays);
    }

    private static int ReadGifDelay(BitmapFrame frame, int fallback)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata)
            {
                var value = metadata.GetQuery("/grctlext/Delay");
                if (value is ushort delay)
                    return Math.Max(10, delay * 10);
                if (value is int intDelay)
                    return Math.Max(10, intDelay * 10);
            }
        }
        catch
        {
            // GIF 元数据不可用时使用动作 FPS
        }
        return fallback;
    }

    private string ResolveDirectory(string assetId)
    {
        if (Path.GetFileName(assetId) != assetId)
            throw new InvalidDataException("素材 ID 无效。");
        return Path.Combine(_root, assetId);
    }

    private static string ReadString(JsonElement root, string property, string fallback)
        => root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? fallback
            : fallback;

    private static int ReadFps(JsonElement root, int fallback)
        => root.ValueKind == JsonValueKind.Object &&
           root.TryGetProperty("fps", out var element) && element.TryGetInt32(out var fps)
            ? Math.Clamp(fps, 1, 24)
            : fallback;

    private static PetFacing ReadFacing(JsonElement root)
        => root.ValueKind == JsonValueKind.Object &&
           root.TryGetProperty("facing", out var element) &&
           element.ValueKind == JsonValueKind.String &&
           string.Equals(element.GetString(), "left", StringComparison.OrdinalIgnoreCase)
            ? PetFacing.Left
            : PetFacing.Right;

    private static bool ReadLoop(JsonElement root, bool fallback)
        => root.ValueKind == JsonValueKind.Object &&
           root.TryGetProperty("loop", out var element) &&
           (element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False)
            ? element.GetBoolean()
            : fallback;

    private static string ActionLabel(PetAction action) => action switch
    {
        PetAction.Idle => "idle",
        PetAction.Walk => "walk",
        PetAction.Drag => "drag",
        PetAction.Click => "click",
        PetAction.Sleep => "sleep",
        PetAction.Stretch => "stretch",
        PetAction.Look => "look",
        PetAction.Work => "work",
        _ => "idle"
    };

    private static string ReadName(string manifestPath, string fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return document.RootElement.TryGetProperty("name", out var name)
                ? (name.GetString() ?? fallback)
                : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static string ActionName(PetAction action) => action switch
    {
        PetAction.Idle => "idle",
        PetAction.Walk => "walk",
        PetAction.Drag => "drag",
        PetAction.Click => "click",
        PetAction.Sleep => "sleep",
        PetAction.Stretch => "stretch",
        PetAction.Look => "look",
        PetAction.Work => "work",
        _ => "idle"
    };

    private static bool IsInside(string path, string root)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}
