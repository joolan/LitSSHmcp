using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace LitSSHmcp.App.Controls;

/// <summary>在线文件编辑器查找：高亮全部匹配项，并区分当前匹配。</summary>
public sealed class FindHighlightTransformer : DocumentColorizingTransformer
{
    private static readonly Brush MatchBrush = Freeze(Color.FromArgb(70, 255, 193, 7));
    private static readonly Brush CurrentBrush = Freeze(Color.FromArgb(120, 255, 152, 0));

    public IReadOnlyList<int> Matches { get; set; } = Array.Empty<int>();
    public string Query { get; set; } = string.Empty;
    public int CurrentIndex { get; set; } = -1;

    protected override void ColorizeLine(DocumentLine line)
    {
        if (string.IsNullOrEmpty(Query) || Matches.Count == 0)
            return;

        for (var i = 0; i < Matches.Count; i++)
        {
            var start = Matches[i];
            if (start < line.Offset || start + Query.Length > line.EndOffset)
                continue;
            var end = start + Query.Length;
            var brush = i == CurrentIndex ? CurrentBrush : MatchBrush;
            try
            {
                ChangeLinePart(start, end, element =>
                    element.TextRunProperties.SetBackgroundBrush(brush));
            }
            catch
            {
                // 文档快速变化时忽略失效区间
            }
        }
    }

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
