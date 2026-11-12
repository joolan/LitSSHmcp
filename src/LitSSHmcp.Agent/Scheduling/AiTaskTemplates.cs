namespace LitSSHmcp.Agent.Scheduling;

/// <summary>AI 巡检任务模板：预置可读的指令与默认调度，用户可在任务窗口一键创建。</summary>
public sealed record AiTaskTemplate(
    string Id,
    string Name,
    string Description,
    string Instruction,
    string TimeOfDay = "08:00",
    int TimeoutSeconds = 900,
    int MaxTurns = 12);

public static class AiTaskTemplates
{
    public static IReadOnlyList<AiTaskTemplate> All { get; } = new[]
    {
        new AiTaskTemplate(
            "daily-inspection",
            "每日整机巡检",
            "所有服务器快照、资源、服务、账号与安全变化",
            "对所有已配置服务器做只读整机巡检：优先读取最新快照；必要时刷新快照；与上一份快照对比资源、端口、服务、容器、账号、证书和安全风险变化。输出：总体健康结论、按严重级别列出的风险、较上次变差的项目、建议处置。只读，不执行任何修改。"),
        new AiTaskTemplate(
            "disk-capacity",
            "磁盘容量巡检",
            "磁盘使用率、增长趋势与大文件风险",
            "只读检查所有服务器的磁盘使用率：重点看根分区、数据盘、inode、可写状态和增长异常；结合快照/历史判断是否接近阈值。输出风险服务器、当前使用率、建议清理方向。不要删除文件或执行清理。"),
        new AiTaskTemplate(
            "certificate-expiry",
            "证书到期巡检",
            "nginx/站点证书有效期与配置可用性",
            "只读检查所有服务器的 nginx/站点证书：读取快照中的证书到期时间、域名/SAN、配置是否可解析；列出 30 天内到期或已过期的证书，按紧急程度排序并给出续期建议。不要修改证书或 nginx 配置。"),
        new AiTaskTemplate(
            "security-baseline",
            "安全基线巡检",
            "账号、SSH、端口、sudo 与高危服务",
            "对所有服务器做只读安全基线巡检：检查账号/空口令/特权账号、authorized_keys、SSH 密码登录与 root 登录、sudo NOPASSWD、暴露高危端口、失败登录和主要安全 findings；与上一份快照对比新增风险。输出高风险项、证据和修复建议，不执行修复。"),
        new AiTaskTemplate(
            "service-docker-health",
            "服务与容器健康巡检",
            "systemd failed、容器状态与端口监听",
            "只读检查所有服务器的 systemd failed 单元、Docker 容器状态/重启情况和关键端口监听；结合上一份快照对比新增失败或消失的服务/容器。输出异常清单、影响面和排查建议，不重启或修改服务。"),
        new AiTaskTemplate(
            "change-diff",
            "变更前后对比巡检",
            "对比最近两份快照的账号/端口/服务/容器/域名变化",
            "只读对比所有服务器最近两份快照：列出账号/公钥、监听端口、systemd、Docker、nginx 域名和配置 hash 的变化；判断哪些变化可疑或需要人工确认。输出变更摘要和风险提示，不执行任何变更。")
    };

    public static AiTaskConfig CreateTask(AiTaskTemplate template, string? ownerUserId = null) => new()
    {
        Name = template.Name,
        Instruction = template.Instruction,
        Enabled = true,
        ScheduleType = AiTaskScheduleType.Daily,
        TimeOfDay = template.TimeOfDay,
        ReadOnly = true,
        MaxTurns = template.MaxTurns,
        TimeoutSeconds = template.TimeoutSeconds,
        NotifyApp = true,
        NotifyOnlyOnIssue = true,
        RunOnStartupIfMissed = true,
        Source = "Template",
        OwnerUserId = string.IsNullOrWhiteSpace(ownerUserId) ? "local" : ownerUserId.Trim()
    };
}
