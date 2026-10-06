using System.IO;
using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.Core.Models;

// 【同步约定 · 请勿删除】MCP 工具发生任何变动（新增/改名/删除、参数或描述变化、分组变化）时, 以下三处必须同步更新:
//   1) MCP 服务器端: src/LitSSHmcp.McpServer/Tools/*.cs 的 [McpServerTool]/[Description] 注解、
//      UsageGuideTools.GetUsageGuide() 内置清单、Program.cs 的 WithTools<T>() 注册;
//   2) 文档:         docs/TOOLS.md (本视图内容的唯一事实来源, 见该文件顶部"同步约定");
//   3) App 端:       本视图 McpToolsView —— 内容由 docs/TOOLS.md 嵌入, 解析约定见下, 通常无需改代码。
// 解析约定(见 docs/TOOLS.md「文档结构约定」):
//   - "## <中文分组名>（<分组键>）" = 工具分组, 分组键须与 config.json 的 tools.enabledGroups / App「工具分组设置」一致;
//   - "### `工具名`" = 具体工具(取反引号内的名字, 计入工具数); 后随的 "（只读）" 之类括注忽略;
//   - 其它 "### 子标题"(不带反引号) = 章节内说明小标题, 不计入工具。
// 只同步其一, AI 客户端拿到的工具说明就会与实际能力不一致。
namespace LitSSHmcp.App.Views;

public partial class McpToolsView : UserControl
{
    private const string DocsResourceName = "LitSSHmcp.App.docs.TOOLS.md";

    private readonly List<DocEntry> _entries;

    public McpToolsView()
    {
        InitializeComponent();

        _entries = LoadDocs();
        UpdateHeader();

        ToolList.ItemsSource = _entries;
        if (_entries.Count > 0)
            ToolList.SelectedIndex = 0;
    }

    private static List<DocEntry> LoadDocs()
    {
        var assembly = typeof(McpToolsView).Assembly;
        using var stream = assembly.GetManifestResourceStream(DocsResourceName);
        if (stream == null)
        {
            return new List<DocEntry>
            {
                new DocEntry("工具说明文档缺失", false, null,
                    $"未找到嵌入资源 {DocsResourceName}。\n请检查 LitSSHmcp.App.csproj 中的 EmbeddedResource 配置(文档来源: docs/TOOLS.md)。")
            };
        }

        using var reader = new StreamReader(stream);
        return ParseMarkdown(reader.ReadToEnd());
    }

