using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>
/// 提供给模型的**工作区文档读写工具**（本地、非 MCP）：用于维护技能要求的运维资产档案（默认 OPS_ASSETS.md）。
/// 所有路径被限制在工作区目录内，拦截 <c>..</c> 越界与绝对路径逃逸，并有大小上限。
/// 采用自实现的 <see cref="AIFunction"/>（手动读参），避免不同大模型连接器对参数编组/默认值的差异。
/// </summary>
public static class WorkspaceTools
{
    public const string DefaultDoc = "OPS_ASSETS.md";
    private const int MaxReadBytes = 200_000;
    private const int MaxWriteChars = 200_000;

    public static string ResolveDir(string? configured) =>
        !string.IsNullOrWhiteSpace(configured)
            ? configured!
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LitSSH");

    public static IReadOnlyList<IAgentTool> Create(string workspaceDir)
    {
        var root = Path.GetFullPath(workspaceDir);
        Directory.CreateDirectory(root);

        var read = new LocalFunction(
            "ops_doc_read",
            "读取工作区文档(默认 OPS_ASSETS.md); path 相对工作区根目录",
            """{"type":"object","properties":{"path":{"type":"string","description":"相对工作区根目录的路径, 省略则读写 OPS_ASSETS.md"}}}""",
            args => Read(root, ArgumentReader.ReadString(args, "path")));

        var write = new LocalFunction(
            "ops_doc_write",
            "写入/覆盖工作区文档(默认 OPS_ASSETS.md); 用于维护运维资产档案与笔记",
            """{"type":"object","properties":{"content":{"type":"string","description":"要写入的完整文本内容"},"path":{"type":"string","description":"相对工作区根目录的路径, 省略则写 OPS_ASSETS.md"}},"required":["content"]}""",
            args => Write(root, ArgumentReader.ReadString(args, "path"), ArgumentReader.ReadString(args, "content") ?? string.Empty));

        var append = new LocalFunction(
            "ops_doc_append",
            "向工作区文档末尾追加内容(文件不存在则创建); 增量更新运维资产档案时优先用它, 避免整份重写",
            """{"type":"object","properties":{"content":{"type":"string","description":"要追加的文本(会自动补换行)"},"path":{"type":"string","description":"相对工作区根目录的路径, 省略则用 OPS_ASSETS.md"}},"required":["content"]}""",
            args => Append(root, ArgumentReader.ReadString(args, "path"), ArgumentReader.ReadString(args, "content") ?? string.Empty));

        var patch = new LocalFunction(
            "ops_doc_patch",
            "在工作区文档中做定点替换: 把第一处 find 文本替换为 replace; 用于增量修正档案中的条目",
            """{"type":"object","properties":{"find":{"type":"string","description":"要查找的原文(须精确匹配, 含空白)"},"replace":{"type":"string","description":"替换为的新文本"},"path":{"type":"string","description":"相对工作区根目录的路径, 省略则用 OPS_ASSETS.md"}},"required":["find","replace"]}""",
            args => Patch(root, ArgumentReader.ReadString(args, "path"), ArgumentReader.ReadString(args, "find") ?? string.Empty, ArgumentReader.ReadString(args, "replace") ?? string.Empty));

        var list = new LocalFunction(
            "ops_doc_list",
            "列出工作区根目录及子目录下的文档文件",
            """{"type":"object","properties":{}}""",
            _ => List(root));

        return new IAgentTool[]
        {
            new LocalAgentTool(read, readOnly: true),
            new LocalAgentTool(write),
            new LocalAgentTool(append),
            new LocalAgentTool(patch),
            new LocalAgentTool(list, readOnly: true)
        };
    }

    private static string Read(string root, string? path)
    {
        var full = ResolvePath(root, path);
        if (!File.Exists(full))
            return $"(文件不存在) {Rel(root, full)}";
        var info = new FileInfo(full);
        if (info.Length > MaxReadBytes)
            return $"(文件过大 {info.Length} 字节, 超过上限 {MaxReadBytes}, 未读取)";
        return File.ReadAllText(full);
    }

    private static string Write(string root, string? path, string content)
    {
        var full = ResolvePath(root, path);
        if (content.Length > MaxWriteChars)
            return $"(内容过长 {content.Length} 字符, 超过上限 {MaxWriteChars}, 未写入)";
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return $"已写入 {Rel(root, full)} ({Encoding.UTF8.GetByteCount(content)} 字节)";
    }

    private static string Append(string root, string? path, string content)
    {
        var full = ResolvePath(root, path);
        if (content.Length > MaxWriteChars)
            return $"(内容过长 {content.Length} 字符, 超过上限 {MaxWriteChars}, 未追加)";
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var prefix = File.Exists(full) && new FileInfo(full).Length > 0 ? Environment.NewLine : string.Empty;
        File.AppendAllText(full, prefix + content);
        return $"已追加到 {Rel(root, full)} ({Encoding.UTF8.GetByteCount(content)} 字节)";
    }

    private static string Patch(string root, string? path, string find, string replace)
    {
        var full = ResolvePath(root, path);
        if (!File.Exists(full))
            return $"(文件不存在) {Rel(root, full)}";
        if (string.IsNullOrEmpty(find))
            return "(find 不能为空)";

        var text = File.ReadAllText(full);
        var index = text.IndexOf(find, StringComparison.Ordinal);
        if (index < 0)
            return "(未找到匹配文本, 未修改)";
        if (text.Length > MaxWriteChars)
            return $"(文件过长 {text.Length} 字符, 超过上限 {MaxWriteChars}, 未修改)";

        var updated = string.Concat(text.AsSpan(0, index), replace, text.AsSpan(index + find.Length));
        File.WriteAllText(full, updated);
        return $"已更新 {Rel(root, full)} (替换 1 处)";
    }

    private static string List(string root)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Rel(root, f))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToList();
        return files.Count == 0 ? "(工作区暂无文件)" : string.Join('\n', files);
    }

    /// <summary>把相对路径解析到工作区内；拦截 <c>..</c> 与绝对路径逃逸。</summary>
    public static string ResolvePath(string root, string? path)
    {
        var rel = string.IsNullOrWhiteSpace(path) ? DefaultDoc : path!.Trim();
        rel = rel.Replace('\\', '/').TrimStart('/');
        if (rel.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("非法路径(禁止 .. 越界)");

        var full = Path.GetFullPath(Path.Combine(root, rel));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("非法路径(越出工作区)");
        return full;
    }

    private static string Rel(string root, string full) => Path.GetRelativePath(root, full).Replace('\\', '/');
}
