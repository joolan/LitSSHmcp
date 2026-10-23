using System.Text;
using System.Text.Json;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>
/// 提供给模型的**工作区文档读写工具**（本地、非 MCP）：用于维护技能要求的运维资产档案（默认 OPS_ASSETS.md）。
/// 所有路径被限制在工作区内，拦截 <c>..</c> 越界、绝对路径逃逸与链接逃逸（§10.C.3），并有大小上限。
/// 双根（评估报告需求 D / D6）：会话分配独立目录时默认读写会话目录（任务产出），
/// <c>shared/</c> 前缀与省略 path 的默认文档指向共享工作区（OPS_ASSETS 资产档案跨会话保留）；
/// 未分配会话目录时共享区即唯一根（现状行为）。
/// 采用自实现的 <see cref="AIFunction"/>（手动读参），避免不同大模型连接器对参数编组/默认值的差异。
/// </summary>
public static class WorkspaceTools
{
    public const string DefaultDoc = "OPS_ASSETS.md";

    /// <summary>共享区路径前缀（会话目录模式下访问共享工作区资产）。</summary>
    public const string SharedPrefix = "shared/";

    private const int MaxReadBytes = 200_000;
    private const int MaxWriteChars = 200_000;

    public static string ResolveDir(string? configured) =>
        !string.IsNullOrWhiteSpace(configured)
            ? configured!
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LitSSH");

