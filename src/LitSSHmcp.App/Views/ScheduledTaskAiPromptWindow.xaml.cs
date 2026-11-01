using System.Windows;
using LitSSHmcp.App.Services;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class ScheduledTaskAiPromptWindow : FluentWindow
{
    private readonly Func<string, bool, Task<bool>> _sendAsync;
    private bool _sending;

    public ScheduledTaskAiPromptWindow(string prompt, Func<string, bool, Task<bool>> sendAsync, string? description = null)
    {
        InitializeComponent();
        _sendAsync = sendAsync;
        PromptBox.Text = prompt;
        if (!string.IsNullOrWhiteSpace(description))
        {
            HintText.Text = description;
        }

        WindowLayout.Attach(this, "scheduled-task-ai-prompt");
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(PromptBox.Text ?? string.Empty);
            StatusText.Text = "已复制。";
        }
        catch
        {
            StatusText.Text = "复制失败。";
        }
    }

    private void OnSend(object sender, RoutedEventArgs e)
        => AsyncOperation.Run("发送到 AI 运维助手", SendAsync);

    private async Task SendAsync()
    {
        if (_sending)
        {
            return;
        }

        _sending = true;
        StatusText.Text = "正在发送…";
        try
        {
            var success = await _sendAsync(PromptBox.Text ?? string.Empty, true);
            if (success)
            {
                DialogResult = true;
                Close();
                return;
            }

            StatusText.Text = "已取消。可修改内容后重试。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "发送失败：" + ex.Message;
        }
        finally
        {
            _sending = false;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_sending)
        {
            return;
        }

        DialogResult = false;
        Close();
    }
}
