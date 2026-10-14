using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class McpServerEditTests
{
    [Fact]
    public void Multiline_text_fields_split_into_args_env_headers()
    {
        var edit = new McpServerEdit
        {
            Name = "外部",
            Transport = "http",
            ArgsText = "-y\r\npkg\n\n  ",
            EnvText = "API_KEY=abc",
            HeadersText = "Authorization: Bearer t\nX-Url: https://a",
            Url = " https://mcp.example.com/mcp ",
        };

        var model = edit.ToModel();

        Assert.Equal(new[] { "-y", "pkg" }, model.Args);
        Assert.Equal(new[] { "API_KEY=abc" }, model.Env);
        Assert.Equal(new[] { "Authorization: Bearer t", "X-Url: https://a" }, model.Headers);
        Assert.Equal("https://mcp.example.com/mcp", model.Url);
        Assert.Equal("http", model.Transport);
    }

    [Fact]
    public void From_populates_fields_and_validation_state()
    {
        var edit = McpServerEdit.From(new AgentMcpServerConfig
        {
            Id = "ext1", Name = "外部", Transport = "stdio", Enabled = true,
            Command = "npx", Args = new[] { "-y" }, Env = new[] { "A=1" },
            LastError = "boom", LastValidatedAt = "2026-10-13 21:17", LastToolCount = 0,
        });

        Assert.Equal("ext1", edit.Id);
        Assert.Equal("npx", edit.Command);
        Assert.Equal("-y", edit.ArgsText);
        Assert.Equal("A=1", edit.EnvText);
        Assert.True(edit.Enabled);
        Assert.False(edit.IsHttp);
        Assert.True(edit.ShowStdio);
        Assert.False(edit.ShowHttp);
        Assert.True(edit.IsExternal);
        Assert.Contains("boom", edit.ValidationText);
        Assert.Equal("停用", edit.EnableButtonText);   // 已启用 → 按钮给"停用"
    }

    [Fact]
    public void Builtin_entry_is_not_editable_as_external_transport()
    {
        var edit = McpServerEdit.From(AgentMcpServerConfig.BuiltinDefault());

        Assert.True(edit.Builtin);
        Assert.False(edit.IsExternal);
        Assert.False(edit.ShowStdio);
        Assert.False(edit.ShowHttp);
        Assert.Equal("stdio（本机子进程）", edit.TransportText);

        // 内置即使被改成 http 也会按 stdio 落盘（传输配置不可改）
        edit.Transport = "http";
        Assert.Equal("stdio", edit.ToModel().Transport);
    }

    [Fact]
    public void Enable_button_text_follows_enabled_state()
    {
        var edit = new McpServerEdit();
        Assert.Equal("启用", edit.EnableButtonText);
        edit.Enabled = true;
        Assert.Equal("停用", edit.EnableButtonText);
        Assert.Equal("启用中", edit.EnabledText);
    }
}