    /// <param name="workspaceDir">主根：会话工作目录（已分配）或共享工作区（未分配=现状）。</param>
    /// <param name="sharedDir">共享工作区根；与 <paramref name="workspaceDir"/> 相同或空 = 单根模式（行为与既往一致）。</param>
    /// <param name="sharedWritable">共享区是否可写（Web 普通成员为 false，会话目录始终可写；桌面默认 true）。</param>
    public static IReadOnlyList<IAgentTool> Create(string workspaceDir, string? sharedDir = null, bool sharedWritable = true)
    {
        var root = Path.GetFullPath(workspaceDir);
        Directory.CreateDirectory(root);

        var shared = string.IsNullOrWhiteSpace(sharedDir) ? null : Path.GetFullPath(sharedDir);
        if (shared is not null &&
            string.Equals(shared.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            shared = null;
        if (shared is not null)
            Directory.CreateDirectory(shared);

        var pathDoc = shared is null
            ? "相对工作区根目录的路径"
            : "相对本会话工作目录的路径; shared/ 前缀表示共享工作区(如 shared/OPS_ASSETS.md)";

        var read = new LocalFunction(
            "ops_doc_read",
            "读取工作区文档(默认 OPS_ASSETS.md); " + pathDoc,
            "{\"type\":\"object\",\"properties\":{" + PathProp(pathDoc + "; 省略则读 OPS_ASSETS.md(共享资产档案)") + "}}",
            args => Read(root, shared, ArgumentReader.ReadString(args, "path")));

        var write = new LocalFunction(
            "ops_doc_write",
            "写入/增量更新工作区文档; mode=overwrite 覆盖全文(默认), append 末尾追加(不存在则创建), patch 把第一处 find 替换为 replace; " +
            "用于维护资产档案(共享区)与任务产出文档(本会话目录)" +
            (shared is null ? string.Empty : "; 脚本类文件(.sh/.py/.ps1等)直接写入共享区会自动在文件头标记『未审阅』(建议改经 ops_promote 提升以桌面确认)"),
            "{\"type\":\"object\",\"properties\":{" +
            "\"mode\":{\"type\":\"string\",\"enum\":[\"overwrite\",\"append\",\"patch\"],\"description\":\"overwrite=覆盖全文(默认), append=末尾追加, patch=定点替换\"}," +
            "\"content\":{\"type\":\"string\",\"description\":\"overwrite/append 的文本内容\"}," +
            "\"find\":{\"type\":\"string\",\"description\":\"patch 要查找的原文(须精确匹配, 含空白)\"}," +
            "\"replace\":{\"type\":\"string\",\"description\":\"patch 替换为的新文本\"}," +
            PathProp(pathDoc + "; 省略则写 OPS_ASSETS.md(共享资产档案)") + "}}",
            async (args, ct) =>
            {
                var mode = (ArgumentReader.ReadString(args, "mode") ?? "overwrite").Trim().ToLowerInvariant();
                var path = ArgumentReader.ReadString(args, "path");
                var content = ArgumentReader.ReadString(args, "content");
                if (mode is "overwrite" or "append" && content is null)
                    return $"错误: mode={mode} 需要 content";
                return mode switch
                {
                    "overwrite" => await Write(root, shared, sharedWritable, path, content!, ct),
                    "append" => await Append(root, shared, sharedWritable, path, content!, ct),
                    "patch" => await Patch(root, shared, sharedWritable, path,
                        ArgumentReader.ReadString(args, "find") ?? string.Empty,
                        ArgumentReader.ReadString(args, "replace") ?? string.Empty, ct),
                    _ => "错误: mode 仅支持 overwrite/append/patch"
                };
            });

        var list = new LocalFunction(
            "ops_doc_list",
            "列出工作区文档文件" + (shared is null ? string.Empty : "(本会话目录 + shared/ 共享区)"),
            """{"type":"object","properties":{}}""",
            _ => List(root, shared));

        return new IAgentTool[]
        {
            new LocalAgentTool(read, readOnly: true),
            new LocalAgentTool(write),
            new LocalAgentTool(list, readOnly: true)
        };
    }

    /// <summary>JSON Schema 的 path 属性片段（description 由调用方按单/双根模式拼好）。</summary>
    private static string PathProp(string description) =>
        "\"path\":{\"type\":\"string\",\"description\":\"" + description + "\"}";

    private const string UnreviewedStampPrefix = "# 未审阅:";

    /// <summary>
    /// 提升审阅策略的另一半（评估报告 §10.D.1 待办 #9 / §5.1 行 185，M1 实现时定）：
    /// 脚本类产物经 <c>ops_promote</c> 提升走**桌面确认**；若 AI 直接经 <c>shared/</c> 前缀把脚本类文件
    /// 写进共享区（绕过提升命令的旁路），则按报告的备选方案**打"未审阅"标记**——文件头部注入注释行
    /// （含来源会话），读取方与后续执行审批可据此识别未审阅产物。仅会话模式（双根）生效；
    /// 单根=旧会话共享区现状行为不打标，避免全量噪音。内容已带标记或非脚本类/非跨根写入则原样返回。
    /// </summary>
    private static string MaybeStampUnreviewed(string root, string? shared, string full, string content)
    {
        if (shared is null)
            return content;                                       // 单根模式: 现状行为
        if (PathGuard.IsUnder(root, full))
            return content;                                       // 会话目录内写入: 产出地, 无需标记
        if (!PathGuard.IsUnder(shared, full))
            return content;                                       // 理论不可达(Resolve 已约束), 防御性放行
        if (!ScriptRiskAnalyzer.IsScriptFile(full))
            return content;                                       // 仅脚本类产物
        if (content.StartsWith(UnreviewedStampPrefix, StringComparison.Ordinal))
            return content;                                       // 已标记, 不重复
        var session = LocalAudit.AmbientSessionId ?? "unknown";
        return $"{UnreviewedStampPrefix} 由 AI 直接写入共享区(未经 ops_promote 桌面确认), 来源会话 {session}\n{content}";
    }

    private static string Read(string root, string? shared, string? path)
    {
        var full = Resolve(root, shared, path);
        if (!File.Exists(full))
            return $"(文件不存在) {Rel(root, full, shared)}";
        var info = new FileInfo(full);
        if (info.Length > MaxReadBytes)
            return $"(文件过大 {info.Length} 字节, 超过上限 {MaxReadBytes}, 未读取)";
        return File.ReadAllText(full);
    }

    // 写/追加/打补丁三类**变更**操作写审计（评估报告 §10.A.1 本地工具补审计；读/list 为只读不记, 避免噪音）。

    private static async Task<string> Write(string root, string? shared, bool sharedWritable, string? path, string content, CancellationToken ct = default)
    {
        var full = ResolveTarget(root, shared, path, out var isShared);
        EnsureSharedWriteAllowed(sharedWritable, isShared);
        content = MaybeStampUnreviewed(root, shared, full, content);
        if (content.Length > MaxWriteChars)
            return $"(内容过长 {content.Length} 字符, 超过上限 {MaxWriteChars}, 未写入)";
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        var bytes = Encoding.UTF8.GetByteCount(content);
        await LocalAudit.LogAsync("ops_doc_write", $"DOC_WRITE: {Rel(root, full, shared)}",
            CommandStatus.Executed, result: $"{bytes} 字节", filePath: full).ConfigureAwait(false);
        return $"已写入 {Rel(root, full, shared)} ({bytes} 字节)";
    }

    private static async Task<string> Append(string root, string? shared, bool sharedWritable, string? path, string content, CancellationToken ct = default)
    {
        var full = ResolveTarget(root, shared, path, out var isShared);
        EnsureSharedWriteAllowed(sharedWritable, isShared);
        if (!File.Exists(full))
            content = MaybeStampUnreviewed(root, shared, full, content);   // 新建文件才打标, 追加既有文件保留原头部
        if (content.Length > MaxWriteChars)
            return $"(内容过长 {content.Length} 字符, 超过上限 {MaxWriteChars}, 未追加)";
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var prefix = File.Exists(full) && new FileInfo(full).Length > 0 ? Environment.NewLine : string.Empty;
        File.AppendAllText(full, prefix + content);
        var bytes = Encoding.UTF8.GetByteCount(content);
        await LocalAudit.LogAsync("ops_doc_append", $"DOC_APPEND: {Rel(root, full, shared)}",
            CommandStatus.Executed, result: $"+{bytes} 字节", filePath: full).ConfigureAwait(false);
        return $"已追加到 {Rel(root, full, shared)} ({bytes} 字节)";
    }

    private static async Task<string> Patch(string root, string? shared, bool sharedWritable, string? path, string find, string replace, CancellationToken ct = default)
    {
        var full = ResolveTarget(root, shared, path, out var isShared);
        EnsureSharedWriteAllowed(sharedWritable, isShared);
        if (!File.Exists(full))
            return $"(文件不存在) {Rel(root, full, shared)}";
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
        await LocalAudit.LogAsync("ops_doc_patch", $"DOC_PATCH: {Rel(root, full, shared)}",
            CommandStatus.Executed, result: "替换 1 处", filePath: full).ConfigureAwait(false);
        return $"已更新 {Rel(root, full, shared)} (替换 1 处)";
    }

    private static void EnsureSharedWriteAllowed(bool sharedWritable, bool isShared)
    {
        if (isShared && !sharedWritable)
            throw new InvalidOperationException("共享工作区只读(仅管理员可写), 请写入本会话工作目录");
    }

    private static string List(string root, string? shared)
    {
        // 不跟随 reparse point（junction/符号链接）：避免枚举沿链接遍历到工作区之外（§10.C.3）
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var files = new List<string>();
        files.AddRange(Directory.EnumerateFiles(root, "*", options).Select(f => Rel(root, f, shared)));

        if (shared is not null)
        {
            // 共享区只展示资产相关子树的根级与脚本/记忆文档，避免 jobs/ 其他会话目录混入清单
            var sharedFiles = Directory.EnumerateFiles(shared, "*", options)
                .Select(f => Path.GetRelativePath(shared, f).Replace('\\', '/'))
                .Where(rel => !IsSessionTreePath(rel))
                .Select(rel => SharedPrefix + rel);
            files.AddRange(sharedFiles);
        }

        var ordered = files
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToList();
        return ordered.Count == 0 ? "(工作区暂无文件)" : string.Join('\n', ordered);
    }

    /// <summary>把相对路径解析到工作区内（单根，供 <c>ops_script_register</c> 等外部调用复用）。</summary>
    public static string ResolvePath(string root, string? path) => Resolve(root, null, path);

    /// <summary>
    /// 双根解析：<c>shared/</c> 前缀 → 共享区；省略 path → 共享区默认文档（会话模式）；
    /// 其余 → 主根（会话目录）。共享区禁止经 <c>shared/jobs/</c> 触达其他会话目录；
    /// 逃逸（<c>..</c>、绝对路径、链接）由 <see cref="PathGuard"/> 按物理路径判定。
    /// </summary>
    private static string Resolve(string root, string? shared, string? path) =>
        ResolveTarget(root, shared, path, out _);

    private static string ResolveTarget(string root, string? shared, string? path, out bool isShared)
    {
        var useDefault = string.IsNullOrWhiteSpace(path);
        var rel = NormalizeRelative(useDefault ? DefaultDoc : path!.Trim());

        string target;
        isShared = false;
        if (shared is not null && !useDefault &&
            rel.StartsWith(SharedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            target = shared;
            isShared = true;
            rel = rel[SharedPrefix.Length..];
            if (IsSessionTreePath(rel))
                throw new InvalidOperationException("非法路径(禁止经 shared/ 访问会话目录)");
        }
        else if (shared is not null && useDefault)
        {
            // 会话模式下省略 path = 共享区资产档案（跨会话共享，不落会话目录）
            target = shared;
            isShared = true;
            rel = DefaultDoc;
        }
        else
        {
            target = root;
        }

        var full = Path.GetFullPath(Path.Combine(target, rel));
        var canonicalTarget = PathGuard.TryCanonicalize(target);
        var canonicalFull = PathGuard.TryCanonicalize(full);
        var underRoot = PathGuard.IsUnder(target, full);
        // 严格包含：根自身（解析后相等）不算合法文件落点
        var isRootItself = canonicalTarget is not null && canonicalFull is not null &&
            string.Equals(canonicalTarget.TrimEnd(Path.DirectorySeparatorChar),
                canonicalFull.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        if (!underRoot || isRootItself || canonicalFull is null)
            throw new InvalidOperationException("非法路径(越出工作区)");
        return full;
    }

    /// <summary>
    /// 字面归一化相对路径：统一分隔符、去掉空段与 <c>.</c> 段，拒绝任何含 <c>..</c> 的输入。
    /// 必须先归一化再判断 <c>shared/jobs</c>，否则 <c>shared/./jobs</c> 可绕过会话树隔离。
    /// </summary>
    private static string NormalizeRelative(string path)
    {
        var rel = path.Replace('\\', '/').TrimStart('/');
        if (rel.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("非法路径(禁止 .. 越界)");

        var segments = rel.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != ".");
        return string.Join('/', segments);
    }

    /// <summary>相对路径的首段是否为会话树 <c>jobs</c>（含该目录自身，大小写不敏感）。</summary>
    private static bool IsSessionTreePath(string relative)
    {
        if (relative.Length == 0)
            return false;
        var slash = relative.IndexOf('/');
        var first = slash < 0 ? relative : relative[..slash];
        return string.Equals(first, WorkspaceLayout.JobsDirName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>相对展示路径：共享区条目带 shared/ 前缀，主根条目为相对主根路径。</summary>
    private static string Rel(string root, string full, string? shared)
    {
        if (shared is not null &&
            Path.GetFullPath(full).StartsWith(shared.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            return SharedPrefix + Path.GetRelativePath(shared, full).Replace('\\', '/');
        return Path.GetRelativePath(root, full).Replace('\\', '/');
    }
}
