using System.Runtime.CompilerServices;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.Agent.Tests;

/// <summary>
/// 测试程序集默认替身：本地工具审计（LocalAudit）在测试中绝不触碰真实 audit.db；
/// 审计专项测试在自身用例里临时换入捕获器，结束时还原为空操作。
/// </summary>
internal static class LocalAuditTestGuard
{
    [ModuleInitializer]
    public static void Init() => LocalAudit.OverrideSink = _ => Task.CompletedTask;
}
