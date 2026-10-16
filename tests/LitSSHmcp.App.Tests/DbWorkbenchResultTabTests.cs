using System;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Threading;
using LitSSHmcp.App.ViewModels;
using Xunit;

namespace LitSSHmcp.App.Tests;

/// <summary>回归测试：查询报错后结果区会选中「消息」标签（错误面板盖住结果），
/// 之后读键必须切回「结果」标签，否则看起来像"错误清不掉、双击键没反应"。</summary>
public class DbWorkbenchResultTabTests
{
    private static Exception? RunSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return captured;
    }

    [Fact]
    public void Key_value_read_recover_result_tab_after_query_error()
    {
        var ex = RunSta(() =>
        {
            var vm = new DbWorkbenchViewModel();

            // 1) 查询失败：错误状态自动记入消息；失败分支重建子标签时没有 Result → 选中「消息」
            vm.StatusMessage = "查询失败: ERR unknown command 'FOO'";
            Assert.Single(vm.Messages);
            var messagesTab = vm.ResultSetTabs.Single(t => t.Kind == ResultTabKind.Messages);
            vm.SelectedResultTab = messagesTab;
            Assert.True(vm.ShowMessages);
            Assert.False(vm.ShowResultGrid);
            Assert.Null(vm.Results);

            // 2) 读键成功后（UseKeyAsync → RebuildKeyResultTabs）：必须存在并选中「结果」标签
            var view = new DataView(new DataTable());
            typeof(DbWorkbenchViewModel)
                .GetMethod("RebuildKeyResultTabs", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, new object?[] { view });

            Assert.False(vm.ShowMessages);
            Assert.True(vm.ShowResultGrid);
            Assert.Same(view, vm.Results);
            // 消息标签仍保留，可手动切回去查看历史错误
            Assert.Contains(vm.ResultSetTabs, t => t.Kind == ResultTabKind.Messages);
        });
        Assert.Null(ex);
    }

    [Fact]
    public void Key_value_read_failure_keeps_messages_visible()
    {
        var ex = RunSta(() =>
        {
            var vm = new DbWorkbenchViewModel();
            vm.StatusMessage = "查询失败: ERR unknown command 'FOO'";
            vm.SelectedResultTab = vm.ResultSetTabs.Single(t => t.Kind == ResultTabKind.Messages);

            // 读键失败：无结果可显示 → 「消息」标签被选中展示失败原因
            typeof(DbWorkbenchViewModel)
                .GetMethod("RebuildKeyResultTabs", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, new object?[] { null });

            Assert.True(vm.ShowMessages);
            Assert.False(vm.ShowResultGrid);
            Assert.Null(vm.Results);
        });
        Assert.Null(ex);
    }
}
