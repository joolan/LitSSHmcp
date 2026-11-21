using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LitSSHmcp.App.Controls;
using Xunit;
using Xunit.Abstractions;

namespace LitSSHmcp.App.Tests;

/// <summary>
/// 终端性能基准（默认跳过）。启用方式：
/// <c>LITSSH_TERM_BENCH=1 dotnet test --filter TerminalBenchmarkTests</c>。
/// 覆盖：解析吞吐、滚动渲染、行缓存开关对比、光标刷新、单字符回显。
/// </summary>
public class TerminalBenchmarkTests
{
    private const int BenchmarkWidth = 1200;
    private const int BenchmarkHeight = 700;
    private const int Frames = 60;

    private readonly ITestOutputHelper _output;

    public TerminalBenchmarkTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Benchmark_parse_and_render()
    {
        if (Environment.GetEnvironmentVariable("LITSSH_TERM_BENCH") != "1")
            return;

        BenchmarkParse();
        BenchmarkRender();
    }

    private void BenchmarkParse()
    {
        const int lines = 30_000;
        var text = BuildOutput(lines);
        var chunks = Chunk(text, 8192);
        var model = new TerminalModel(120, 40);

        // 预热 JIT/解析器
        model.Feed("warmup\r\n");

        var sw = Stopwatch.StartNew();
        foreach (var chunk in chunks)
            model.Feed(chunk);
        sw.Stop();

        var mb = Encoding.UTF8.GetByteCount(text) / 1024.0 / 1024.0;
        _output.WriteLine(
            $"parse: {sw.Elapsed.TotalMilliseconds:F1}ms, {mb:F2}MB, {mb / sw.Elapsed.TotalSeconds:F1}MB/s, " +
            $"{lines / sw.Elapsed.TotalSeconds:F0} lines/s, chunks={chunks.Count}, scrollback={model.ScrollbackCount}");
    }

    private void BenchmarkRender()
    {
        RunSta(() =>
        {
            var model = new TerminalModel(120, 40);
            for (var i = 0; i < 2000; i++)
                model.Feed(BuildLine(i));

            var view = new TerminalView
            {
                Session = new FakeSession(model),
                Width = BenchmarkWidth,
                Height = BenchmarkHeight
            };
            view.Measure(new Size(BenchmarkWidth, BenchmarkHeight));
            view.Arrange(new Rect(0, 0, BenchmarkWidth, BenchmarkHeight));
            view.UpdateLayout();

            var bitmap = new RenderTargetBitmap(BenchmarkWidth, BenchmarkHeight, 96, 96, PixelFormats.Pbgra32);
            for (var i = 0; i < 5; i++)
                bitmap.Render(view);   // 预热 WPF 绘制/字体缓存

            var cacheField = typeof(TerminalView).GetField("_rowDrawingCache",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(cacheField);
            var cache = cacheField!.GetValue(view);
            var onRender = typeof(TerminalView).GetMethod("OnRender",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(onRender);

            // 测量 OnRender 的绘制构建（不含 RenderTargetBitmap 软件栅格化，贴近 WPF GPU 合成时的 CPU 成本）。
            var scrollWithCache = MeasureBuildFrames(onRender!, view, () =>
            {
                for (var i = 0; i < 5; i++)
                    model.Feed(BuildLine(3000 + i));
            });

            var scrollNoCache = MeasureBuildFrames(onRender!, view, () =>
            {
                for (var i = 0; i < 5; i++)
                    model.Feed(BuildLine(4000 + i));
                (cache as System.Collections.IDictionary)?.Clear();
            });

            var cursorOnly = MeasureBuildFrames(onRender!, view, () => { });

            var echo = new List<double>(200);
            var echoSw = new Stopwatch();
            for (var i = 0; i < 200; i++)
            {
                echoSw.Restart();
                model.Feed(((char)('a' + i % 26)).ToString());
                var group = new DrawingGroup();
                using (var dc = group.Open())
                    onRender!.Invoke(view, new object[] { dc });
                echoSw.Stop();
                echo.Add(echoSw.Elapsed.TotalMilliseconds);
            }

            _output.WriteLine($"build.scroll.cache_on: avg={Avg(scrollWithCache):F2}ms p95={P95(scrollWithCache):F2}ms");
            _output.WriteLine($"build.scroll.cache_off: avg={Avg(scrollNoCache):F2}ms p95={P95(scrollNoCache):F2}ms");
            _output.WriteLine($"build.cursor_only: avg={Avg(cursorOnly):F2}ms p95={P95(cursorOnly):F2}ms");
            _output.WriteLine($"build.echo_char: avg={Avg(echo):F2}ms p95={P95(echo):F2}ms");
        });
    }

    private static List<double> MeasureBuildFrames(MethodInfo onRender, TerminalView view, Action beforeFrame)
    {
        var samples = new List<double>(Frames);
        var sw = new Stopwatch();
        for (var i = 0; i < Frames; i++)
        {
            beforeFrame();
            sw.Restart();
            var group = new DrawingGroup();
            using (var dc = group.Open())
                onRender.Invoke(view, new object[] { dc });
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        return samples;
    }

    private static double Avg(List<double> values) => values.Count == 0 ? 0 : values.Average();

    private static double P95(List<double> values)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.OrderBy(v => v).ToArray();
        var index = Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * 0.95) - 1);
        return sorted[index];
    }

    private static string BuildOutput(int lines)
    {
        var sb = new StringBuilder(lines * 96);
        for (var i = 0; i < lines; i++)
            sb.Append(BuildLine(i));
        return sb.ToString();
    }

    private static string BuildLine(int i) =>
        $"\u001b[32mOK\u001b[0m \u001b[90m{i:D6}\u001b[0m  deploy/service-{i % 20:D2} " +
        $"latency={i % 300}ms status=healthy \u001b[33mwarning\u001b[0m\r\n";

    private static List<string> Chunk(string text, int size)
    {
        var chunks = new List<string>((text.Length / size) + 1);
        for (var offset = 0; offset < text.Length; offset += size)
            chunks.Add(text.Substring(offset, Math.Min(size, text.Length - offset)));
        return chunks;
    }

    private static void RunSta(Action action)
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
        Assert.Null(captured);
    }

    private sealed class FakeSession(TerminalModel model) : ITerminalSession
    {
        public TerminalModel Model { get; } = model;
        public Task EnsureStartedAsync(int cols, int rows) => Task.CompletedTask;
        public void SendInput(string text) { }
        public void SendUserInput(string text) { }
        public void Resize(int cols, int rows) => Model.Resize(cols, rows);
    }
}
