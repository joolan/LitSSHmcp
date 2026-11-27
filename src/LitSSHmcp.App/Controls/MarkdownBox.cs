using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using LitSSHmcp.App.Services;

namespace LitSSHmcp.App.Controls;

/// <summary>把 Markdown 文本渲染为只读富文本的 WPF 控件（用于 AI 助手回答）。</summary>
public sealed class MarkdownBox : FlowDocumentScrollViewer
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownBox),
        new PropertyMetadata(string.Empty, OnMarkdownChanged));

    /// <summary>
    /// 是否处于流式输出中。长文本流式输出时不再每个增量都做一次完整 Markdown 解析
    /// （否则整体复杂度是 O(n²)，长回答会明显卡顿），而是用纯文本增量刷新，
    /// 流式结束后再做一次完整渲染。
    /// </summary>
    public static readonly DependencyProperty IsStreamingProperty = DependencyProperty.Register(
        nameof(IsStreaming), typeof(bool), typeof(MarkdownBox),
        new PropertyMetadata(false, OnIsStreamingChanged));

    /// <summary>流式输出超过该长度后切换为轻量纯文本预览；短回答仍保持实时 Markdown。</summary>
    private const int StreamingPlainTextThreshold = 12000;

    private static readonly FontFamily StreamFont = new("Microsoft YaHei");

    private FlowDocument? _streamDocument;
    private Run? _streamRun;

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public bool IsStreaming
    {
        get => (bool)GetValue(IsStreamingProperty);
        set => SetValue(IsStreamingProperty, value);
    }

    public MarkdownBox()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        IsToolBarVisible = false;
        Background = Brushes.Transparent;
        Padding = new Thickness(0);
        Document = MarkdownRenderer.Render(string.Empty);

        // 文档内控件（复制按钮、可选中代码框）获得焦点时，FlowDocumentScrollViewer 会对自己发起一次
        // BringIntoView；外层聊天 ScrollViewer 收到后会把整个 Markdown 区块滚到顶部，表现为点“复制”页面跳动。
        AddHandler(RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler(OnRequestBringIntoView),
            handledEventsToo: true);

        ThemeService.ThemeChanged += OnThemeChanged;
        Unloaded += (_, _) => ThemeService.ThemeChanged -= OnThemeChanged;
    }

    /// <summary>
    /// 只拦 FlowDocumentScrollViewer 自己发起的“把我滚到可见区”请求，保留文档内子元素
    /// （按钮/可选代码）正常的 BringIntoView，避免键盘导航和拖选失去滚动跟随。
    /// </summary>
    private void OnRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (ReferenceEquals(e.Source, this))
            e.Handled = true;
    }

    // 主题切换后按新 token 重渲染（FlowDocument 用的是具体画刷，不会自动跟随动态资源）。
    private void OnThemeChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(OnThemeChanged);
            return;
        }
        // 主题切换后重建流式预览文档，让纯文本预览也使用新主题画刷。
        _streamDocument = null;
        _streamRun = null;
        UpdateDocument();
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
        ((MarkdownBox)d).UpdateDocument();

    private static void OnIsStreamingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MarkdownBox)d).UpdateDocument();

    private void UpdateDocument()
    {
        var text = Markdown ?? string.Empty;

        // 短文本的 Markdown 渲染很便宜，保持实时格式化体验；
        // 长文本改用单段落纯文本预览，避免流式期间反复解析/重建整个文档。
        if (IsStreaming && text.Length > StreamingPlainTextThreshold)
        {
            if (_streamDocument is null)
            {
                _streamRun = new Run();
                _streamDocument = new FlowDocument
                {
                    PagePadding = new Thickness(0),
                    FontFamily = StreamFont,
                    FontSize = 13,
                    Foreground = ThemeBrushes.TextPrimary
                };
                _streamDocument.Blocks.Add(new Paragraph(_streamRun) { Margin = new Thickness(0, 2, 0, 2) });
            }

            _streamRun!.Text = text;
            if (!ReferenceEquals(Document, _streamDocument))
                Document = _streamDocument;
            return;
        }

        _streamDocument = null;
        _streamRun = null;
        // 流式期间代码块用轻量 TextBlock，结束后再换成可选中的 RichTextBox，避免每个增量重建富文本框。
        Document = MarkdownRenderer.Render(text, selectableCode: !IsStreaming);
    }
}
