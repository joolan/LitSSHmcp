using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using LitSSHmcp.Agent;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Services.Storage;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class AgentWindow : FluentWindow
{
    private readonly AgentViewModel _viewModel;
    private readonly IConfigService _configService;

    public AgentWindow(IConfigService configService, IContextStore store, string skillsDir)
    {
        InitializeComponent();
        _configService = configService;

        _viewModel = new AgentViewModel(configService, store, skillsDir, action => Dispatcher.Invoke(action));
        _viewModel.SettingsRequested += tab => Dispatcher.Invoke(() => OpenSettings(tab));
        DataContext = _viewModel;
        WindowLayout.Attach(this, "agent");
        _viewModel.Turns.CollectionChanged += (_, _) => ChatScroll.ScrollToEnd();
        Loaded += async (_, _) => await _viewModel.InitializeAsync();
        Closed += async (_, _) => await _viewModel.DisposeAsync();
    }

    // 点击用户指令：展开/折叠本轮工具过程
    private void OnUserClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AgentTurn turn && turn.HasSteps)
            turn.ShowTools = !turn.ShowTools;
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => OpenSettings(null);

    private void OpenSettings(string? tab)
    {
        new AgentSettingsWindow(_configService, tab) { Owner = this }.ShowDialog();
        _ = _viewModel.ReloadProvidersAsync();
    }

    // 输入区「＋」更多：左键打开其右键菜单
    private void OnComposerMore(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.ContextMenu is { } menu)
        {
            menu.PlacementTarget = element;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    // 拖动输入区顶部把手调整高度
    private void OnComposerResize(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        var height = InputBox.Height;
        if (double.IsNaN(height) || height <= 0)
            height = InputBox.ActualHeight;
        InputBox.Height = Math.Clamp(height - e.VerticalChange, 64, 400);
    }

    // 右键会话：在 ListBox 层定位鼠标所在项并“吞掉”事件（不切换/不聚焦会话），让右键菜单作用于该项
    private AgentSessionRow? _contextSession;

    private void OnSessionListRightClick(object sender, MouseButtonEventArgs e)
    {
        _contextSession = FindSessionRow(e.OriginalSource as DependencyObject);
        if (_contextSession is not null)
            e.Handled = true;
    }

    private static AgentSessionRow? FindSessionRow(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is System.Windows.Controls.ListBoxItem item)
                return item.DataContext as AgentSessionRow;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private void OnDeleteSession(object sender, RoutedEventArgs e)
    {
        _ = _viewModel.DeleteSessionAsync(_contextSession ?? _viewModel.SelectedSession);
    }

    private void OnClearDisplay(object sender, RoutedEventArgs e)
    {
        _ = _viewModel.ClearSessionMessagesAsync(_contextSession ?? _viewModel.SelectedSession);
    }

    private void OnOpenWorkspace(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = _viewModel.WorkspaceDir;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("打开工作区文件夹失败: " + ex.Message, "AI 运维助手");
        }
    }

    private void OnRenameSession(object sender, RoutedEventArgs e)
    {
        var session = _contextSession ?? _viewModel.SelectedSession;
        if (session is null)
            return;
        var name = TextInputDialog.Prompt(this, "重命名会话", "会话名称:", session.Title);
        if (name is not null)
            _ = _viewModel.RenameSessionAsync(session, name);
    }

    private void OnEditTurn(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AgentTurn turn)
            _viewModel.BeginEdit(turn);
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+V 粘贴剪贴板图片为附件
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && Clipboard.ContainsImage())
        {
            try
            {
                var image = Clipboard.GetImage();
                if (image is not null)
                {
                    var dir = Path.Combine(Path.GetTempPath(), "litssh-attach");
                    Directory.CreateDirectory(dir);
                    var file = Path.Combine(dir, $"paste-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                    using (var fs = File.Create(file))
                        encoder.Save(fs);
                    _viewModel.AddAttachmentFiles(new[] { file });
                }
            }
            catch { /* 剪贴板异常忽略 */ }
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0 && !_viewModel.IsBusy)
        {
            _viewModel.SendCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnAddAttachment(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "添加图片 / 文件",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp"
                     + "|文档|*.txt;*.md;*.log;*.json;*.yaml;*.yml;*.xml;*.csv;*.docx;*.pdf;*.ini;*.conf;*.properties;*.sql;*.sh;*.ps1;*.cs;*.java;*.py;*.js;*.ts;*.go;*.html;*.css"
                     + "|所有文件|*.*"
        };
        if (dialog.ShowDialog() == true)
            _viewModel.AddAttachmentFiles(dialog.FileNames);
    }

    private void OnRemoveAttachment(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AgentAttachment attachment)
            _viewModel.RemoveAttachment(attachment);
    }

    private void OnComposerDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnComposerDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            _viewModel.AddAttachmentFiles(files);
        e.Handled = true;
    }

    private void OnCopyAnswer(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AgentTurn turn && !string.IsNullOrEmpty(turn.AssistantText))
        {
            try { Clipboard.SetText(turn.AssistantText); } catch { /* ignore */ }
        }
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        var session = _contextSession ?? _viewModel.SelectedSession;
        if (session is null)
            return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"litssh-agent-{DateTime.Now:yyyyMMdd-HHmmss}.md",
            Filter = "Markdown 文件|*.md|文本文件|*.txt|所有文件|*.*"
        };
        if (dialog.ShowDialog() != true)
            return;

        var sb = new StringBuilder();
        if (session.Id == _viewModel.CurrentSessionId)
        {
            foreach (var turn in _viewModel.Turns)
            {
                sb.AppendLine($"## 🧑 用户 · {turn.UserTime}");
                sb.AppendLine();
                sb.AppendLine(turn.UserText);
                sb.AppendLine();

                if (turn.Steps.Count > 0)
                {
                    sb.AppendLine($"<details><summary>工具过程 ({turn.Steps.Count})</summary>");
                    sb.AppendLine();
                    foreach (var step in turn.Steps)
                        sb.AppendLine($"- {step.Label}：{step.Detail.Replace("\n", " ")}");
                    sb.AppendLine();
                    sb.AppendLine("</details>");
                    sb.AppendLine();
                }

                if (!string.IsNullOrEmpty(turn.AssistantText) || !string.IsNullOrEmpty(turn.Note))
                {
                    sb.AppendLine($"## 🤖 助手 · {turn.Footer}");
                    sb.AppendLine();
                    if (!string.IsNullOrEmpty(turn.AssistantText))
                    {
                        sb.AppendLine(turn.AssistantText);
                        sb.AppendLine();
                    }
                    if (!string.IsNullOrEmpty(turn.Note))
                    {
                        sb.AppendLine($"> {turn.Note}");
                        sb.AppendLine();
                    }
                }
            }
        }
        else
        {
            foreach (var row in await _viewModel.GetMessagesAsync(session.Id))
            {
                sb.AppendLine(row.Role == "user" ? $"## 🧑 用户 · {row.Timestamp}" : "## 🤖 助手");
                sb.AppendLine();
                sb.AppendLine(row.Content);
                sb.AppendLine();
            }
        }

        try
        {
            File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 导出失败忽略
        }
    }
}
