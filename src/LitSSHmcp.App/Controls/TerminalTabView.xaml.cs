using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
namespace LitSSHmcp.App.Controls;

/// <summary>终端标签内容：左侧远程文件浏览器 + 右侧终端；分栏宽度记忆、可显示/隐藏。</summary>
public partial class TerminalTabView : UserControl
{
    private const string WidthKey = "ssh.filepanel.width";
    private const double DefaultWidth = 300;
    private const double MinPanelWidth = 120;

    public TerminalTabView()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyLayout();
        DataContextChanged += (_, _) => Hook();
    }

    private TerminalSessionViewModel? Vm => DataContext as TerminalSessionViewModel;

    private void Hook()
    {
        if (Vm is { } vm)
        {
            vm.PropertyChanged -= OnVmPropertyChanged;
            vm.PropertyChanged += OnVmPropertyChanged;
        }
        ApplyLayout();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalSessionViewModel.ShowFileBrowser))
            ApplyLayout();
    }

    private void ApplyLayout()
    {
        if (Vm is not { } vm)
            return;
        var saved = UiPrefs.GetDouble(WidthKey, DefaultWidth);
        FileCol.Width = vm.ShowFileBrowser ? new GridLength(Math.Max(MinPanelWidth, saved)) : new GridLength(0);
    }

    private void OnSnippets(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var snippets = SnippetStore.Load();
        var added = 0;
        foreach (var snippet in snippets)
        {
            if (string.IsNullOrWhiteSpace(snippet.Command))
                continue;
            var command = snippet.Command;
            var item = new MenuItem
            {
                Header = string.IsNullOrWhiteSpace(snippet.Name) ? command : snippet.Name,
                ToolTip = command
            };
            item.Click += (_, _) => Vm?.SendInput(command + "\r");
            menu.Items.Add(item);
            added++;
        }
        if (added == 0)
            menu.Items.Add(new MenuItem { Header = "（无片段，请在 设置 → 命令片段 添加）", IsEnabled = false });

        menu.PlacementTarget = sender as UIElement;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        var width = FileCol.ActualWidth;
        if (width >= MinPanelWidth)
            UiPrefs.SetDouble(WidthKey, width);
    }

    private void OnSendInput(object sender, RoutedEventArgs e) => SendBoxText();

    private void OnSendBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            SendBoxText();
            e.Handled = true;
        }
    }

    private void SendBoxText()
    {
        var text = SendBox.Text;
        if (string.IsNullOrEmpty(text))
            return;
        Vm?.SendInput(text + "\r");
        SendBox.Text = string.Empty;
    }
}
