using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Views;

/// <summary>
/// 会话录像 / 日志查看：
///  - <c>.log</c>：纯文本（去 ANSI）静态查看；
///  - <c>.cast</c>（asciicast v2）：按时间戳**回放**（播放/暂停/重开/倍速），可导出为去 ANSI 文本。
/// </summary>
public partial class TerminalLogWindow : Wpf.Ui.Controls.FluentWindow
{
    private static readonly Regex AnsiRegex = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07]*\x07|\x1B[@-Z\\-_]", RegexOptions.Compiled);
    private readonly string _dir = Path.Combine(ConfigPaths.AppDataDir, "terminal-logs");

    // 回放状态
    private readonly DispatcherTimer _timer;
    private readonly List<CastEvent> _events = new();
    private int _index;
    private double _elapsed;
    private double _total;
    private double _speed = 1;
    private bool _playing;
    private readonly StringBuilder _rendered = new();

    public TerminalLogWindow()
    {
        InitializeComponent();
        WindowLayout.Attach(this, "terminal-logs");
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += OnTick;
        Refresh();
    }

    private void Refresh()
    {
        StopReplay();
        var items = new List<LogFileItem>();
        try
        {
            if (Directory.Exists(_dir))
            {
                foreach (var file in Directory.GetFiles(_dir))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is not (".log" or ".cast"))
                        continue;
                    items.Add(new LogFileItem(file, Path.GetFileName(file), File.GetLastWriteTime(file), ext == ".cast"));
                }
            }
        }
        catch
        {
            // 读取失败
        }
        LogList.ItemsSource = items.OrderByDescending(i => i.Modified).ToList();
        StatusText.Text = items.Count == 0
            ? "暂无录像（需在 设置→终端 开启「记录会话日志」）"
            : $"{items.Count} 个录像/日志（.cast 支持回放）";
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private void OnSelect(object sender, SelectionChangedEventArgs e)
    {
        StopReplay();
        if (LogList.SelectedItem is not LogFileItem item)
            return;

        if (item.IsCast)
        {
            StartCast(item.Path);
            return;
        }

        ReplayPanel.Visibility = Visibility.Collapsed;
        try
        {
            ContentBox.Text = AnsiRegex.Replace(SessionLogReader.ReadAllText(item.Path), string.Empty);
        }
        catch (Exception ex)
        {
            ContentBox.Text = "读取失败: " + ex.Message;
        }
    }

    // ---- 回放 ----

    private void StartCast(string path)
    {
        _events.Clear();
        _index = 0;
        _elapsed = 0;
        _rendered.Clear();
        ContentBox.Text = string.Empty;

        try
        {
            var lines = SessionLogReader.ReadAllLines(path);
            foreach (var line in lines)
            {
                if (line.Length == 0 || line[0] != '[')
                    continue;   // 跳过头部
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var t = root[0].GetDouble();
                    var type = root[1].GetString();
                    var text = root[2].GetString() ?? string.Empty;
                    if (type is "o" or "i")
                        _events.Add(new CastEvent(t, type!, text));
                }
                catch
                {
                    // 跳过坏行
                }
            }
        }
        catch (Exception ex)
        {
            ReplayPanel.Visibility = Visibility.Collapsed;
            ContentBox.Text = "读取录像失败: " + ex.Message;
            return;
        }

        _total = _events.Count > 0 ? _events[^1].T : 0;
        ReplayPanel.Visibility = Visibility.Visible;
        _playing = false;
        PlayButton.Content = "▶ 播放";
        UpdateProgress();
        StatusText.Text = $"{Path.GetFileName(path)}：{_events.Count} 个事件，时长 {_total:F1}s";
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (_events.Count == 0)
            return;
        if (_playing)
        {
            StopTimer();
            PlayButton.Content = "▶ 播放";
        }
        else
        {
            if (_index >= _events.Count)
                ResetPlayback();
            _timer.Start();
            _playing = true;
            PlayButton.Content = "⏸ 暂停";
        }
    }

    private void OnRestart(object sender, RoutedEventArgs e)
    {
        if (_events.Count == 0)
            return;
        ResetPlayback();
        if (_playing || ContentBox.Text.Length == 0)
        {
            _timer.Start();
            _playing = true;
            PlayButton.Content = "⏸ 暂停";
        }
    }

    private void ResetPlayback()
    {
        _index = 0;
        _elapsed = 0;
        _rendered.Clear();
        ContentBox.Text = string.Empty;
        UpdateProgress();
    }

    private void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        _speed = SpeedBox.SelectedIndex switch { 1 => 2, 2 => 4, 3 => 8, _ => 1 };
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _elapsed += 0.05 * _speed;
        var changed = false;
        while (_index < _events.Count && _events[_index].T <= _elapsed)
        {
            var ev = _events[_index++];
            if (ev.Type == "o")
            {
                _rendered.Append(AnsiRegex.Replace(ev.Text, string.Empty));
                changed = true;
            }
        }
        if (changed)
            ContentBox.Text = _rendered.ToString();
        UpdateProgress();

        if (_index >= _events.Count && _elapsed >= _total)
        {
            StopTimer();
            _playing = false;
            PlayButton.Content = "▶ 播放";
        }
    }

    private void StopTimer()
    {
        _timer.Stop();
    }

    private void StopReplay()
    {
        StopTimer();
        _playing = false;
        _events.Clear();
        _index = 0;
        _elapsed = 0;
        _total = 0;
        _rendered.Clear();
        ReplayPanel.Visibility = Visibility.Collapsed;
    }

    private void UpdateProgress() =>
        ProgressText.Text = _total > 0 ? $"{_elapsed:F1}s / {_total:F1}s" : string.Empty;

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            NativeShell.OpenFolder(_dir);
        }
        catch
        {
            // ignore
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (LogList.SelectedItem is not LogFileItem item)
            return;
        if (MessageBox.Show($"删除「{item.Name}」？", "删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            File.Delete(item.Path);
            // .cast 与 .log 同名成对：一并删除
            var sibling = Path.ChangeExtension(item.Path, item.IsCast ? ".log" : ".cast");
            if (File.Exists(sibling))
                File.Delete(sibling);
        }
        catch { /* ignore */ }
        Refresh();
    }

    private sealed record CastEvent(double T, string Type, string Text);

    private sealed record LogFileItem(string Path, string Name, DateTime Modified, bool IsCast)
    {
        public string ModifiedText => (IsCast ? "[录像] " : "[日志] ") + Modified.ToString("yyyy-MM-dd HH:mm");
    }
}