    // 把 docs/TOOLS.md 解析成条目: "## " 为分组(左列加粗), "### `name`" 为具体工具(缩进显示);
    // 其它 "### " 子标题并入当前章节内容, 不单独成条目。
    private static List<DocEntry> ParseMarkdown(string markdown)
    {
        var entries = new List<DocEntry>();
        var buffer = new List<string>();

        // 当前标题: 初始为文档开头的"总述"(H1 与顶部同步约定, 在任何 ## 之前)
        var title = "文档总述与同步约定";
        string? groupKey = null;
        var isTool = false;
        var hasCurrent = false;   // 当前标题是否已作为待输出条目(即使是空内容也输出, 避免"只有工具无简介"的分组被丢弃)

        void StartHeading(string newTitle, bool newIsTool, string? newGroupKey)
        {
            if (hasCurrent || buffer.Any(line => !string.IsNullOrWhiteSpace(line)))
            {
                entries.Add(new DocEntry(title, isTool, groupKey, string.Join(Environment.NewLine, buffer).Trim()));
            }
            buffer.Clear();
            title = newTitle;
            isTool = newIsTool;
            groupKey = newGroupKey;
            hasCurrent = true;
        }

        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                var heading = line[3..].Trim();
                StartHeading(heading, false, ExtractGroupKey(heading));
            }
            else if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                var heading = line[4..].Trim();
                if (heading.StartsWith('`'))
                    StartHeading(ExtractToolName(heading), true, null);
                else
                    buffer.Add(line);   // 非工具子标题: 作为章节内容保留, 不计入工具
            }
            else
            {
                buffer.Add(line);
            }
        }

        if (hasCurrent || buffer.Any(line => !string.IsNullOrWhiteSpace(line)))
            entries.Add(new DocEntry(title, isTool, groupKey, string.Join(Environment.NewLine, buffer).Trim()));

        return entries;
    }

    // 从 "### `工具名`（只读）" 取反引号内的工具名; 无闭合反引号时退化为去反引号。
    private static string ExtractToolName(string heading)
    {
        heading = heading.Trim();
        if (!heading.StartsWith('`')) return heading;
        var end = heading.IndexOf('`', 1);
        return end > 1 ? heading[1..end] : heading.Trim('`').Trim();
    }

    // 从 "## <中文分组名>（<分组键>）" 提取分组键; 非分组标题返回 null。
    private static string? ExtractGroupKey(string title)
    {
        var open = title.LastIndexOf('（');
        var close = title.LastIndexOf('）');
        if (open < 0 || close <= open)
            return null;

        var key = title[(open + 1)..close].Trim();
        return ToolGroups.All.Any(g => g.Equals(key, StringComparison.OrdinalIgnoreCase)) ? key : null;
    }

    private void UpdateHeader(string? prefix = null)
    {
        var toolCount = _entries.Count(e => e.IsTool);
        var groupKeys = _entries.Where(e => !e.IsTool && e.GroupKey != null).Select(e => e.GroupKey!).ToArray();
        var aligned = groupKeys.Length == ToolGroups.All.Length
                      && ToolGroups.All.All(g => groupKeys.Contains(g, StringComparer.OrdinalIgnoreCase));

        var head = string.IsNullOrEmpty(prefix) ? "LitSSH MCP 工具说明" : prefix;
        var groupText = aligned
            ? $"{groupKeys.Length} 个分组"
            : $"{groupKeys.Length} 个分组（与「工具分组设置」不一致!）";

        HeaderText.Text = $"{head} · 共 {toolCount} 个工具 · {groupText} · 数据源: docs/TOOLS.md（与 MCP 服务器端工具注解双向同步）";
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var keyword = SearchBox.Text?.Trim() ?? string.Empty;
        ToolList.ItemsSource = string.IsNullOrEmpty(keyword)
            ? _entries
            : _entries.Where(x => x.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ToolList.SelectedItem is not DocEntry entry)
            return;

        var divider = new string('─', 60);
        var body = string.IsNullOrWhiteSpace(entry.Content) && !entry.IsTool
            ? "（本分组无额外说明，工具见左侧列表）"
            : entry.Content;
        DetailBox.Text = $"{entry.Title}\n{divider}\n\n{body}";
        DetailBox.ScrollToHome();
    }

    private void OnCopySection(object sender, RoutedEventArgs e)
    {
        if (ToolList.SelectedItem is not DocEntry entry)
            return;

        CopyText($"{(entry.IsTool ? "###" : "##")} {entry.Title}\n\n{entry.Content}", $"已复制本节: {entry.Title}");
    }

    private void OnCopyAll(object sender, RoutedEventArgs e)
    {
        var assembly = typeof(McpToolsView).Assembly;
        using var stream = assembly.GetManifestResourceStream(DocsResourceName);
        if (stream == null)
            return;

        using var reader = new StreamReader(stream);
        CopyText(reader.ReadToEnd(), "已复制全部工具说明, 可粘贴到 AI 智能体的提示词中");
    }

    private void CopyText(string text, string statusMessage)
    {
        try
        {
            Clipboard.SetText(text);
            UpdateHeader(statusMessage);
        }
        catch (Exception ex)
        {
            HeaderText.Text = $"复制失败: {ex.Message}";
        }
    }

    private sealed record DocEntry(string Title, bool IsTool, string? GroupKey, string Content);
}
