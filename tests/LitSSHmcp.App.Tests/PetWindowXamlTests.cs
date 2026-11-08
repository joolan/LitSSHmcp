using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using LitSSHmcp.App.Controls;
using LitSSHmcp.App.Services.Pet;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class PetWindowXamlTests
{
    private static Exception? RunSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return captured;
    }

    [Fact]
    public void Placeholder_pet_generates_animation_frames()
    {
        var ex = RunSta(() =>
        {
            var frames = PetPlaceholder.Create();
            Assert.NotEmpty(frames[PetAction.Idle]);
            Assert.NotEmpty(frames[PetAction.Walk]);
            Assert.NotEmpty(frames[PetAction.Drag]);
            Assert.NotEmpty(frames[PetAction.Click]);
            Assert.NotEmpty(frames[PetAction.Sleep]);
            Assert.NotEmpty(frames[PetAction.Stretch]);
            Assert.NotEmpty(frames[PetAction.Look]);
            Assert.NotEmpty(frames[PetAction.Work]);
        });
        Assert.Null(ex);
    }

    [Fact]
    public void Full_context_ring_renders_complete_circle()
    {
        var ex = RunSta(() =>
        {
            var ring = new ContextRing { Width = 100, Height = 100, Percent = 1, InvertScale = true };
            ring.Measure(new Size(100, 100));
            ring.Arrange(new Rect(0, 0, 100, 100));
            ring.UpdateLayout();
            var bitmap = new RenderTargetBitmap(100, 100, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(ring);
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(50, 91, 1, 1), pixel, 4, 0);
            Assert.True(pixel[3] > 0);
            Assert.True(pixel[1] > pixel[2], "底部应为绿色进度，而不是灰色轨道：BGRA=" + string.Join(",", pixel));
        });
        Assert.Null(ex);
    }

    [Fact]
    public void Pet_window_and_settings_view_parse_xaml()
    {
        var ex = RunSta(() =>
        {
            var window = new PetWindow(new PetConfig { Enabled = true, ReduceMotion = true });
            var grid = Assert.IsType<Grid>(window.Content);
            Assert.Contains(grid.ContextMenu!.Items.OfType<MenuItem>(),
                item => (string)item.Header == "展示动作");
            window.SetAiState(PetAiState.Running);
            window.SetAiState(PetAiState.WaitingApproval);
            window.SetAiState(PetAiState.Completed);
            window.SetAiState(PetAiState.Failed);
            window.SetAiState(PetAiState.Idle);
            window.Close();
            _ = new PetSettingsView();
            var editor = new PetAssetEditorWindow(new PetAssetService().CreateBuiltinDraft());
            editor.Close();
            var sprite = new SpriteSheetImportWindow();
            sprite.Close();
        });
        Assert.Null(ex);
    }
}
