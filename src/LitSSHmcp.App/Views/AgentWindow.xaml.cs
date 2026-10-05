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
        DataContext = _viewModel;
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

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        new AgentSettingsWindow(_configService) { Owner = this }.ShowDialog();
        _ = _viewModel.ReloadProvidersAsync();
    }

    // 右键会话：选中该项（让右键菜单作用于该会话）
    private void OnSessionRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBoxItem item)
            item.IsSelected = true;
    }

    private void OnDeleteSession(object sender, RoutedEventArgs e) =>
        _viewModel.DeleteSessionCommand.Execute(null);

    private void OnClearDisplay(object sender, RoutedEventArgs e) =>
        _viewModel.ClearCommand.Execute(null);

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
        var session = _viewModel.SelectedSession;
        if (session is null)
            return;
        var name = TextInputDialog.Prompt(this, "重命名会话", "会话名称:", session.Title);
        if (name is not null)
            _ = _viewModel.RenameSessionAsync(name);
    }

    private void OnEditTurn(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AgentTurn turn)
            _viewModel.BeginEdit(turn);
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0 && !_viewModel.IsBusy)
        {
            _viewModel.SendCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnCopyAnswer(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AgentTurn turn && !string.IsNullOrEmpty(turn.AssistantText))
        {
            try { Clipboard.SetText(turn.AssistantText); } catch { /* ignore */ }
        }
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"litssh-agent-{DateTime.Now:yyyyMMdd-HHmmss}.md",
            Filter = "Markdown 文件|*.md|文本文件|*.txt|所有文件|*.*"
        };
        if (dialog.ShowDialog() != true)
            return;

        var sb = new StringBuilder();
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
