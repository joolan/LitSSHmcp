using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.Services;
using Markdig;
using Microsoft.Web.WebView2.Core;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public enum HelpTopic
{
    QuickStart,
    Features,
    Shortcuts,
    Web,
    Security,
    McpTools
}

public partial class HelpWindow : FluentWindow
{
    private const string HelpResourceName = "LitSSHmcp.App.docs.HELP.md";
    private const string ToolsResourceName = "LitSSHmcp.App.docs.TOOLS.md";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private readonly string _helpMarkdown;
    private readonly string _toolsMarkdown;
    private HelpTopic _currentTopic;
    private bool _webReady;

    public HelpWindow(string? topic = null)
    {
        InitializeComponent();
        _helpMarkdown = LoadResource(HelpResourceName);
        _toolsMarkdown = LoadResource(ToolsResourceName);
        _currentTopic = Enum.TryParse<HelpTopic>(topic, true, out var parsed) ? parsed : HelpTopic.QuickStart;
        TopicList.SelectedIndex = (int)_currentTopic;

        Loaded += (_, _) => AsyncOperation.Run("加载使用说明", InitializeWebViewAsync);
        Closed += OnClosed;
        ThemeService.ThemeChanged += OnThemeChanged;
    }

    public void SelectTopic(string? topic)
    {
        if (!Enum.TryParse<HelpTopic>(topic, true, out var parsed))
            return;

        TopicList.SelectedIndex = (int)parsed;
        if (TopicList.SelectedIndex == (int)parsed)
            _currentTopic = parsed;
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LitSSH", "webview2-help");
            Directory.CreateDirectory(userDataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await HelpWeb.EnsureCoreWebView2Async(environment);

            var settings = HelpWeb.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;

            _webReady = true;
            HelpWeb.Visibility = Visibility.Visible;
            FallbackScroll.Visibility = Visibility.Collapsed;
            RenderCurrent();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowFallback("未检测到 Microsoft Edge WebView2 运行时，已切换为简化视图。");
        }
        catch (Exception ex)
        {
            ShowFallback("使用说明渲染失败，已切换为简化视图：" + ex.Message);
        }
    }

    private void OnTopicChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TopicList.SelectedItem is not ListBoxItem { Tag: string tag } ||
            !Enum.TryParse<HelpTopic>(tag, true, out var topic))
        {
            return;
        }

        _currentTopic = topic;
        if (_webReady)
            RenderCurrent();
        else
            SetFallbackMarkdown(GetMarkdown(topic));
    }

    private void RenderCurrent()
    {
        var markdown = GetMarkdown(_currentTopic);
        if (!_webReady)
        {
            SetFallbackMarkdown(markdown);
            return;
        }

        HelpWeb.NavigateToString(BuildHtml(markdown));
    }

    private void ShowFallback(string reason)
    {
        _webReady = false;
        try { HelpWeb.Visibility = Visibility.Collapsed; } catch { /* ignore */ }
        FallbackScroll.Visibility = Visibility.Visible;
        SetFallbackMarkdown($"> {reason}\n\n{GetMarkdown(_currentTopic)}");
    }

    private void SetFallbackMarkdown(string markdown) => FallbackMarkdown.Markdown = markdown;

    private string GetMarkdown(HelpTopic topic)
    {
        if (topic == HelpTopic.McpTools)
            return _toolsMarkdown;

        var heading = topic switch
        {
            HelpTopic.QuickStart => "快速上手",
            HelpTopic.Features => "功能说明",
            HelpTopic.Shortcuts => "快捷键",
            HelpTopic.Web => "Web 端",
            HelpTopic.Security => "安全与审批",
            _ => "快速上手"
        };

        var section = ExtractSection(_helpMarkdown, heading);
        return string.IsNullOrWhiteSpace(section) ? _helpMarkdown : section;
    }

    private static string ExtractSection(string markdown, string heading)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var inFence = false;
        var start = -1;
        var end = lines.Length;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) ||
                trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
                continue;

            if (start < 0)
            {
                if (string.Equals(line.Trim(), $"# {heading}", StringComparison.Ordinal))
                    start = i;
            }
            else if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                end = i;
                break;
            }
        }

        if (start < 0)
            return string.Empty;

        return string.Join('\n', lines[start..end]).Trim();
    }
    private static string BuildHtml(string markdown)
    {
        var body = Markdown.ToHtml(markdown, Pipeline);
        var dark = string.Equals(ThemeService.Current, "dark", StringComparison.OrdinalIgnoreCase);
        var bg = dark ? "#1B1B1B" : "#FAFAFA";
        var text = dark ? "#F5F5F5" : "#1A1A1A";
        var secondary = dark ? "#BFBFBF" : "#666666";
        var border = dark ? "#3A3A3A" : "#E5E7EB";
        var surface = dark ? "#2B2B2B" : "#FFFFFF";
        var codeBg = dark ? "#141414" : "#F2F4F7";
        var accent = dark ? "#4CC2FF" : "#2563EB";

        return $$"""
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="color-scheme" content="light dark">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline';">
<style>
:root { color-scheme: {{(dark ? "dark" : "light")}}; }
* { box-sizing: border-box; }
html, body { margin: 0; padding: 0; background: {{bg}}; color: {{text}}; }
body { font-family: "Segoe UI", "Microsoft YaHei UI", sans-serif; font-size: 14px; line-height: 1.7; padding: 24px 28px 40px; }
main { max-width: 900px; margin: 0 auto; }
h1 { font-size: 26px; line-height: 1.35; margin: 0 0 18px; font-weight: 650; }
h2 { font-size: 19px; margin: 28px 0 10px; padding-bottom: 6px; border-bottom: 1px solid {{border}}; }
h3 { font-size: 15px; margin: 20px 0 8px; }
p, ul, ol, table { margin: 8px 0; }
a { color: {{accent}}; text-decoration: none; }
a:hover { text-decoration: underline; }
ul, ol { padding-left: 24px; }
li { margin: 3px 0; }
table { width: 100%; border-collapse: collapse; margin: 12px 0; }
th, td { border: 1px solid {{border}}; padding: 7px 9px; text-align: left; vertical-align: top; }
th { background: {{surface}}; font-weight: 600; }
code { font-family: "Cascadia Code", Consolas, monospace; font-size: 0.92em; background: {{codeBg}}; padding: 1px 5px; border-radius: 4px; }
pre { background: {{codeBg}}; padding: 12px 14px; border-radius: 6px; overflow: auto; }
pre code { background: transparent; padding: 0; }
blockquote { margin: 12px 0; padding: 8px 12px; border-left: 3px solid {{accent}}; color: {{secondary}}; background: {{surface}}; }
strong { font-weight: 650; }
hr { border: 0; border-top: 1px solid {{border}}; margin: 22px 0; }
</style>
</head>
<body><main>{{body}}</main></body>
</html>
""";
    }

    private static string LoadResource(string name)
    {
        using var stream = typeof(HelpWindow).Assembly.GetManifestResourceStream(name);
        if (stream is null)
            return $"# 文档缺失\n\n未找到嵌入资源 `{name}`。";

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void OnThemeChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnThemeChanged));
            return;
        }

        if (_webReady)
            RenderCurrent();
        else
            SetFallbackMarkdown(GetMarkdown(_currentTopic));
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ThemeService.ThemeChanged -= OnThemeChanged;
        try { HelpWeb.Dispose(); } catch { /* ignore */ }
    }
}
