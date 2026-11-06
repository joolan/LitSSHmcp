using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace LitSSHmcp.App.Controls;

/// <summary>AvalonEdit 搜索高亮：绘制全部命中，当前命中使用更强强调色。</summary>
public sealed class SearchHighlightRenderer : IBackgroundRenderer
{
    private IReadOnlyList<(int Offset, int Length)> _matches = Array.Empty<(int, int)>();
    private int _currentOffset = -1;
    private int _currentLength;

    public KnownLayer Layer => KnownLayer.Selection;

    public Brush MatchBrush { get; set; } = new SolidColorBrush(Color.FromArgb(0x50, 0x4C, 0xC2, 0xFF));
    public Brush CurrentMatchBrush { get; set; } = new SolidColorBrush(Color.FromArgb(0xA0, 0x4C, 0xC2, 0xFF));

    public void SetMatches(IReadOnlyList<(int Offset, int Length)> matches, int currentOffset, int currentLength)
    {
        _matches = matches;
        _currentOffset = currentOffset;
        _currentLength = currentLength;
    }

    public void Clear()
    {
        _matches = Array.Empty<(int, int)>();
        _currentOffset = -1;
        _currentLength = 0;
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_matches.Count == 0 || textView.Document is null)
            return;

        foreach (var (offset, length) in _matches)
        {
            if (offset < 0 || length <= 0 || offset + length > textView.Document.TextLength)
                continue;

            var isCurrent = offset == _currentOffset && length == _currentLength;
            var brush = isCurrent ? CurrentMatchBrush : MatchBrush;
            var segment = new TextSegment { StartOffset = offset, Length = length };
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                drawingContext.DrawRectangle(brush, null, rect);
        }
    }
}
