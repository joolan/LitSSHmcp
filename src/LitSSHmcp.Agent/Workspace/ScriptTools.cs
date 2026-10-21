using System.Text;
using System.Text.RegularExpressions;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.Agent;

/// <summary>
/// 可复用脚本登记工具（本地、非 MCP）：把已验证的运维脚本沉淀到工作区 scripts/ 目录约定，
/// 维护 scripts/MANIFEST.md 台账（路径/用途/兼容性/已部署资产/验证状态），
/// 并在长期记忆可用时同步为 kind=script 的结构化记忆（同 key 更新=记录新的部署资产）。
/// </summary>
public static class ScriptTools
{
    public const string ScriptsDir = "scripts";
    public const string LocalDir = "scripts/local";     // 本机复用 (PowerShell / Python)
    public const string RemoteDir = "scripts/remote";   // 远程服务器复用 (shell / Python)
    public const string ManifestPath = "scripts/MANIFEST.md";

    private const int MaxManifestChars = 200_000;

    public static IReadOnlyList<IAgentTool> Create(string workspaceDir, AgentMemoryService? memory, bool enabled = true)
    {
        if (!enabled)
            return Array.Empty<IAgentTool>();

        var root = Path.GetFullPath(workspaceDir);

        var register = new LocalFunction(
            "ops_script_register",
            "登记/更新一个可复用运维脚本: 写入 scripts/MANIFEST.md 台账并同步经验记忆; 重复登记同一 path 会合并已部署资产",
            """{"type":"object","properties":{"path":{"type":"string","description":"脚本相对工作区路径, 须在 scripts/local/ 或 scripts/remote/ 下"},"summary":{"type":"string","description":"用途与验证结论(简明, 含关键参数说明)"},"compat":{"type":"string","description":"兼容性, 如 python3 / python2.7+3 / bash4+ / POSIX sh / PowerShell 5.1"},"deployedTo":{"type":"string","description":"已上传执行过的资产ID, 逗号分隔, 如 ssh:web01"},"verified":{"type":"boolean","description":"是否已在目标环境验证通过, 默认 true"}},"required":["path","summary","compat"]}""",
            async (args, ct) =>
            {
                var path = ArgumentReader.ReadString(args, "path");
                var summary = ArgumentReader.ReadString(args, "summary") ?? string.Empty;
                var compat = ArgumentReader.ReadString(args, "compat") ?? string.Empty;
                var deployedTo = ArgumentReader.ReadString(args, "deployedTo");
                var verified = ReadBool(args, "verified", true);
                return await RegisterAsync(root, memory, path, summary, compat, deployedTo, verified, ct);
            });

        return new IAgentTool[] { new LocalAgentTool(register) };
    }

    private static bool ReadBool(IDictionary<string, object?> args, string key, bool fallback)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return fallback;
        return value switch
        {
            bool b => b,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.True } => true,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.False } => false,
            string s when bool.TryParse(s, out var p) => p,
            _ => fallback
        };
    }

    private static async Task<string> RegisterAsync(string root, AgentMemoryService? memory, string? path,
        string summary, string compat, string? deployedTo, bool verified, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "错误: path 为必填";
        if (string.IsNullOrWhiteSpace(summary))
            return "错误: summary 为必填";
        if (string.IsNullOrWhiteSpace(compat))
            return "错误: compat 为必填(如 python3 / bash4+ / POSIX sh)";

        string rel;
        try
        {
            var full = WorkspaceTools.ResolvePath(root, path);
            rel = Path.GetRelativePath(root, full).Replace('\\', '/');
        }
        catch (InvalidOperationException ex)
        {
            return $"错误: {ex.Message}";
        }

        if (!rel.StartsWith(ScriptsDir + "/", StringComparison.OrdinalIgnoreCase))
            return $"错误: 脚本须位于 {LocalDir}/ 或 {RemoteDir}/ 下, 当前: {rel}";
        if (!File.Exists(WorkspaceTools.ResolvePath(root, rel)))
            return $"(脚本不存在 {rel}, 未登记) 请先落盘脚本文件再登记";

        var manifest = WorkspaceTools.ResolvePath(root, ManifestPath);
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        if (!File.Exists(manifest))
            await File.WriteAllTextAsync(manifest,
                "# 可复用脚本清单\n\n> 由 ops_script_register 维护；本机脚本在 scripts/local/，上服务器脚本在 scripts/remote/。\n\n" +
                "| 路径 | 用途 | 兼容性 | 已部署资产 | 验证 | 更新 |\n| --- | --- | --- | --- | --- | --- |\n", ct);

        var text = await File.ReadAllTextAsync(manifest, ct);
        if (text.Length > MaxManifestChars)
            return $"(清单过大 {text.Length} 字符, 未更新)";

        var now = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var assets = string.IsNullOrWhiteSpace(deployedTo)
            ? string.Empty
            : string.Join(",", deployedTo.Split(new[] { ',', ';', '，' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim()).Where(v => v.Length > 0));

        var row = $"| {rel} | {Cell(summary)} | {Cell(compat)} | {Cell(MergeRowAssets(FindRowAssets(text, rel), assets))} " +
                  $"| {(verified ? "已验证" : "待验证")} | {now} |";

        var pattern = new Regex(@"^\|\s*" + Regex.Escape(rel) + @"\s*\|.*$", RegexOptions.Multiline);
        string updated;
        if (pattern.IsMatch(text))
            updated = pattern.Replace(text, _ => row, 1);
        else
            updated = text.TrimEnd() + "\n" + row + "\n";

        await File.WriteAllTextAsync(manifest, updated, ct);

        // 脚本台账变更入审计链（评估报告 §10.A.1：脚本写入/登记此前零审计, D6 投毒路径需据可查）
        await LocalAudit.LogAsync("ops_script_register", $"SCRIPT_REGISTER: {rel}",
            CommandStatus.Executed,
            result: verified ? "已验证" : "待验证",
            filePath: manifest).ConfigureAwait(false);

        string? memoryResult = null;
        if (memory is not null)
        {
            try
            {
                var content = $"脚本 {rel}\n用途: {summary}\n兼容性: {compat}\n验证: {(verified ? "已验证" : "待验证")}" +
                              (string.IsNullOrEmpty(assets) ? string.Empty : $"\n已部署: {assets}");
                memoryResult = await memory.SaveAsync("script", rel, content, assets, compat, ct);
            }
            catch
            {
                memoryResult = "(记忆同步失败, 台账已更新)";
            }
        }

        return $"已登记 {rel} → {ManifestPath}" + (memoryResult is null ? string.Empty : $"\n{memoryResult}");
    }

    private static string FindRowAssets(string manifestText, string rel)
    {
        var match = new Regex(@"^\|\s*" + Regex.Escape(rel) + @"\s*\|.*$", RegexOptions.Multiline)
            .Match(manifestText);
        if (!match.Success)
            return string.Empty;
        var cells = match.Value.Split('|');
        // | 路径 | 用途 | 兼容性 | 已部署资产 | 验证 | 更新 |  → 索引 4
        return cells.Length > 4 ? cells[4].Trim() : string.Empty;
    }

    private static string MergeRowAssets(string existing, string added)
    {
        var set = new HashSet<string>(
            (existing ?? string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim()),
            StringComparer.OrdinalIgnoreCase);
        foreach (var item in (added ?? string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var v = item.Trim();
            if (v.Length > 0)
                set.Add(v);
        }
        return string.Join(",", set);
    }

    private static string Cell(string value)
    {
        var v = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|").Trim();
        return v.Length > 300 ? v[..300] + "…" : v;
    }
}
