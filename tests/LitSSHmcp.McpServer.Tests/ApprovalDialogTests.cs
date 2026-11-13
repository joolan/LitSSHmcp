using System.Threading;
using LitSSHmcp.McpServer.Services;
using Xunit;

namespace LitSSHmcp.McpServer.Tests;

/// <summary>
/// 审批弹窗构造回归：带风险报告时使用左右分栏，SplitContainer 的分隔设置
/// 若在构造函数里完成会抛 InvalidOperationException，导致审批退化为原生 MessageBox。
/// </summary>
public class ApprovalDialogTests
{
    [Fact]
    public void Dialog_with_risk_report_constructs_on_sta_thread()
    {
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var dialog = new ApprovalDialog(
                    "server-1", "脚本执行", "bash /tmp/deploy.sh", null, 30, topMost: false,
                    riskReport: "【风险】写文件: /etc/passwd");
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        {
            IsBackground = true
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "审批弹窗构造超时");
        Assert.Null(error);
    }

    [Fact]
    public void Native_message_body_keeps_head_and_tail_and_caps_height()
    {
        var text = string.Join('\n', Enumerable.Range(0, 200)
            .Select(i => $"line{i} " + new string('x', 300)));

        var compact = DesktopApprovalService.CompactNativeBody(text, out var truncated);

        Assert.True(truncated);
        Assert.True(compact.Length < text.Length);
        Assert.Contains("中间省略", compact);
        Assert.Contains("line0", compact);
        Assert.Contains("line199", compact);
        Assert.DoesNotContain(new string('x', 200), compact);
        Assert.All(compact.Split('\n'), line => Assert.True(line.Length <= 240));
    }
}
