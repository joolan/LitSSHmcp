using System.IO;
using System.Text.Json;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services;

/// <summary>用户自定义提示词模板（在 AI 助手里以斜杠命令形式出现）。</summary>
public sealed class AgentPromptTemplate
{
    /// <summary>命令名（不含前导斜杠），如 <c>查nginx</c> → <c>/查nginx</c>。</summary>
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>选中后填入输入框的提示词正文。</summary>
    public string Prompt { get; set; } = string.Empty;
}

/// <summary>提示词模板存储：<c>%APPDATA%\LitSSH\agent-prompts.json</c>。</summary>
public static class AgentPromptStore
{
    public static string DefaultPath => Path.Combine(ConfigPaths.AppDataDir, "agent-prompts.json");

    public static List<AgentPromptTemplate> Load() => LoadFrom(DefaultPath);

    public static void Save(IEnumerable<AgentPromptTemplate> templates) => SaveTo(DefaultPath, templates);

    public static List<AgentPromptTemplate> LoadFrom(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var list = JsonSerializer.Deserialize<List<AgentPromptTemplate>>(File.ReadAllText(path));
                if (list is not null)
                    return Sanitize(list);
            }
        }
        catch
        {
            // 损坏则回退默认
        }
        return Defaults();
    }

    public static void SaveTo(string path, IEnumerable<AgentPromptTemplate> templates)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(Sanitize(templates), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 保存失败忽略
        }
    }

    /// <summary>去掉空名/空正文项，规范命令名（去前导斜杠、去空白）。</summary>
    public static List<AgentPromptTemplate> Sanitize(IEnumerable<AgentPromptTemplate> templates) =>
        templates
            .Select(t => new AgentPromptTemplate
            {
                Name = (t.Name ?? string.Empty).Trim().TrimStart('/').Trim(),
                Description = (t.Description ?? string.Empty).Trim(),
                Prompt = t.Prompt ?? string.Empty
            })
            .Where(t => t.Name.Length > 0 && !string.IsNullOrWhiteSpace(t.Prompt))
            .ToList();

    private static List<AgentPromptTemplate> Defaults() => new()
    {
        new AgentPromptTemplate
        {
            Name = "整机巡检",
            Description = "对某台服务器做整机巡检并输出风险清单",
            Prompt = "请对指定服务器做一次整机巡检：先看整机快照（资源占用、异常进程、端口↔服务、磁盘/inode、失败的服务与容器、证书到期），再给出风险清单与处置建议。目标服务器："
        },
        new AgentPromptTemplate
        {
            Name = "排查nginx",
            Description = "排查 nginx 502/503/504 与网关超时",
            Prompt = "请排查 nginx 5xx（502/503/504）与网关超时：先看 nginx error.log 中的 upstream/timeout/connect failed，再定位上游后端进程与端口，给出根因与最小处置。目标服务器："
        },
        new AgentPromptTemplate
        {
            Name = "数据库体检",
            Description = "对数据源做连接数/慢查询/锁/复制体检",
            Prompt = "请对指定数据源做一次体检：连接数、慢查询、锁等待、复制延迟（MySQL/PG）或内存/命中率/慢日志（Redis），给出结论与处置建议。目标数据源："
        }
    };
}
