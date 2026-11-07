using System.IO;
using System.IO.Compression;
using LitSSHmcp.App.Services.Pet;
using System.Windows.Media.Imaging;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class PetAssetServiceTests : IDisposable
{
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "litssh-pet-assets-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Imports_folder_and_loads_png_sequence()
    {
        var source = CreateAssetSource("FolderPet");
        var service = new PetAssetService(Path.Combine(_root, "store"));

        var imported = await service.ImportFolderAsync(source);
        var list = service.List();
        Assert.Contains(list, item => item.Id == imported.Id && item.Name == "FolderPet");

        var pack = await service.LoadAsync(imported.Id);
        Assert.Equal(8, pack.Frames.Fps);
        Assert.NotEmpty(pack.Frames[PetAction.Idle]);
        Assert.Single(pack.Frames[PetAction.Walk]);
        Assert.Equal(500, pack.Frames.GetDelay(PetAction.Walk, 0));
    }

    [Fact]
    public async Task Imports_zip_and_rejects_unsupported_files()
    {
        var source = CreateAssetSource("ZipPet");
        var zip = Path.Combine(_root, "pet.zip");
        Directory.CreateDirectory(_root);
        ZipFile.CreateFromDirectory(source, zip);
        var service = new PetAssetService(Path.Combine(_root, "store"));

        var imported = await service.ImportZipAsync(zip);
        var pack = await service.LoadAsync(imported.Id);
        Assert.Equal("ZipPet", pack.Name);

        File.WriteAllText(Path.Combine(source, "payload.exe"), "bad");
        var badZip = Path.Combine(_root, "bad.zip");
        ZipFile.CreateFromDirectory(source, badZip);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportZipAsync(badZip));
    }

    [Fact]
    public async Task Saves_draft_as_asset_pack_with_delays()
    {
        var bitmap = CreateBitmap();
        var draft = new PetAssetDraft { Name = "DraftPet", Fps = 12 };
        draft.Actions[PetAction.Idle].Fps = 4;
        draft.Actions[PetAction.Idle].Frames.Add(new PetAssetDraftFrame
        {
            Image = bitmap,
            DelayMs = 250
        });
        var service = new PetAssetService(Path.Combine(_root, "store"));

        var info = await service.SaveDraftAsync(draft);
        var loaded = await service.LoadAsync(info.Id);

        Assert.Equal("DraftPet", loaded.Name);
        Assert.Single(loaded.Frames[PetAction.Idle]);
        Assert.Equal(250, loaded.Frames.GetDelay(PetAction.Idle, 0));
    }

    [Fact]
    public async Task Loads_draft_for_editing_with_metadata_and_delays()
    {
        var draft = new PetAssetDraft
        {
            Name = "EditablePet",
            Source = "Test source",
            License = "MIT",
            Fps = 12
        };
        draft.Actions[PetAction.Idle].Fps = 12;
        draft.Actions[PetAction.Idle].Frames.Add(new PetAssetDraftFrame
        {
            Image = CreateBitmap(),
            DelayMs = 123
        });
        var service = new PetAssetService(Path.Combine(_root, "store"));
        var info = await service.SaveDraftAsync(draft);

        var loaded = await service.LoadDraftAsync(info.Id);

        Assert.Equal("Test source", loaded.Source);
        Assert.Equal("MIT", loaded.License);
        Assert.Equal(123, loaded.Actions[PetAction.Idle].Frames[0].DelayMs);
        Assert.Equal(12, loaded.Actions[PetAction.Idle].Fps);
    }

    [Fact]
    public async Task Loads_preview_with_metadata_and_idle_frames()
    {
        var draft = new PetAssetDraft { Name = "PreviewPet", Source = "Preview source", License = "CC0" };
        draft.Actions[PetAction.Idle].Frames.Add(new PetAssetDraftFrame
        {
            Image = CreateBitmap(),
            DelayMs = 80
        });
        draft.Actions[PetAction.Walk].Frames.Add(new PetAssetDraftFrame
        {
            Image = CreateBitmap(),
            DelayMs = 90
        });
        var service = new PetAssetService(Path.Combine(_root, "store"));
        var info = await service.SaveDraftAsync(draft);

        var preview = await service.LoadPreviewAsync(info.Id);

        Assert.Equal("Preview source", preview.Source);
        Assert.Equal("CC0", preview.License);
        Assert.Single(preview.Frames[PetAction.Idle]);
        Assert.Equal(80, preview.Frames.GetDelay(PetAction.Idle, 0));
    }

    [Fact]
    public async Task Saves_existing_asset_in_place_and_updates_metadata()
    {
        var draft = new PetAssetDraft { Name = "Original", License = "MIT" };
        draft.Actions[PetAction.Idle].Frames.Add(new PetAssetDraftFrame
        {
            Image = CreateBitmap(),
            DelayMs = 100
        });
        var service = new PetAssetService(Path.Combine(_root, "store"));
        var info = await service.SaveDraftAsync(draft);

        var loaded = await service.LoadDraftAsync(info.Id);
        loaded.Name = "Updated";
        loaded.License = "CC0";
        loaded.Actions[PetAction.Idle].Frames[0].DelayMs = 200;
        var updated = await service.SaveDraftAsync(loaded, info.Id);

        Assert.Equal(info.Id, updated.Id);
        var reloaded = await service.LoadAsync(info.Id);
        Assert.Equal("Updated", reloaded.Name);
        Assert.Equal("CC0", reloaded.License);
        Assert.Equal(200, reloaded.Frames.GetDelay(PetAction.Idle, 0));
        Assert.Single(service.List(), item => item.Id == info.Id);
    }

    [Fact]
    public async Task Reports_asset_cache_hits_and_memory_usage()
    {
        var draft = new PetAssetDraft { Name = "Cached" };
        draft.Actions[PetAction.Idle].Frames.Add(new PetAssetDraftFrame { Image = CreateBitmap(), DelayMs = 100 });
        var service = new PetAssetService(Path.Combine(_root, "store"));
        var info = await service.SaveDraftAsync(draft);
        var before = PetAssetService.GetCacheStats();
        await service.LoadAsync(info.Id);
        await service.LoadAsync(info.Id);
        var after = PetAssetService.GetCacheStats();
        Assert.True(after.Hits > before.Hits);
        Assert.True(after.LiveEntries >= 1);
        Assert.True(after.EstimatedBytes > 0);
    }

    [Fact]
    public async Task Copies_and_renames_assets_without_touching_original()
    {
        var draft = new PetAssetDraft { Name = "Source", License = "MIT" };
        draft.Actions[PetAction.Idle].Frames.Add(new PetAssetDraftFrame
        {
            Image = CreateBitmap(),
            DelayMs = 100
        });
        var service = new PetAssetService(Path.Combine(_root, "store"));
        var original = await service.SaveDraftAsync(draft);

        var copy = await service.CopyAsync(original.Id, "Copied");
        var renamed = await service.RenameAsync(copy.Id, "Renamed");

        Assert.Equal("Renamed", (await service.LoadAsync(renamed.Id)).Name);
        Assert.Equal("Source", (await service.LoadAsync(original.Id)).Name);
    }

    [Fact]
    public async Task Copies_builtin_sample_as_editable_asset()
    {
        var service = new PetAssetService(Path.Combine(_root, "store"));

        var copy = await service.CopyAsync(PetAssetService.BuiltinId, "My Sample");

        var loaded = await service.LoadAsync(copy.Id);
        Assert.Equal("My Sample", loaded.Name);
        Assert.NotEmpty(loaded.Frames[PetAction.Idle]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RenameAsync(PetAssetService.BuiltinId, "Bad"));
    }

    [Fact]
    public async Task Exports_builtin_sample_with_license_and_source()
    {
        var service = new PetAssetService(Path.Combine(_root, "store"));
        var zip = Path.Combine(_root, "builtin.zip");

        await service.ExportZipAsync(PetAssetService.BuiltinId, zip);

        using var archive = ZipFile.OpenRead(zip);
        Assert.Contains(archive.Entries, entry => entry.FullName == "pet.json");
        Assert.Contains(archive.Entries, entry => entry.FullName == "LICENSE.txt");
        Assert.Contains(archive.Entries, entry => entry.FullName == "SOURCE.txt");
        var builtin = service.CreateBuiltinDraft();
        Assert.Equal("MIT", builtin.License);
        Assert.NotEmpty(builtin.Actions[PetAction.Idle].Frames);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private static BitmapImage CreateBitmap()
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(Convert.FromBase64String(OnePixelPng));
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private string CreateAssetSource(string name)
    {
        var source = Path.Combine(_root, "source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(source, "idle"));
        File.WriteAllBytes(Path.Combine(source, "idle", "0.png"), Convert.FromBase64String(OnePixelPng));
        File.WriteAllText(Path.Combine(source, "idle", "1.png"), string.Empty);
        File.Copy(Path.Combine(source, "idle", "0.png"), Path.Combine(source, "idle", "1.png"), true);
        File.WriteAllText(Path.Combine(source, "pet.json"),
            "{\"name\":\"" + name + "\",\"fps\":8,\"actions\":{\"idle\":[\"idle/0.png\",\"idle/1.png\"],\"walk\":{\"fps\":2,\"frames\":[\"idle/0.png\"]}}}");
        return source;
    }
}
