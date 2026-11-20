using System.Windows;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace LitSSHmcp.App.Controls;

/// <summary>为 nginx/properties/ini/toml/dockerfile 等配置文本提供轻量语法着色。</summary>
public sealed class ConfigSyntaxTransformer : DocumentColorizingTransformer
{
    private readonly string _language;

    public ConfigSyntaxTransformer(string language) => _language = language;

    protected override void ColorizeLine(DocumentLine line)
    {
        var text = CurrentContext.Document.GetText(line);
        var offset = line.Offset;
        foreach (var token in SyntaxHighlighter.Highlight(text, _language))
        {
            if (token.Text.Length == 0)
                continue;
            var start = offset;
            var end = Math.Min(offset + token.Text.Length, line.EndOffset);
            if (start < line.EndOffset && end > start)
            {
                try
                {
                    ChangeLinePart(start, end, element =>
                        element.TextRunProperties.SetForegroundBrush(token.Brush));
                }
                catch
                {
                    // 文档快速变化时忽略失效区间
                }
            }
            offset += token.Text.Length;
            if (offset >= line.EndOffset)
                break;
        }
    }
}
