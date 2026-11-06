using System.IO;
using System.Windows;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.Toolbox;
using LitSSHmcp.App.ViewModels;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class ToolboxItemEditWindow : FluentWindow
{
    private ToolboxItemEditViewModel ViewModel => (ToolboxItemEditViewModel)DataContext;

    public ToolboxItemEditWindow(ToolboxItem? item = null)
    {
        InitializeComponent();
        DataContext = new ToolboxItemEditViewModel(item);
        Title = ViewModel.WindowTitle;
    }

    public ToolboxItem? Result { get; private set; }

    private void OnBrowseTarget(object sender, RoutedEventArgs e)
    {
        var type = ViewModel.Type;
        if (type == ToolboxItemType.Folder)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择文件夹",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(ViewModel.Target) ? ViewModel.Target : string.Empty
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                ViewModel.Target = dialog.SelectedPath;
            return;
        }

        if (type == ToolboxItemType.Url)
            return;

        var fileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择目标",
            Filter = type == ToolboxItemType.Script
                ? "脚本|*.bat;*.cmd;*.ps1|所有文件|*.*"
                : type == ToolboxItemType.Application
                    ? "程序|*.exe;*.lnk|所有文件|*.*"
                    : "所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (fileDialog.ShowDialog() == true)
        {
            ViewModel.Target = fileDialog.FileName;
            if (string.IsNullOrWhiteSpace(ViewModel.Name))
                ViewModel.Name = Path.GetFileNameWithoutExtension(fileDialog.FileName);
        }
    }

    private void OnBrowseWorkingDirectory(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择工作目录",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(ViewModel.WorkingDirectory) ? ViewModel.WorkingDirectory : string.Empty
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            ViewModel.WorkingDirectory = dialog.SelectedPath;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Validate() is { } error)
        {
            new UserDialogService().Warn(error, "工具箱");
            return;
        }

        Result = ViewModel.ToModel();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
