using System.Windows;

namespace LitSSHmcp.App.Services;

/// <summary>ViewModel 的对话框边界：避免业务代码直接依赖 <see cref="MessageBox"/>，便于测试与统一交互。</summary>
public interface IUserDialogService
{
    bool Confirm(string message, string title, bool warning = true, bool defaultNo = false);

    void Info(string message, string title);

    void Warn(string message, string title);

    void Error(string message, string title);
}

/// <summary>基于 WPF <see cref="MessageBox"/> 的默认实现（仅在宿主/无注入场景使用）。</summary>
public sealed class UserDialogService : IUserDialogService
{
    public bool Confirm(string message, string title, bool warning = true, bool defaultNo = false)
    {
        var result = MessageBox.Show(message, title, MessageBoxButton.YesNo,
            warning ? MessageBoxImage.Warning : MessageBoxImage.Question,
            defaultNo ? MessageBoxResult.No : MessageBoxResult.Yes);
        return result == MessageBoxResult.Yes;
    }

    public void Info(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void Warn(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void Error(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
