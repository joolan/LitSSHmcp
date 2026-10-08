using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using LitSSHmcp.Agent;
using LitSSHmcp.App.Services;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;
using Microsoft.Extensions.AI;

namespace LitSSHmcp.App.ViewModels;

/// <summary>
/// 临时聊天（斜杠命令「/临时聊天」）：独立、纯内存的对话——**不注入任何 skill / MCP 工具、不持久化**，
/// 与主运维任务完全隔离；可切换模型（切换时保留已聊内容）；窗口关闭即整体丢弃。
/// </summary>
public sealed class TemporaryChatViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private const string TempSystemPrompt =
        "你是一个临时聊天助手：仅用于普通问答与闲聊。本会话**没有任何工具**，不要尝试调用工具，也不要声称已执行任何本机/远程操作。" +
        "回答请简洁、准确，需要时可用 Markdown。";

    private readonly IConfigService _configService;
    private readonly string? _initialProviderId;

    private AgentConfig _config = new();
    private AgentSession? _session;
    private AgentProviderConfig? _provider;
    private CancellationTokenSource? _cts;

    public TemporaryChatViewModel(IConfigService configService, string? initialProviderId)
    {
        _configService = configService;
        _initialProviderId = initialProviderId;
        SendOrStopCommand = new RelayCommand(_ => { if (IsBusy) _cts?.Cancel(); else _ = SendAsync(); },
            _ => IsBusy || (!string.IsNullOrWhiteSpace(Input) || HasAttachments));
    }

    public ObservableCollection<AgentProviderConfig> Providers { get; } = new();
    public ObservableCollection<AgentTurn> Turns { get; } = new();
    public ObservableCollection<AgentAttachment> Attachments { get; } = new();

    public ICommand SendOrStopCommand { get; }

    public bool HasAttachments => Attachments.Count > 0;

    private string _input = string.Empty;
    public string Input
    {
        get => _input;
        set { if (Set(ref _input, value)) (SendOrStopCommand as RelayCommand)?.RaiseCanExecuteChanged(); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) { OnPropertyChanged(nameof(SendButtonText)); (SendOrStopCommand as RelayCommand)?.RaiseCanExecuteChanged(); } }
    }

    public string SendButtonText => IsBusy ? "停止" : "发送";

    private string _statusMessage = string.Empty;
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    private AgentProviderConfig? _selectedProvider;
    public AgentProviderConfig? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (!Set(ref _selectedProvider, value))
                return;
            if (_provider?.Id != value?.Id)
                RebuildSession();
        }
    }

    public Visibility EmptyStateVisibility => Turns.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public async Task InitializeAsync()
    {
        try
        {
            var cfg = await _configService.LoadConfigAsync();
            _config = cfg.Agent ?? new AgentConfig();
            Providers.Clear();
            foreach (var p in _config.Providers.Where(p => p.Enabled))
                Providers.Add(p);

            var pick = Providers.FirstOrDefault(p => p.Id == _initialProviderId)
                       ?? Providers.FirstOrDefault(p => p.Id == _config.ActiveProviderId)
                       ?? Providers.FirstOrDefault();

            if (pick is null)
                StatusMessage = "没有启用的大模型：请先在「AI 助手设置」里添加并启用模型。";
            SelectedProvider = pick;
        }
        catch (Exception ex)
        {
            StatusMessage = "加载配置失败: " + ex.Message;
        }
    }

    private void RebuildSession()
    {
        if (SelectedProvider is null)
        {
            _provider = null;
            _session = null;
            return;
        }

        // 切换模型时保留已聊内容（取旧会话的消息历史，剔除系统提示）
        var history = _session?.Messages.Where(m => m.Role != ChatRole.System).ToList();
        try
        {
            var client = ChatClientFactory.Create(SelectedProvider);
            _provider = SelectedProvider;
            _session = new AgentSession(client, SelectedProvider, Array.Empty<IAgentTool>(),
                TempSystemPrompt, _config, history, recall: null, spill: null);
            StatusMessage = $"模型：{SelectedProvider.Name} / {SelectedProvider.Model}（临时，不保存）";
        }
        catch (Exception ex)
        {
            _provider = null;
            _session = null;
            StatusMessage = "模型不可用: " + ex.Message;
        }
    }

    private async Task SendAsync()
    {
        if (IsBusy)
            return;
        if (_session is null)
        {
            StatusMessage = "模型不可用，无法发送。";
            return;
        }

        var text = Input?.Trim() ?? string.Empty;
        var pending = Attachments.ToList();
        if (text.Length == 0 && pending.Count == 0)
            return;

        Input = string.Empty;
        var (display, contents) = BuildContents(text, pending);
        Attachments.Clear();
        OnPropertyChanged(nameof(HasAttachments));

        var turn = new AgentTurn(display, DateTime.UtcNow);
        Turns.Add(turn);
        OnPropertyChanged(nameof(EmptyStateVisibility));

        IsBusy = true;
        _cts = new CancellationTokenSource();
        var progress = new Progress<AgentEvent>(e => OnEvent(e, turn));
        try
        {
            await _session.SendAsync(contents, progress, _cts.Token);
            StatusMessage = "完成（临时聊天，不保存）";
        }
        catch (OperationCanceledException)
        {
            turn.Note = "已停止。";
        }
        catch (Exception ex)
        {
            turn.Note = ChatErrorClassifier.Describe(ex);
            StatusMessage = turn.Note;
        }
        finally
        {
            turn.MarkDone(DateTime.UtcNow);
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnEvent(AgentEvent e, AgentTurn turn)
    {
        switch (e.Kind)
        {
            case AgentEventKind.AssistantText:
                if (e.Text is { Length: > 0 } chunk)
                    turn.AssistantText += chunk;
                break;
            case AgentEventKind.Error:
                turn.Note = e.Text ?? "发生错误";
                break;
        }
    }

    /// <summary>把文本 + 附件构造成模型输入（图片走 DataContent，文档文本内联；均只在内存，不落盘）。</summary>
    private (string Display, IReadOnlyList<AIContent> Contents) BuildContents(string text, IReadOnlyList<AgentAttachment> attachments)
    {
        if (attachments.Count == 0)
            return (text, new AIContent[] { new TextContent(text) });

        var vision = _provider?.SupportsVision == true;
        var display = new StringBuilder(text);
        var contents = new List<AIContent>();

        foreach (var a in attachments)
        {
            if (a.IsImage)
            {
                if (vision && a.ImageBytes is { Length: > 0 })
                {
                    display.Append($"\n🖼 图片: {a.Name}");
                    contents.Add(new DataContent(a.ImageBytes, a.MediaType ?? "image/png"));
                }
                else
                {
                    display.Append($"\n🖼 图片: {a.Name}（当前模型未开启视觉，已忽略）");
                }
                continue;
            }

            display.Append($"\n📎 文件: {a.Name}");
            contents.Add(new TextContent($"\n【附件 {a.Name}】\n{a.Text}"));
        }

        contents.Insert(0, new TextContent(display.ToString()));
        return (display.ToString(), contents);
    }

    public void AddAttachmentFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (Attachments.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var parsed = AttachmentService.Parse(path);
                Attachments.Add(new AgentAttachment
                {
                    Name = parsed.Name,
                    Path = path,
                    Kind = parsed.Kind,
                    MediaType = parsed.MediaType,
                    ImageBytes = parsed.ImageBytes,
                    Text = parsed.Text
                });
            }
            catch (Exception ex)
            {
                StatusMessage = "附件读取失败: " + ex.Message;
            }
        }
        OnPropertyChanged(nameof(HasAttachments));
        (SendOrStopCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public void RemoveAttachment(AgentAttachment attachment)
    {
        Attachments.Remove(attachment);
        OnPropertyChanged(nameof(HasAttachments));
        (SendOrStopCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public void ClearAttachments()
    {
        if (Attachments.Count == 0)
            return;
        Attachments.Clear();
        OnPropertyChanged(nameof(HasAttachments));
        (SendOrStopCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
        await ValueTask.CompletedTask;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
