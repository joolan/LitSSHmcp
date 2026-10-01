using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;

namespace LitSSHmcp.Tests;

/// <summary>用于过滤器单测的固定安全配置提供者。</summary>
internal sealed class FakeSecurityOptions : ISecurityOptionsProvider
{
    public CommandFilterConfig CommandFilter { get; set; } = new();
    public SqlFilterConfig SqlFilter { get; set; } = new();
    public FileTransferConfig FileTransfer { get; set; } = new();
    public SshHostKeyConfig SshHostKey { get; set; } = new();
    public DiscoveryConfig Discovery { get; set; } = new();
    public LimitsConfig Limits { get; set; } = new();
    public AuditConfig Audit { get; set; } = new();
    public ApprovalConfig Approval { get; set; } = new();
    public void Invalidate() { }
}
