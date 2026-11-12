using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.Toolbox;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views.Tools;

public partial class CertificateToolWindow : FluentWindow
{
    private const int MaxConcurrency = 8;
    private static readonly TimeSpan PerTargetTimeout = TimeSpan.FromSeconds(10);
    private static readonly Encoding CsvEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    private readonly CertificateInspector _inspector = new();
    private readonly ObservableCollection<CertificateCheckResult> _results = new();
    private CancellationTokenSource? _cts;

    public CertificateToolWindow()
    {
        InitializeComponent();
        ResultsGrid.ItemsSource = _results;
        Loaded += (_, _) =>
        {
            UpdateInputHint();
            UpdateSummary();
            UrlInput.Focus();
        };
    }

    private void OnCheckAll(object sender, RoutedEventArgs e)
    {
        if (_cts is not null)
            return;
        _ = CheckAllAsync();
    }

    private async Task CheckAllAsync()
    {
        if (_cts is not null)
            return;

        var targets = CertificateTargetParser.SplitInput(UrlInput.Text);
        if (targets.Count == 0)
        {
            StatusText.Text = "请先输入至少一个 URL 或主机名。";
            return;
        }

        _results.Clear();
        DetailBox.Text = string.Empty;
        UpdateSummary();
        var cts = new CancellationTokenSource();
        _cts = cts;
        SetRunning(true);
        StatusText.Text = $"正在检测 0/{targets.Count}...";

        try
        {
            using var gate = new SemaphoreSlim(MaxConcurrency);
            var tasks = targets.Select(async target =>
            {
                await gate.WaitAsync(cts.Token);
                try
                {
                    var result = await _inspector.CheckAsync(target, PerTargetTimeout, cts.Token);
                    if (cts.IsCancellationRequested)
                        return;

                    _results.Add(result);
                    UpdateSummary();
                    StatusText.Text = $"正在检测 {_results.Count}/{targets.Count}...";
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();

            await Task.WhenAll(tasks);
            StatusText.Text = cts.IsCancellationRequested
                ? $"已停止，完成 {_results.Count}/{targets.Count} 个目标。"
                : $"检测完成：{_results.Count} 个目标。";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"已停止，完成 {_results.Count}/{targets.Count} 个目标。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "检测失败：" + ex.Message;
        }
        finally
        {
            SetRunning(false);
            cts.Dispose();
            if (ReferenceEquals(_cts, cts))
                _cts = null;
            UpdateSummary();
        }
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StopButton.IsEnabled = false;
        StatusText.Text = "正在停止...";
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (_cts is not null)
            return;

        UrlInput.Clear();
        _results.Clear();
        DetailBox.Text = string.Empty;
        StatusText.Text = string.Empty;
        UpdateSummary();
        UpdateInputHint();
    }

    private void OnImportFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入 URL 列表",
            Filter = "URL 列表 (*.txt;*.csv)|*.txt;*.csv|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            AppendInput(File.ReadAllText(dialog.FileName));
            StatusText.Text = "已导入：" + Path.GetFileName(dialog.FileName);
        }
        catch (Exception ex)
        {
            StatusText.Text = "导入失败：" + ex.Message;
        }
    }

    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        if (_results.Count == 0)
        {
            StatusText.Text = "没有可导出的检测结果。";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出证书检测结果",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"tls-certificates-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var csv = new StringBuilder();
            csv.AppendLine(CertificateCheckResult.CsvHeader);
            foreach (var result in _results)
                csv.AppendLine(result.ToCsvRow());

            File.WriteAllText(dialog.FileName, csv.ToString(), CsvEncoding);
            StatusText.Text = "已导出：" + dialog.FileName;
        }
        catch (Exception ex)
        {
            StatusText.Text = "导出失败：" + ex.Message;
        }
    }

    private void OnCopyDetails(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DetailBox.Text))
            return;

        try
        {
            Clipboard.SetText(DetailBox.Text);
            StatusText.Text = "已复制详情。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "复制失败：" + ex.Message;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DetailBox.Text = ResultsGrid.SelectedItem is CertificateCheckResult result
            ? result.Details
            : string.Empty;
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e) => UpdateInputHint();

    private void OnInputDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            || e.Data.GetDataPresent(DataFormats.UnicodeText)
            || e.Data.GetDataPresent(DataFormats.Text)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnInputDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)
                && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                foreach (var file in files)
                    AppendInput(File.ReadAllText(file));
                StatusText.Text = $"已导入 {files.Length} 个文件。";
            }
            else if (e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                AppendInput((string?)e.Data.GetData(DataFormats.UnicodeText) ?? string.Empty);
                StatusText.Text = "已导入文本。";
            }
            else if (e.Data.GetDataPresent(DataFormats.Text))
            {
                AppendInput((string?)e.Data.GetData(DataFormats.Text) ?? string.Empty);
                StatusText.Text = "已导入文本。";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "导入失败：" + ex.Message;
        }
        finally
        {
            e.Handled = true;
        }
    }

    private void AppendInput(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var current = UrlInput.Text.TrimEnd();
        UrlInput.Text = current.Length == 0
            ? text.Trim()
            : current + Environment.NewLine + text.Trim();
        UpdateInputHint();
    }

    private void SetRunning(bool running)
    {
        CheckButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        UrlInput.IsReadOnly = running;
    }

    private void UpdateSummary()
    {
        var total = _results.Count;
        if (total == 0)
        {
            SummaryText.Text = "尚未检测";
            return;
        }

        var ok = _results.Count(item => item.Severity == "Ok");
        var warning = _results.Count(item => item.Severity == "Warning");
        var error = _results.Count(item => item.Severity == "Error");
        SummaryText.Text = $"已检测 {total} 个目标 · 正常 {ok} · 警告 {warning} · 错误 {error}";
    }

    private void UpdateInputHint()
        => InputHint.Text = CertificateTargetParser.SplitInput(UrlInput.Text).Count is var count && count > 0
            ? $"{count} 个目标"
            : "等待输入";
}
