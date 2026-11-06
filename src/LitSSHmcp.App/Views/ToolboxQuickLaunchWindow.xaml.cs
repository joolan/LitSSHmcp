using System.Windows;
using System.Windows.Input;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.App.Views.Tools;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class ToolboxQuickLaunchWindow : FluentWindow
{
    private readonly ToolboxQuickLaunchViewModel _viewModel;

    public ToolboxQuickLaunchWindow(ToolboxQuickLaunchViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.BuiltinRequested += OnBuiltinRequested;
        Loaded += (_, _) =>
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            AsyncOperation.Run("加载快速启动", LoadAsync);
        };
        Closed += (_, _) => _viewModel.BuiltinRequested -= OnBuiltinRequested;
        WindowLayout.Attach(this, "toolbox.quick");
    }

    private async Task LoadAsync()
    {
        await _viewModel.LoadAsync();
        if (_viewModel.Results.Count > 0)
            ResultsList.SelectedIndex = 0;
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Enter:
                _ = LaunchSelectedAsync();
                e.Handled = true;
                break;
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (_viewModel.Results.Count == 0)
            return;
        var index = Math.Clamp(ResultsList.SelectedIndex + delta, 0, _viewModel.Results.Count - 1);
        ResultsList.SelectedIndex = index;
        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
    }

    private void OnResultsDoubleClick(object sender, MouseButtonEventArgs e) => _ = LaunchSelectedAsync();

    private async Task LaunchSelectedAsync()
    {
        var item = ResultsList.SelectedItem as ToolboxItemViewModel ?? _viewModel.SelectedItem;
        if (item is null)
            return;

        if (await _viewModel.LaunchAsync(item))
            Close();
        else
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        }
    }

    private void OnBuiltinRequested(string id)
    {
        if (string.Equals(id, "hosts", StringComparison.OrdinalIgnoreCase))
        {
            new HostsEditorWindow().Show();
            Close();
            return;
        }

        if (string.Equals(id, "password", StringComparison.OrdinalIgnoreCase))
        {
            new PasswordGeneratorWindow().Show();
            Close();
            return;
        }

        if (string.Equals(id, "vault", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var window = new EncryptedVaultWindow();
                window.Show();
                window.Activate();
            }
            catch (Exception ex)
            {
                new UserDialogService().Error("打开加密文件柜失败：" + ex.Message, "工具箱");
            }
            Close();
        }
    }
}
