using LitSSHmcp.Core.Models;
using LitSSHmcp.Web;
using LitSSHmcp.Web.Sessions;
using Xunit;

namespace LitSSHmcp.Web.Tests;

/// <summary>Web 预算闸与 HTTPS 启动解析纯函数测试（评估报告 §10.B.6 / §10.B.2）。</summary>
public class WebPolicyTests
{
    // ---- 成本预算（§10.B.6 agent.budget 按用户生效）----

    [Fact]
    public void Budget_block_disabled_without_limit_or_prices()
    {
        Assert.Null(WebBudgetGuard.ComputeCostBlock(null, 10_000_000, 0, 10, 30));
        Assert.Null(WebBudgetGuard.ComputeCostBlock(new AgentBudgetConfig { MaxSessionCost = 0 },
            10_000_000, 0, 10, 30));
        // 模型未填价格无法判定成本 → 放行（与桌面一致）
        Assert.Null(WebBudgetGuard.ComputeCostBlock(new AgentBudgetConfig { MaxSessionCost = 1 },
            10_000_000, 0, 0, 0));
    }

    [Fact]
    public void Budget_block_triggers_when_cost_reaches_limit()
    {
        var budget = new AgentBudgetConfig { MaxSessionCost = 1 };
        // 10M 输入 token × ¥10/M = ¥10 ≥ ¥1
        var block = WebBudgetGuard.ComputeCostBlock(budget, 10_000_000, 0, 10, 30);
        Assert.NotNull(block);
        Assert.Contains("预算上限", block, StringComparison.Ordinal);

        // 未到限放行
        Assert.Null(WebBudgetGuard.ComputeCostBlock(budget, 1000, 0, 10, 30));

        // 只填出价也能判定：500k 输出 × ¥30/M = ¥15 ≥ ¥1
        Assert.NotNull(WebBudgetGuard.ComputeCostBlock(budget, 0, 500_000, 0, 30));
    }

    // ---- HTTPS 启动（§10.B.2 建议项）----

    [Fact]
    public void Https_disabled_without_config_or_missing_file()
    {
        Assert.Null(WebHttpsBoot.Resolve(null, _ => true));
        Assert.Null(WebHttpsBoot.Resolve(new WebHostConfig(), _ => true));
        Assert.Null(WebHttpsBoot.Resolve(new WebHostConfig
        {
            Https = new WebHttpsConfig { CertificatePath = "C:/certs/x.pfx" }
        }, _ => false));   // 配置了但文件不存在 → 不启用（防启动即崩）
    }

    [Fact]
    public void Https_resolves_default_and_custom_port_with_password()
    {
        var resolved = WebHttpsBoot.Resolve(new WebHostConfig
        {
            Https = new WebHttpsConfig { CertificatePath = "C:/certs/x.pfx" }
        }, _ => true);
        Assert.NotNull(resolved);
        Assert.Equal((WebHttpsBoot.DefaultHttpsPort, "C:/certs/x.pfx", ""), resolved!.Value);

        var custom = WebHttpsBoot.Resolve(new WebHostConfig
        {
            Https = new WebHttpsConfig { CertificatePath = "y.pfx", CertificatePassword = "pw", Port = 9443 }
        }, _ => true);
        Assert.NotNull(custom);
        Assert.Equal((9443, "y.pfx", "pw"), custom!.Value);
    }
}
