using System.Windows;
using System.Windows.Input;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

/// <summary>独立「临时聊天」面板：纯内存、无工具/技能、可切换模型；关闭即清空。</summary>
public partial class TemporaryChatWindow : FluentWindow
{
    private readonly TemporaryChatViewModel _vm;

    public TemporaryChatWindow(IConfigService configService, string? initialProviderId)
    {
        InitializeComponent();
        _vm = new TemporaryChatViewModel(configService, initialProviderId);
        DataContext = _vm;
        WindowLayout.Attach(this, "agent-temp");
        _vm.Turns.CollectionChanged += (_, _) => ChatScroll.ScrollToEnd();
        Loaded += async (_, _) =>
        {
            await _vm.InitializeAsync();
            InputBox.Focus();
        };
        Closed += async (_, _) => await _vm.DisposeAsync();
    }

    /// <summary>把初始文本填入输入框（不自动发送）。</summary>
    public void Prefill(string text)
    {
        _vm.Input = text;
        Dispatcher.BeginInvoke(new Action(() => InputBox.Focus()), System.Windows.Threading.DispatcherPriority.Input);
    }

    private TemporaryChatViewModel Vm => _vm;

    private void OnAddAttachment(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "添加附件",
            Multiselect = true,
            Filter = "所有文件|*.*"
        };
        if (dialog.ShowDialog(this) == true)
            _vm.AddAttachmentFiles(dialog.FileNames);
    }

    private void OnRemoveAttachment(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AgentAttachment a)
            _vm.RemoveAttachment(a);
    }

    private void OnClearAttachments(object sender, RoutedEventArgs e) => _vm.ClearAttachments();

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            return;
        if (!_vm.IsBusy && _vm.SendOrStopCommand.CanExecute(null))
        {
            _vm.SendOrStopCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            _vm.AddAttachmentFiles(paths);
            e.Handled = true;
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().ToArray();
                if (files.Length > 0)
                {
                    _vm.AddAttachmentFiles(files);
                    e.Handled = true;
                }
            }
        }
        catch
        {
            // 剪贴板不可用忽略
        }
    }
}
