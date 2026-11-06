using System.IO;
using LitSSHmcp.App.Services.Toolbox;
using LitSSHmcp.App.ViewModels;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class ToolboxServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "litssh-toolbox-" + Guid.NewGuid().ToString("N"));

    public ToolboxServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Add_from_paths_classifies_and_deduplicates()
    {
        var exe = Path.Combine(_root, "demo.exe");
        var text = Path.Combine(_root, "notes.txt");
        var script = Path.Combine(_root, "cleanup.bat");
        var folder = Path.Combine(_root, "folder");
        await File.WriteAllBytesAsync(exe, new byte[] { 1 });
        await File.WriteAllTextAsync(text, "x");
        await File.WriteAllTextAsync(script, "echo hi");
        Directory.CreateDirectory(folder);

        var service = new ToolboxService(new FakeDialogService(), Path.Combine(_root, "toolbox.json"));
        await service.InitializeAsync();

        var added = service.AddFromPaths(new[] { exe, text, script, folder });

        Assert.Equal(4, added);
        Assert.Contains(service.Items, i => i.Type == ToolboxItemType.Application && i.Target == exe);
        Assert.Contains(service.Items, i => i.Type == ToolboxItemType.File && i.Target == text);
        Assert.Contains(service.Items, i => i.Type == ToolboxItemType.Script && i.Target == script && i.ScriptShell == "cmd");
        Assert.Contains(service.Items, i => i.Type == ToolboxItemType.Folder && i.Target == folder);
        Assert.Contains(service.Items, i => i.Type == ToolboxItemType.Builtin && i.BuiltinId == "hosts");

        Assert.Equal(0, service.AddFromPaths(new[] { exe, text, script, folder }));
    }

    [Fact]
    public async Task Move_before_reorders_without_changing_other_sort_rules()
    {
        var firstPath = Path.Combine(_root, "a.exe");
        var secondPath = Path.Combine(_root, "b.exe");
        var thirdPath = Path.Combine(_root, "c.exe");
        await File.WriteAllBytesAsync(firstPath, new byte[] { 1 });
        await File.WriteAllBytesAsync(secondPath, new byte[] { 2 });
        await File.WriteAllBytesAsync(thirdPath, new byte[] { 3 });

        var service = new ToolboxService(new FakeDialogService(), Path.Combine(_root, "toolbox.json"));
        await service.InitializeAsync();
        service.AddFromPaths(new[] { firstPath, secondPath, thirdPath });

        var first = service.Items.Single(i => i.Target == firstPath);
        var third = service.Items.Single(i => i.Target == thirdPath);

        await service.MoveBeforeAsync(third.Id, first.Id);

        var ordered = service.Items.OrderBy(i => i.SortOrder).ToList();
        Assert.True(ordered.IndexOf(third) < ordered.IndexOf(first));
        Assert.Equal(ordered.Select(i => i.SortOrder).Distinct().Count(), ordered.Count);
    }

    [Fact]
    public async Task Builtin_tools_can_set_group_and_are_not_editable()
    {
        var service = new ToolboxService(new FakeDialogService(), Path.Combine(_root, "toolbox.json"));
        var viewModel = new ToolboxViewModel(service, new FakeDialogService());
        await viewModel.LoadAsync();

        var builtin = viewModel.Items.First(i => i.Model.BuiltinId == "password-vault");
        Assert.False(builtin.CanEdit);

        await viewModel.SetGroupAsync(builtin, "安全");
        Assert.Equal("安全", service.Items.First(i => i.BuiltinId == "password-vault").Group);
        Assert.Contains("安全", viewModel.Groups);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }
}
