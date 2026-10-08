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
    public void Clear_context_readonly_tools_registered()
    {
        Assert.Contains(SlashCommands.All, c => c.Name == SlashCommands.ClearContext);
        Assert.Contains(SlashCommands.All, c => c.Name == SlashCommands.ReadOnly);
        Assert.Contains(SlashCommands.All, c => c.Name == SlashCommands.Tools);
    }

    [Fact]
    public void Parse_supports_arguments()
    {
        var (cmd, args) = SlashCommands.Parse("/工具 log mysql");
        Assert.NotNull(cmd);
        Assert.Equal(SlashCommands.Tools, cmd!.Name);
        Assert.Equal("log mysql", args);
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
