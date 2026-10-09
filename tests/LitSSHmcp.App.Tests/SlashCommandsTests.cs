using LitSSHmcp.App.ViewModels;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class SlashCommandsTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("/临", "/临")]
    [InlineData("/临时聊天", "/临时聊天")]
    [InlineData("  /临", "/临")]
    public void CurrentToken_detects_command_prefix(string input, string expected)
        => Assert.Equal(expected, SlashCommands.CurrentToken(input));

    [Theory]
    [InlineData("普通文本")]
    [InlineData("/临时聊天 附带说明")] // 含空白 → 不再视为“正在输入命令”
    [InlineData("")]
    public void CurrentToken_ignores_non_command(string input)
        => Assert.Null(SlashCommands.CurrentToken(input));

    [Fact]
    public void Match_filters_by_prefix()
    {
        Assert.NotEmpty(SlashCommands.Match("/临"));
        Assert.Empty(SlashCommands.Match("/zzz"));
    }

    [Fact]
    public void FindExact_trims_and_matches()
    {
        Assert.NotNull(SlashCommands.FindExact(" /临时聊天 "));
        Assert.Null(SlashCommands.FindExact("/不存在"));
    }

    [Fact]
    public void Temp_chat_command_registered()
        => Assert.Contains(SlashCommands.All, c => c.Name == SlashCommands.TempChat);

    [Fact]
    public void Compact_session_command_registered()
        => Assert.Contains(SlashCommands.All, c => c.Name == SlashCommands.CompactSession);

    [Fact]
    public void Clear_screen_command_registered()
        => Assert.Contains(SlashCommands.All, c => c.Name == SlashCommands.ClearScreen);

    [Fact]
    public void Clear_context_command_registered()
        => Assert.Contains(SlashCommands.All, c => c.Name == SlashCommands.ClearContext);

    [Fact]
    public void Removed_tool_commands_are_not_registered()
    {
        Assert.DoesNotContain(SlashCommands.All, c => c.Name == "/只读");
        Assert.DoesNotContain(SlashCommands.All, c => c.Name == "/工具");
    }

    [Fact]
    public void Parse_supports_arguments()
    {
        var (cmd, args) = SlashCommands.Parse("/压缩会话 附带说明");
        Assert.NotNull(cmd);
        Assert.Equal(SlashCommands.CompactSession, cmd!.Name);
        Assert.Equal("附带说明", args);
    }

    [Fact]
    public void Parse_matches_first_token_and_trims()
    {
        var (cmd, args) = SlashCommands.Parse("  /压缩会话  ");
        Assert.NotNull(cmd);
        Assert.Equal(SlashCommands.CompactSession, cmd!.Name);
        Assert.Equal(string.Empty, args);
    }
}
