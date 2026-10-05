using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LitSSHmcp.App.Controls;

/// <summary>
/// 轻量 Markdown → WPF FlowDocument 渲染（无第三方依赖）。
/// 支持：标题、粗体/斜体/删除线、行内代码、围栏代码块(高亮+复制按钮)、表格、有序/无序列表(含嵌套)、
/// 任务清单、引用、水平线、链接、图片、脚注。
/// </summary>
public static class MarkdownRenderer
{
    public static FlowDocument Render(string? markdown) => new Renderer().Render(markdown);

    private sealed class Renderer
    {
        private static readonly Regex InlinePattern = new(
            @"(\*\*[^*]+\*\*)|(`[^`]+`)|(~~[^~]+~~)|(\*[^*]+\*)|(_[^_]+_)|(\[[^\]]+\]\([^)]+\))|(\[\^[^\]]+\])",
            RegexOptions.Compiled);

        private static readonly Regex ListItemPattern = new(@"^(\s*)([-*+]|\d+\.)\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex TaskItemPattern = new(@"^\[([ xX])\]\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex HrPattern = new(@"^(\-{3,}|\*{3,}|_{3,})$", RegexOptions.Compiled);
        private static readonly Regex ImagePattern = new(@"^!\[([^\]]*)\]\(([^)\s]+)\)$", RegexOptions.Compiled);
        private static readonly Regex FootnoteDefPattern = new(@"^\[\^([^\]]+)\]:\s*(.*)$", RegexOptions.Compiled);

        private readonly Dictionary<string, int> _footnotes = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(int Number, string Text)> _footnoteTexts = new();

        public FlowDocument Render(string? markdown)
        {
            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontFamily = new FontFamily("Microsoft YaHei"),
                FontSize = 13,
                Foreground = ThemeBrushes.TextPrimary
            };

            var lines = (markdown ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            StripFootnoteDefinitions(lines);

            var i = 0;
            while (i < lines.Length)
            {
                var line = lines[i];
                if (line is null) { i++; continue; }
                var trimmed = line.TrimEnd();

                // 围栏代码块
                var fence = trimmed.TrimStart();
                if (fence.StartsWith("```", StringComparison.Ordinal))
                {
                    doc.Blocks.Add(BuildCodeBlock(lines, ref i));
                    continue;
                }

                if (trimmed.Length == 0) { i++; continue; }

                var level = HeadingLevel(trimmed);
                if (level > 0)
                {
                    var p = new Paragraph
                    {
                        FontSize = Math.Max(14, 20 - level * 1.5),
                        FontWeight = FontWeights.Bold,
                        Margin = new Thickness(0, 8, 0, 3)
                    };
                    AppendInline(p, trimmed.TrimStart('#').Trim());
                    doc.Blocks.Add(p);
                    i++;
                    continue;
                }

                if (HrPattern.IsMatch(trimmed.Trim()))
                {
                    doc.Blocks.Add(new Paragraph(new Run(new string('─', 40)))
                    {
                        Foreground = ThemeBrushes.TextSecondary,
                        Margin = new Thickness(0, 4, 0, 4)
                    });
                    i++;
                    continue;
                }

                var image = ImagePattern.Match(trimmed.Trim());
                if (image.Success)
                {
                    doc.Blocks.Add(BuildImage(image.Groups[1].Value, image.Groups[2].Value));
                    i++;
                    continue;
                }

                if (ListItemPattern.IsMatch(line))
                {
                    doc.Blocks.Add(BuildList(lines, ref i));
                    continue;
                }

                var t = trimmed.TrimStart();
                if (t.StartsWith("> ", StringComparison.Ordinal))
                {
                    var p = new Paragraph
                    {
                        Foreground = ThemeBrushes.TextSecondary,
                        FontStyle = FontStyles.Italic,
                        BorderBrush = ThemeBrushes.Border,
                        BorderThickness = new Thickness(3, 0, 0, 0),
                        Padding = new Thickness(8, 0, 0, 0),
                        Margin = new Thickness(0, 2, 0, 2)
                    };
                    AppendInline(p, t[2..]);
                    doc.Blocks.Add(p);
                    i++;
                    continue;
                }

                if (IsTableHeader(lines, i))
                {
                    doc.Blocks.Add(BuildTable(lines, ref i));
                    continue;
                }

                var para = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
                AppendInline(para, trimmed);
                doc.Blocks.Add(para);
                i++;
            }

            AppendFootnotes(doc);
            return doc;
        }

        // ---------- 代码块（高亮 + 复制按钮） ----------

        private Block BuildCodeBlock(string[] lines, ref int i)
        {
            var opening = lines[i]!.TrimStart();
            var language = opening.Length > 3 ? opening[3..].Trim() : string.Empty;

            var sb = new StringBuilder();
            i++;
            while (i < lines.Length && lines[i] is not null &&
                   !lines[i].TrimEnd().TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                sb.AppendLine(lines[i]);
                i++;
            }
            if (i < lines.Length) i++;   // 跳过结束围栏
            var code = sb.ToString().TrimEnd('\n');

            var text = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeBrushes.CodeText,
                Background = Brushes.Transparent
            };
            foreach (var token in SyntaxHighlighter.Highlight(code, language))
                text.Inlines.Add(new Run(token.Text) { Foreground = token.Brush, FontWeight = token.Bold ? FontWeights.Bold : FontWeights.Normal });

            var copy = new Button
            {
                Content = "复制",
                Padding = new Thickness(8, 1, 8, 1),
                Margin = new Thickness(6, 0, 0, 0),
                FontSize = 11,
                Tag = code,
                ToolTip = "复制该代码块"
            };
            copy.Click += (_, _) =>
            {
                try { Clipboard.SetText((string)copy.Tag); } catch { /* 剪贴板偶发占用忽略 */ }
            };

            var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 2) };
            DockPanel.SetDock(copy, Dock.Right);
            header.Children.Add(copy);
            header.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(language) ? "code" : language,
                Foreground = ThemeBrushes.TextSecondary,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            });

            var panel = new StackPanel();
            panel.Children.Add(header);
            panel.Children.Add(text);

            return new BlockUIContainer(new Border
            {
                Background = ThemeBrushes.CodeBg,
                BorderBrush = ThemeBrushes.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Child = panel
            })
            { Margin = new Thickness(0, 6, 0, 6) };
        }

        // ---------- 列表（含嵌套 + 任务清单） ----------

        private Block BuildList(string[] lines, ref int i)
        {
            var rootIndent = IndentOf(lines[i]);
            var root = new List { Margin = new Thickness(20, 0, 0, 0), Padding = new Thickness(0) };
            var stack = new List<Frame> { new(rootIndent, root) };

            while (i < lines.Length && lines[i] is not null && ListItemPattern.IsMatch(lines[i]))
            {
                var match = ListItemPattern.Match(lines[i]);
                var indent = match.Groups[1].Value.Length;
                var itemText = match.Groups[3].Value;

                while (stack.Count > 1 && indent < stack[^1].Indent)
                    stack.RemoveAt(stack.Count - 1);

                // 更深缩进 → 在上一项内嵌套子列表
                if (indent > stack[^1].Indent && stack[^1].Last is { } parentItem)
                {
                    var nested = new List
                    {
                        MarkerStyle = match.Groups[2].Value.EndsWith('.') ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Margin = new Thickness(0),
                        Padding = new Thickness(0)
                    };
                    parentItem.Blocks.Add(nested);
                    stack.Add(new Frame(indent, nested));
                }

                var target = stack[^1].List;
                if (target.MarkerStyle == TextMarkerStyle.None)
                    target.MarkerStyle = match.Groups[2].Value.EndsWith('.') ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc;

                var paragraph = new Paragraph { Margin = new Thickness(0) };
                var task = TaskItemPattern.Match(itemText);
                if (task.Success)
                {
                    paragraph.Inlines.Add(new Run(task.Groups[1].Value.Trim().Length > 0 ? "☑ " : "☐ ")
                    {
                        Foreground = task.Groups[1].Value.Trim().Length > 0 ? ThemeBrushes.Success : ThemeBrushes.TextSecondary
                    });
                    AppendInline(paragraph, task.Groups[2].Value);
                }
                else
                {
                    AppendInline(paragraph, itemText);
                }

                var listItem = new ListItem(paragraph);
                target.ListItems.Add(listItem);
                stack[^1].Last = listItem;
                i++;
            }

            return root;
        }

        private static int IndentOf(string line)
        {
            var count = 0;
            foreach (var ch in line)
            {
                if (ch == ' ') count++;
                else if (ch == '\t') count += 4;
                else break;
            }
            return count;
        }

        private sealed class Frame
        {
            public Frame(int indent, List list) { Indent = indent; List = list; }
            public int Indent { get; }
            public List List { get; }
            public ListItem? Last { get; set; }
        }

        // ---------- 表格 ----------

        private static bool IsTableHeader(string[] lines, int i)
        {
            if (i + 1 >= lines.Length || lines[i] is null || lines[i + 1] is null)
                return false;
            if (!lines[i]!.Contains('|'))
                return false;
            var separator = lines[i + 1]!.Trim();
            if (!separator.Contains('|') || !separator.Contains('-'))
                return false;
            foreach (var ch in separator)
                if (ch is not ('|' or '-' or ':' or ' ' or '\t'))
                    return false;
            return true;
        }

        private Block BuildTable(string[] lines, ref int i)
        {
            var headers = SplitCells(lines[i]!.Trim());
            i += 2;
            var columns = Math.Max(1, headers.Count);

            var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 4) };
            for (var c = 0; c < columns; c++) table.Columns.Add(new TableColumn());

            var group = new TableRowGroup();
            table.RowGroups.Add(group);

            var headerRow = new TableRow { Background = ThemeBrushes.Surface };
            for (var c = 0; c < columns; c++)
                headerRow.Cells.Add(MakeCell(c < headers.Count ? headers[c] : string.Empty, bold: true));
            group.Rows.Add(headerRow);

            while (i < lines.Length && lines[i] is not null)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || !line.Contains('|')) break;
                var cells = SplitCells(line);
                var row = new TableRow();
                for (var c = 0; c < columns; c++)
                    row.Cells.Add(MakeCell(c < cells.Count ? cells[c] : string.Empty, bold: false));
                group.Rows.Add(row);
                i++;
            }

            return table;
        }

        private TableCell MakeCell(string text, bool bold)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0), FontWeight = bold ? FontWeights.Bold : FontWeights.Normal };
            AppendInline(paragraph, text);
            return new TableCell(paragraph)
            {
                Padding = new Thickness(7, 3, 7, 3),
                BorderBrush = ThemeBrushes.Border,
                BorderThickness = new Thickness(0.5)
            };
        }

        private static List<string> SplitCells(string line)
        {
            var text = line.Trim();
            if (text.StartsWith('|')) text = text[1..];
            if (text.EndsWith('|')) text = text[..^1];
            return text.Split('|').Select(s => s.Trim()).ToList();
        }

        // ---------- 图片 ----------

        private static Block BuildImage(string alt, string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return new Paragraph(new Run($"![{alt}]({url})"));

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = uri;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.EndInit();
                bitmap.DownloadFailed += (_, _) => { /* 保持占位 */ };

                var image = new Image
                {
                    Source = bitmap,
                    Stretch = Stretch.Uniform,
                    MaxWidth = 900,
                    MaxHeight = 500,
                    ToolTip = alt
                };
                return new BlockUIContainer(image) { Margin = new Thickness(0, 4, 0, 4) };
            }
            catch
            {
                return new Paragraph(new Run($"![{alt}]({url})"));
            }
        }

        // ---------- 脚注 ----------

        private void StripFootnoteDefinitions(string?[] lines)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line is null) continue;
                var match = FootnoteDefPattern.Match(line.Trim());
                if (!match.Success) continue;

                var id = match.Groups[1].Value;
                if (!_footnotes.ContainsKey(id))
                {
                    var number = _footnotes.Count + 1;
                    _footnotes[id] = number;
                    _footnoteTexts.Add((number, match.Groups[2].Value));
                }
                lines[i] = null;
            }
        }

        private void AppendFootnotes(FlowDocument doc)
        {
            if (_footnoteTexts.Count == 0) return;

            doc.Blocks.Add(new Paragraph(new Run("脚注"))
            {
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Foreground = ThemeBrushes.TextSecondary,
                Margin = new Thickness(0, 8, 0, 2)
            });

            foreach (var (number, text) in _footnoteTexts.OrderBy(f => f.Number))
            {
                var p = new Paragraph { FontSize = 11, Foreground = ThemeBrushes.TextSecondary, Margin = new Thickness(12, 0, 0, 0) };
                p.Inlines.Add(new Run($"[{number}] "));
                AppendInline(p, text);
                doc.Blocks.Add(p);
            }
        }

        // ---------- 行内 ----------

        private static int HeadingLevel(string line)
        {
            var count = 0;
            while (count < line.Length && count < 6 && line[count] == '#') count++;
            return count > 0 && count < line.Length && line[count] == ' ' ? count : 0;
        }

        private void AppendInline(Paragraph paragraph, string text)
        {
            var pos = 0;
            foreach (Match match in InlinePattern.Matches(text))
            {
                if (match.Index > pos)
                    paragraph.Inlines.Add(new Run(text[pos..match.Index]));

                var token = match.Value;
                if (token.StartsWith("**", StringComparison.Ordinal))
                    paragraph.Inlines.Add(new Bold(new Run(token[2..^2])));
                else if (token.StartsWith('`'))
                    paragraph.Inlines.Add(new Run(token[1..^1])
                    {
                        FontFamily = new FontFamily("Consolas"),
                        Foreground = ThemeBrushes.CodeText,
                        Background = ThemeBrushes.CodeBg
                    });
                else if (token.StartsWith("~~", StringComparison.Ordinal))
                    paragraph.Inlines.Add(new Span(new Run(token[2..^2])) { TextDecorations = TextDecorations.Strikethrough });
                else if (token.StartsWith("^[", StringComparison.Ordinal))
                    paragraph.Inlines.Add(new Run(token));  // 占位，不会命中（脚注用 [^id]）
                else if (token.StartsWith("[^", StringComparison.Ordinal))
                    AddFootnoteRef(paragraph, token);
                else if (token.StartsWith('*') || token.StartsWith('_'))
                    paragraph.Inlines.Add(new Italic(new Run(token[1..^1])));
                else if (token.StartsWith('['))
                    AddLink(paragraph, token);

                pos = match.Index + match.Length;
            }

            if (pos < text.Length)
                paragraph.Inlines.Add(new Run(text[pos..]));
        }

        private void AddFootnoteRef(Paragraph paragraph, string token)
        {
            var id = token[2..^1];
            if (_footnotes.TryGetValue(id, out var number))
            {
                paragraph.Inlines.Add(new Run($"[{number}]")
                {
                    FontSize = 10,
                    BaselineAlignment = BaselineAlignment.Superscript,
                    Foreground = ThemeBrushes.TextSecondary
                });
            }
            else
            {
                paragraph.Inlines.Add(new Run(token));
            }
        }

        private static void AddLink(Paragraph paragraph, string token)
        {
            var close = token.IndexOf(']');
            var open = token.IndexOf('(');
            if (close < 0 || open < 0 || open < close)
            {
                paragraph.Inlines.Add(new Run(token));
                return;
            }

            var label = token[1..close];
            var url = token[(open + 1)..^1];
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                paragraph.Inlines.Add(new Run(label));
                return;
            }

            var hyperlink = new Hyperlink(new Run(label)) { NavigateUri = uri, ToolTip = url };
            hyperlink.RequestNavigate += (_, e) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
                catch { /* 打不开忽略 */ }
            };
            paragraph.Inlines.Add(hyperlink);
        }
    }
}
