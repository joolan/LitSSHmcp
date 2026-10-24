using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Controls;

[ContentProperty(nameof(Actions))]
public partial class PageHeader : UserControl
{
    public PageHeader()
    {
        InitializeComponent();
    }

    /// <summary>聚焦页头操作区中的第一个输入框（搜索框），并全选现有内容。</summary>
    public bool FocusFirstInput()
    {
        var box = FindFirst<System.Windows.Controls.TextBox>(this);
        if (box == null)
            return false;

        box.Focus();
        box.SelectAll();
        return true;
    }

    private static T? FindFirst<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;

            var nested = FindFirst<T>(child);
            if (nested != null)
                return nested;
        }

        return null;
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(PageHeader), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(SymbolRegular), typeof(PageHeader), new PropertyMetadata(SymbolRegular.Document24));

    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions), typeof(object), typeof(PageHeader), new PropertyMetadata(null));

    public static readonly DependencyProperty StatusContentProperty = DependencyProperty.Register(
        nameof(StatusContent), typeof(object), typeof(PageHeader), new PropertyMetadata(null));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public SymbolRegular Icon
    {
        get => (SymbolRegular)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    public object? StatusContent
    {
        get => GetValue(StatusContentProperty);
        set => SetValue(StatusContentProperty, value);
    }
}
