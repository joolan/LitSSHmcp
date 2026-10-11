using System.IO;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class PromptTemplateTests
{
    [Fact]
    public void Sanitize_drops_empty_and_normalizes_name()
    {
        var list = AgentPromptStore.Sanitize(new[]
        {
            new AgentPromptTemplate { Name = " /查nginx ", Description = "d", Prompt = "p" },
            new AgentPromptTemplate { Name = "", Prompt = "x" },
            new AgentPromptTemplate { Name = "空正文", Prompt = "   " },
        });

        var one = Assert.Single(list);
        Assert.Equal("查nginx", one.Name);
        Assert.Equal("p", one.Prompt);
    }

    [Fact]
    public void Save_then_load_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), "litssh-prompts-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            AgentPromptStore.SaveTo(path, new[] { new AgentPromptTemplate { Name = "a", Description = "d", Prompt = "P" } });

            var loaded = AgentPromptStore.LoadFrom(path);

            var one = Assert.Single(loaded);
            Assert.Equal("a", one.Name);
            Assert.Equal("P", one.Prompt);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Slash_parse_matches_user_template_and_extracts_args()
    {
        var commands = new List<SlashCommand>(SlashCommands.BuiltIn)
        {
            new("/查nginx", "排查 nginx", SlashCommandKind.Template, "请排查 nginx 502")
        };

        var (cmd, args) = SlashCommands.Parse(commands, "/查nginx 服务器1");

        Assert.NotNull(cmd);
        Assert.Equal(SlashCommandKind.Template, cmd!.Kind);
        Assert.Equal("请排查 nginx 502", cmd.Prompt);
        Assert.Equal("服务器1", args);
    }

    [Fact]
    public void Templates_vm_rejects_duplicate_names()
    {
        var vm = new PromptTemplatesViewModel();
        vm.Items.Clear();
        vm.Items.Add(new PromptTemplateItem(new AgentPromptTemplate { Name = "x", Prompt = "p" }));
        vm.Items.Add(new PromptTemplateItem(new AgentPromptTemplate { Name = "X", Prompt = "p2" }));

        vm.Save();

        Assert.Contains("重复", vm.Status);
    }

    [Fact]
    public void ToolPolicy_row_maps_index_to_policy()
    {
        var tool = new LitSSHmcp.Agent.ToolPolicyItem("ssh_execute_command", "command", false, true);
        var row = new LitSSHmcp.App.Views.ToolPolicyRow(tool, LitSSHmcp.Agent.AgentToolPolicy.Default);

        Assert.Equal(0, row.SelectedIndex);

        row.SelectedIndex = 2;
        Assert.Equal(LitSSHmcp.Agent.AgentToolPolicy.Deny, row.Policy);

        row.SelectedIndex = 1;
        Assert.Equal(LitSSHmcp.Agent.AgentToolPolicy.Allow, row.Policy);
    }
}
