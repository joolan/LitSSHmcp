using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace LitSSHmcp.App.Controls;

public partial class PathPicker : UserControl
{
    public PathPicker()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(PathPicker),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PathPicker), new PropertyMetadata("选择路径"));

    public static readonly DependencyProperty FilterProperty = DependencyProperty.Register(
        nameof(Filter), typeof(string), typeof(PathPicker), new PropertyMetadata("所有文件|*.*"));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(PathPicker), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ButtonTextProperty = DependencyProperty.Register(
        nameof(ButtonText), typeof(string), typeof(PathPicker), new PropertyMetadata("浏览…"));

    public static readonly DependencyProperty PickFolderProperty = DependencyProperty.Register(
        nameof(PickFolder), typeof(bool), typeof(PathPicker), new PropertyMetadata(false));

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Filter
    {
        get => (string)GetValue(FilterProperty);
        set => SetValue(FilterProperty, value);
    }

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public string ButtonText
    {
        get => (string)GetValue(ButtonTextProperty);
        set => SetValue(ButtonTextProperty, value);
    }

    public bool PickFolder
    {
        get => (bool)GetValue(PickFolderProperty);
        set => SetValue(PickFolderProperty, value);
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        if (PickFolder)
        {
            var folderDialog = new OpenFolderDialog { Title = Title };
            if (folderDialog.ShowDialog(owner) == true)
                Value = folderDialog.FolderName;
            return;
        }

        var fileDialog = new OpenFileDialog
        {
            Title = Title,
            Filter = string.IsNullOrWhiteSpace(Filter) ? "所有文件|*.*" : Filter
        };
        if (fileDialog.ShowDialog(owner) == true)
            Value = fileDialog.FileName;
    }
}
