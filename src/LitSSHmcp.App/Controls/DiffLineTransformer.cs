using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using LitSSHmcp.App.Services.RemoteFiles;

namespace LitSSHmcp.App.Controls;

/// <summary>左右并排 diff 的差异行背景色。</summary>
public sealed class DiffLineTransformer : DocumentColorizingTransformer
{
    private static readonly Brush RemovedBrush = Freeze(Color.FromArgb(45, 220, 50, 50));
    private static readonly Brush AddedBrush = Freeze(Color.FromArgb(45, 40, 170, 70));
    private static readonly Brush BlankBrush = Freeze(Color.FromArgb(32, 128, 128, 128));

    private readonly IReadOnlyList<DiffLine> _lines;
    private readonly bool _left;

    public DiffLineTransformer(IReadOnlyList<DiffLine> lines, bool left)
    {
        _lines = lines;
        _left = left;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        var index = line.LineNumber - 1;
        if (index < 0 || index >= _lines.Count)
            return;
        var kind = _lines[index].Kind;
        var brush = kind switch
        {
            DiffLineKind.Removed when _left => RemovedBrush,
            DiffLineKind.Added when !_left => AddedBrush,
            DiffLineKind.Blank => BlankBrush,
            _ => null
        };
        if (brush is null)
            return;
        try
        {
            ChangeLinePart(line.Offset, line.EndOffset, element =>
                element.TextRunProperties.SetBackgroundBrush(brush));
        }
        catch
        {
            // 文档变化时忽略失效行
        }
    }

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
