using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace LitSSHmcp.App.Controls;

/// <summary>把 Markdown 文本渲染为只读富文本的 WPF 控件（用于 AI 助手回答）。</summary>
public sealed class MarkdownBox : FlowDocumentScrollViewer
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownBox),
        new PropertyMetadata(string.Empty, OnMarkdownChanged));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public MarkdownBox()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        IsToolBarVisible = false;
        Background = Brushes.Transparent;
        Padding = new Thickness(0);
        Document = MarkdownRenderer.Render(string.Empty);
    }

    // 本控件不自行滚动（内容随外层聊天区滚动），把滚轮事件转发给父级，避免鼠标停在 Markdown 上时外层无法滚动。
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        e.Handled = true;

        var parent = VisualTreeHelper.GetParent(this) as UIElement;
        parent?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = parent
        });
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MarkdownBox)d).Document = MarkdownRenderer.Render(e.NewValue as string);
}
