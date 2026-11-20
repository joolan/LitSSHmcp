using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
using LitSSHmcp.App.Controls;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.RemoteFiles;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Security;
using LitSSHmcp.Core.Services.SSH;

namespace LitSSHmcp.App.Views;

public partial class RemoteFileEditorWindow : Wpf.Ui.Controls.FluentWindow
{
    private static readonly ConcurrentDictionary<string, WeakReference<RemoteFileEditorWindow>> Windows = new();
    private readonly RemoteFileEditorViewModel _vm;
    private readonly ISshService _ssh;
    private readonly IUserDialogService _dialogs = new UserDialogService();
    private readonly string _initialDirectory;
    private bool _suppressEditor;
    private bool _suppressEncoding;
    private bool _forceClose;
    private bool _closeInProgress;
    private bool _saveAllInProgress;
    private RemoteFileEditTab? _dragTab;
    private Point _dragStart;
    private readonly Dictionary<RemoteFileEditTab, TextDocument> _documents = new();
    private FindHighlightTransformer? _findTransformer;
    private ConfigSyntaxTransformer? _configSyntax;
    private readonly List<int> _findMatches = new();
    private int _findCurrentIndex = -1;
    private string _findQuery = string.Empty;
    private bool _findCaseSensitive;
    private bool _findWholeWord;
    private bool _findRegex;
    private readonly DispatcherTimer _externalCheckTimer;
    private string? _externalPromptKey;
    private bool _restoreStarted;
    private bool _restoringTabs;

    internal RemoteFileEditorWindow(SshServerConfig server, ISshService ssh, string initialDirectory)
    {
        InitializeComponent();
        _ssh = ssh;
        _initialDirectory = string.IsNullOrWhiteSpace(initialDirectory) ? "/" : initialDirectory;
        _vm = new RemoteFileEditorViewModel(server, ssh);
        DataContext = _vm;
        _vm.TabLoaded += OnTabLoaded;
        _vm.FileSaved += directory => FileSaved?.Invoke(directory);

        EncodingBox.ItemsSource = RemoteFileEditorViewModel.EncodingNames;
        LineEndingBox.ItemsSource = RemoteFileEditorViewModel.LineEndingNames;
        EncodingBox.SelectedItem = "utf-8";
        LineEndingBox.SelectedItem = "LF";
        Editor.TextChanged += OnEditorTextChanged;
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateCursorInfo();
        TitleBarControl.Title = "在线文件编辑器 - " + server.Name;
        WindowLayout.Attach(this, "remote-file-editor");
        _externalCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _externalCheckTimer.Tick += (_, _) => _ = CheckExternalChangeAsync();
        _externalCheckTimer.Start();
        Loaded += (_, _) => _ = RestoreTabsAsync();
        Closed += (_, _) =>
        {
            _externalCheckTimer.Stop();
            Windows.TryRemove(server.Id, out _);
        };
        UpdateEditorFromSelected();
    }

    public event Action<string>? FileSaved;

    private string? CurrentPath => _vm.SelectedTab?.RemotePath;

    public static RemoteFileEditorWindow ShowForServer(
        Window? owner, SshServerConfig server, ISshService ssh, string initialDirectory)
    {
        if (Windows.TryGetValue(server.Id, out var weak) &&
            weak.TryGetTarget(out var existing) && existing.IsLoaded)
        {
            existing.Activate();
            return existing;
        }

        // 不设置 Owner：编辑器与 SSH 会话并列，不能始终压在被打开窗口上方。
        var window = new RemoteFileEditorWindow(server, ssh, initialDirectory);
        Windows[server.Id] = new WeakReference<RemoteFileEditorWindow>(window);
        window.Show();
        return window;
    }

    public async Task OpenExistingAsync(RemoteFileItem item)
    {
        Activate();
        var tab = await _vm.OpenExistingAsync(item);
        if (tab is not null)
        {
            _vm.SelectedTab = tab;
            UpdateEditorFromSelected();
            PersistTabsState();
        }
        else
        {
            _dialogs.Warn(_vm.StatusMessage, "在线编辑");
        }
    }

    public void OpenBlank(string? directory = null)
    {
        Activate();
        _vm.OpenBlank(string.IsNullOrWhiteSpace(directory) ? _initialDirectory : directory);
        UpdateEditorFromSelected();
        Editor.Focus();
    }

    private void OnTabLoaded(RemoteFileEditTab tab)
    {
        if (ReferenceEquals(_vm.SelectedTab, tab))
            UpdateEditorFromSelected();
    }

    private void SetEditorDocument(RemoteFileEditTab tab)
    {
        if (!_documents.TryGetValue(tab, out var document))
        {
            document = new TextDocument(tab.Content);
            _documents[tab] = document;
        }
        else if (!string.Equals(document.Text, tab.Content, StringComparison.Ordinal))
        {
            // 重新加载/切换编码等外部内容变化应丢弃旧撤销栈；普通 Tab 切换保留各自文档。
            document = new TextDocument(tab.Content);
            _documents[tab] = document;
        }

        if (!ReferenceEquals(Editor.Document, document))
            Editor.Document = document;
    }

    private void UpdateEditorFromSelected()
    {
        var tab = _vm.SelectedTab;
        if (tab is null)
        {
            _suppressEditor = true;
            Editor.Text = string.Empty;
            _suppressEditor = false;
            Editor.IsEnabled = false;
            LoadingOverlay.Visibility = Visibility.Collapsed;
            UpdateCursorInfo();
            return;
        }

        _suppressEditor = true;
        try
        {
            Editor.IsEnabled = true;
            SetEditorDocument(tab);
            ApplySyntax(tab);
        }
        finally
        {
            _suppressEditor = false;
        }

        _suppressEncoding = true;
        try
        {
            EncodingBox.SelectedItem = EncodingNameOf(tab.Encoding, tab.HasBom);
            LineEndingBox.SelectedItem = tab.LineEnding;
        }
        finally
        {
            _suppressEncoding = false;
        }

        LoadingOverlay.Visibility = tab.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        LoadingText.Text = tab.Status;
        Editor.IsReadOnly = tab.IsLoading;
        UpdateCursorInfo();
        if (FindBar.Visibility == Visibility.Visible)
        {
            RebuildFindMatches();
            UpdateFindDisplay();
        }
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEditor)
            return;
        UpdateEditorFromSelected();
        PersistTabsState();
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        UpdateCursorInfo();
        if (_suppressEditor || _vm.SelectedTab is not { } tab || tab.IsLoading)
            return;

        if (!string.Equals(tab.Content, Editor.Text, StringComparison.Ordinal))
        {
            tab.Content = Editor.Text;
            tab.IsDirty = true;
        }
    }

    private void ApplySyntax(RemoteFileEditTab tab)
    {
        try
        {
            if (_configSyntax is not null)
            {
                Editor.TextArea.TextView.LineTransformers.Remove(_configSyntax);
                _configSyntax = null;
            }

            var language = ConfigLanguageFor(tab.RemotePath ?? tab.DisplayName);
            if (language is not null)
            {
                Editor.SyntaxHighlighting = null;
                _configSyntax = new ConfigSyntaxTransformer(language);
                Editor.TextArea.TextView.LineTransformers.Add(_configSyntax);
                Editor.TextArea.TextView.Redraw();
                return;
            }

            var extension = Path.GetExtension(tab.RemotePath ?? tab.DisplayName);
            Editor.SyntaxHighlighting = string.IsNullOrWhiteSpace(extension)
                ? null
                : HighlightingManager.Instance.GetDefinitionByExtension(extension);
            Editor.TextArea.TextView.Redraw();
        }
        catch
        {
            Editor.SyntaxHighlighting = null;
        }
    }

    private static string? ConfigLanguageFor(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name == "dockerfile" || name.StartsWith("dockerfile.", StringComparison.Ordinal))
            return "dockerfile";
        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".conf" or ".nginx" => "conf",
            ".properties" => "properties",
            ".ini" => "ini",
            ".toml" => "toml",
            _ => null
        };
    }

    private void OnEditorPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            return;
        Editor.FontSize = Math.Clamp(Editor.FontSize + (e.Delta > 0 ? 1 : -1), 9, 30);
        e.Handled = true;
    }

    private void OnEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ShowFindBar();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && FindBar.Visibility == Visibility.Visible)
        {
            HideFindBar();
            e.Handled = true;
        }
    }

    private void OnFindBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FindNext(!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HideFindBar();
            e.Handled = true;
        }
    }

    private void ShowFindBar()
    {
        FindBar.Visibility = Visibility.Visible;
        Panel.SetZIndex(FindBar, 20);
        EnsureFindTransformer();
        RebuildFindMatches();
        UpdateFindDisplay();
        FindBox.Focus();
        FindBox.SelectAll();
    }

    private void HideFindBar()
    {
        FindBar.Visibility = Visibility.Collapsed;
        FindCountText.Text = string.Empty;
        if (_findTransformer is not null)
        {
            Editor.TextArea.TextView.LineTransformers.Remove(_findTransformer);
            _findTransformer = null;
        }
        Editor.Focus();
    }

    private void OnFindTextChanged(object sender, TextChangedEventArgs e)
    {
        if (FindBar.Visibility != Visibility.Visible)
            return;
        _findQuery = FindBox.Text;
        _findCurrentIndex = -1;
        RebuildFindMatches();
        UpdateFindDisplay();
    }

    private void OnFindNext(object sender, RoutedEventArgs e) => FindNext(true);

    private void OnFindPrevious(object sender, RoutedEventArgs e) => FindNext(false);

    private void OnFindClose(object sender, RoutedEventArgs e) => HideFindBar();

    private void EnsureFindTransformer()
    {
        if (_findTransformer is not null)
            return;
        _findTransformer = new FindHighlightTransformer();
        Editor.TextArea.TextView.LineTransformers.Add(_findTransformer);
    }

    private void OnFindOptionChanged(object sender, RoutedEventArgs e)
    {
        _findCaseSensitive = CaseSensitiveBox.IsChecked == true;
        _findWholeWord = WholeWordBox.IsChecked == true;
        _findRegex = RegexBox.IsChecked == true;
        if (FindBar.Visibility == Visibility.Visible)
        {
            RebuildFindMatches();
            UpdateFindDisplay();
        }
    }

    private void RebuildFindMatches()
    {
        _findMatches.Clear();
        _findQuery = FindBox.Text;
        _findCurrentIndex = -1;
        if (string.IsNullOrEmpty(_findQuery) || Editor.Text.Length == 0)
        {
            RefreshFindTransformer();
            return;
        }

        _findMatches.AddRange(FindAllMatches(Editor.Text, _findQuery));
        RefreshFindTransformer();
    }

    private List<int> FindAllMatches(string text, string query)
    {
        var result = new List<int>();
        if (string.IsNullOrEmpty(query))
            return result;

        if (_findRegex)
        {
            try
            {
                var options = RegexOptions.Multiline;
                if (!_findCaseSensitive)
                    options |= RegexOptions.IgnoreCase;
                foreach (Match match in Regex.Matches(text, query, options))
                {
                    if (match.Length > 0)
                        result.Add(match.Index);
                }
            }
            catch (ArgumentException)
            {
                FindCountText.Text = "正则无效";
            }
            return result;
        }

        var comparison = _findCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var index = 0;
        while (index <= text.Length - query.Length)
        {
            index = text.IndexOf(query, index, comparison);
            if (index < 0)
                break;
            if (!_findWholeWord || IsWholeWord(text, index, query.Length))
                result.Add(index);
            index += Math.Max(1, query.Length);
        }
        return result;
    }

    private static bool IsWholeWord(string text, int index, int length)
    {
        static bool IsWord(char ch) => char.IsLetterOrDigit(ch) || ch == '_';
        var before = index == 0 || !IsWord(text[index - 1]);
        var end = index + length;
        var after = end >= text.Length || !IsWord(text[end]);
        return before && after;
    }

    private void OnReplace(object sender, RoutedEventArgs e) => ReplaceCurrent();

    private void OnReplaceAll(object sender, RoutedEventArgs e) => ReplaceAll();

    private void ReplaceCurrent()
    {
        if (string.IsNullOrEmpty(FindBox.Text))
            return;
        if (!string.Equals(_findQuery, FindBox.Text, StringComparison.Ordinal) || _findMatches.Count == 0)
            RebuildFindMatches();
        if (_findMatches.Count == 0)
        {
            UpdateFindDisplay();
            return;
        }
        if (_findCurrentIndex < 0)
        {
            FindNext(true);
            return;
        }

        var start = _findMatches[_findCurrentIndex];
        Editor.Document.Replace(start, FindBox.Text.Length, ReplaceBox.Text);
        RebuildFindMatches();
        FindNext(true);
    }

    private void ReplaceAll()
    {
        if (string.IsNullOrEmpty(FindBox.Text))
            return;
        if (!string.Equals(_findQuery, FindBox.Text, StringComparison.Ordinal) || _findMatches.Count == 0)
            RebuildFindMatches();
        if (_findMatches.Count == 0)
        {
            UpdateFindDisplay();
            return;
        }

        if (_findRegex)
        {
            try
            {
                var options = RegexOptions.Multiline;
                if (!_findCaseSensitive)
                    options |= RegexOptions.IgnoreCase;
                Editor.Document.Text = Regex.Replace(Editor.Text, FindBox.Text, ReplaceBox.Text, options);
            }
            catch (ArgumentException)
            {
                FindCountText.Text = "正则无效";
                return;
            }
        }
        else
        {
            for (var i = _findMatches.Count - 1; i >= 0; i--)
                Editor.Document.Replace(_findMatches[i], FindBox.Text.Length, ReplaceBox.Text);
        }

        RebuildFindMatches();
        UpdateFindDisplay();
    }

    private void RefreshFindTransformer()
    {
        if (_findTransformer is null)
            return;
        _findTransformer.Query = _findQuery;
        _findTransformer.Matches = _findMatches;
        _findTransformer.CurrentIndex = _findCurrentIndex;
        Editor.TextArea.TextView.Redraw();
    }

    private void UpdateFindDisplay()
    {
        if (_findMatches.Count == 0)
        {
            FindCountText.Text = "0/0";
            return;
        }
        FindCountText.Text = _findCurrentIndex >= 0
            ? $"{_findCurrentIndex + 1}/{_findMatches.Count}"
            : $"0/{_findMatches.Count}";
    }

    private void FindNext(bool forward)
    {
        if (!string.Equals(_findQuery, FindBox.Text, StringComparison.Ordinal) || _findMatches.Count == 0)
            RebuildFindMatches();

        if (_findMatches.Count == 0)
        {
            UpdateFindDisplay();
            return;
        }

        var selectionStart = Editor.SelectionStart;
        int next;
        if (forward)
        {
            next = _findMatches.FindIndex(m => m > selectionStart);
            if (next < 0)
                next = 0;
        }
        else
        {
            next = _findMatches.FindLastIndex(m => m < selectionStart);
            if (next < 0)
                next = _findMatches.Count - 1;
        }

        _findCurrentIndex = next;
        var index = _findMatches[next];
        Editor.Select(index, FindBox.Text.Length);
        var line = Editor.Document.GetLineByOffset(index).LineNumber;
        Editor.ScrollToLine(line);
        RefreshFindTransformer();
        UpdateFindDisplay();
    }

    private void UpdateCursorInfo()
    {
        try
        {
            CursorInfoText.Text = $"行 {Editor.TextArea.Caret.Line} · 列 {Editor.TextArea.Caret.Column} · " +
                                  $"{Editor.Document.LineCount} 行";
        }
        catch
        {
            CursorInfoText.Text = string.Empty;
        }
    }

    private void OnNewFile(object sender, RoutedEventArgs e)
    {
        var directory = _vm.SelectedTab?.Directory ?? _initialDirectory;
        OpenBlank(directory);
    }

    private void OnRecentMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        if (_vm.RecentFiles.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "暂无最近文件", IsEnabled = false });
        }
        else
        {
            foreach (var path in _vm.RecentFiles)
            {
                var item = new MenuItem { Header = path, ToolTip = path };
                item.Click += (_, _) => _ = OpenRecentAsync(path);
                menu.Items.Add(item);
            }
        }
        if (sender is UIElement target)
            menu.PlacementTarget = target;
        menu.IsOpen = true;
    }

    private async Task OpenRecentAsync(string path)
    {
        var tab = await _vm.OpenRecentAsync(path);
        if (tab is not null)
        {
            _vm.SelectedTab = tab;
            UpdateEditorFromSelected();
        }
        else
        {
            _dialogs.Warn(_vm.StatusMessage, "最近文件");
        }
    }

    private void OnCompare(object sender, RoutedEventArgs e) => _ = CompareAsync();

    private async Task CompareAsync()
    {
        var tab = _vm.SelectedTab;
        if (tab is null)
            return;
        if (ReferenceEquals(tab, _vm.SelectedTab))
            tab.Content = Editor.Text;
        if (string.IsNullOrWhiteSpace(tab.RemotePath))
        {
            _dialogs.Warn("新文件还没有服务器版本，无法比较。", "文件比较");
            return;
        }

        var remote = await _vm.ReadRemoteVersionAsync(tab);
        if (!remote.Success)
        {
            _dialogs.Warn(remote.Message, "文件比较");
            return;
        }

        RemoteFileDiffWindow.Show(
            this,
            "文件差异 - " + tab.DisplayName,
            "编辑器当前内容",
            "服务器当前版本",
            tab.Content,
            remote.Content);
    }

    private void OnHistory(object sender, RoutedEventArgs e) => _ = OpenHistoryAsync();

    private async Task OpenHistoryAsync()
    {
        var tab = _vm.SelectedTab;
        if (tab is null)
            return;
        if (ReferenceEquals(tab, _vm.SelectedTab))
            tab.Content = Editor.Text;
        if (string.IsNullOrWhiteSpace(tab.RemotePath))
        {
            _dialogs.Warn("新文件还没有服务器版本备份。", "文件历史");
            return;
        }

        var history = new RemoteFileHistoryWindow(_vm, tab, tab.Content)
        {
            Owner = this
        };
        if (history.ShowDialog() == true)
        {
            await _vm.ReloadTabAsync(tab);
            UpdateEditorFromSelected();
        }
    }

    private void OnSave(object sender, RoutedEventArgs e) => _ = SaveCurrentAsync();

    private void OnSaveAll(object sender, RoutedEventArgs e) => _ = SaveAllAsync();

    private async Task<bool> SaveCurrentAsync()
    {
        var tab = _vm.SelectedTab;
        if (tab is null)
            return false;
        return await SaveTabAsync(tab);
    }

    private async Task<bool> SaveAllAsync()
    {
        if (_saveAllInProgress)
            return false;

        var targets = _vm.Tabs
            .Where(t => t.IsDirty || t.IsNew)
            .ToList();
        if (targets.Count == 0)
            return true;

        _saveAllInProgress = true;
        SaveAllProgressPanel.Visibility = Visibility.Visible;
        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                UpdateSaveAllProgress(i, targets.Count);
                if (!await SaveTabAsync(targets[i]))
                    return false;
            }
            UpdateSaveAllProgress(targets.Count, targets.Count);
            return true;
        }
        finally
        {
            _saveAllInProgress = false;
            SaveAllProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateSaveAllProgress(int done, int total)
    {
        SaveAllProgressText.Text = $"保存 {done}/{total}";
        SaveAllProgressBar.Value = total == 0 ? 0 : done * 100.0 / total;
    }

    private async Task CheckExternalChangeAsync()
    {
        var tab = _vm.SelectedTab;
        if (tab is null || tab.IsNew || tab.IsLoading || tab.IsSaving ||
            _saveAllInProgress || _closeInProgress)
            return;

        var snapshot = await _vm.GetSnapshotAsync(tab);
        if (snapshot is null)
            return;
        if (tab.Snapshot is null)
        {
            tab.Snapshot = snapshot;
            return;
        }
        if (snapshot.Size == tab.Snapshot.Size && snapshot.LastModified == tab.Snapshot.LastModified)
            return;

        var key = $"{tab.RemotePath}|{snapshot.Size}|{snapshot.LastModified:O}";
        if (string.Equals(_externalPromptKey, key, StringComparison.Ordinal))
            return;
        _externalPromptKey = key;

        if (tab.IsDirty)
        {
            tab.Status = "服务器文件已变化；保存时会提示冲突";
            return;
        }

        if (_dialogs.Confirm(
                $"服务器文件已变化：\n{tab.RemotePath}\n\n是否重新加载服务器版本？",
                "文件已变化", warning: true))
        {
            await _vm.ReloadTabAsync(tab);
            if (ReferenceEquals(tab, _vm.SelectedTab))
                UpdateEditorFromSelected();
        }
        else
        {
            tab.Status = "服务器文件已变化；保存时会提示冲突";
        }
    }

    private async Task<bool> SaveTabAsync(RemoteFileEditTab tab)
    {
        if (ReferenceEquals(tab, _vm.SelectedTab))
            tab.Content = Editor.Text;
        if (string.IsNullOrWhiteSpace(tab.RemotePath) && !AskNewFilePath(tab))
            return false;

        var result = await _vm.SaveTabAsync(tab, tab.Content);
        if (result.Conflict)
        {
            var remote = await _vm.ReadRemoteVersionAsync(tab);
            var decision = RemoteFileConflictWindow.Show(
                this, tab.RemotePath ?? string.Empty, tab.Content, remote.Success ? remote.Content : string.Empty);
            if (decision == RemoteFileConflictDecision.Cancel)
                return false;
            if (decision == RemoteFileConflictDecision.Reload)
            {
                await _vm.ReloadTabAsync(tab);
                UpdateEditorFromSelected();
                return false;
            }
            result = await _vm.SaveTabAsync(tab, tab.Content, force: true);
        }
        else if (result.AlreadyExists)
        {
            if (!_dialogs.Confirm(
                    $"目标文件已存在：\n{tab.RemotePath}\n\n是否强制覆盖保存？", "保存冲突"))
                return false;
            result = await _vm.SaveTabAsync(tab, tab.Content, force: true);
        }

        if (!result.Success)
        {
            _dialogs.Error(result.Message, "保存失败");
            return false;
        }

        PersistTabsState();
        return true;
    }

    private bool AskNewFilePath(RemoteFileEditTab tab)
    {
        var name = TextInputDialog.Prompt(this, "新建文件", "文件名", "untitled.txt");
        if (string.IsNullOrWhiteSpace(name))
            return false;
        name = name.Trim();
        if (!RemotePathGuard.TryFileName(name, out var safeName))
        {
            _dialogs.Warn("文件名不合法，不能包含路径分隔符或危险字符。", "新建文件");
            return false;
        }

        var directory = string.IsNullOrWhiteSpace(tab.Directory) ? _initialDirectory : tab.Directory;
        tab.RemotePath = JoinPath(directory, safeName);
        return true;
    }

    private void OnReload(object sender, RoutedEventArgs e) => _ = ReloadAsync();

    private async Task ReloadAsync()
    {
        var tab = _vm.SelectedTab;
        if (tab is null || string.IsNullOrWhiteSpace(tab.RemotePath))
            return;
        if (tab.IsDirty && !_dialogs.Confirm("放弃本地修改并重新从服务器加载？", "重新加载", warning: true))
            return;
        await _vm.ReloadTabAsync(tab);
        UpdateEditorFromSelected();
    }

    private void OnEncodingChanged(object sender, SelectionChangedEventArgs e)
        => _ = ChangeEncodingAsync();

    private async Task ChangeEncodingAsync()
    {
        if (_suppressEncoding || _vm.SelectedTab is not { } tab || EncodingBox.SelectedItem is not string name)
            return;
        var encoding = RemoteTextFileService.EncodingForName(name);
        if (await _vm.ChangeEncodingAsync(tab, encoding))
            UpdateEditorFromSelected();
    }

    private void OnLineEndingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEncoding || _vm.SelectedTab is not { } tab || LineEndingBox.SelectedItem is not string ending)
            return;
        tab.LineEnding = ending;
        tab.IsDirty = true;
        tab.Status = "换行符：" + ending;
    }

    private void OnWordWrapChanged(object sender, RoutedEventArgs e)
    {
        Editor.WordWrap = WordWrapBox.IsChecked == true;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (ctrl && e.Key == Key.S)
        {
            _ = shift ? SaveAllAsync() : SaveCurrentAsync();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.W)
        {
            if (_vm.SelectedTab is { } tab)
                _ = TryCloseTabAsync(tab);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.N)
        {
            OnNewFile(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.F3)
        {
            if (FindBar.Visibility != Visibility.Visible)
                ShowFindBar();
            FindNext(!shift);
            e.Handled = true;
        }
    }

    private void OnCloseTabClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RemoteFileEditTab tab)
            _ = TryCloseTabAsync(tab);
    }

    private RemoteFileEditTab? MenuTab(object sender)
        => (sender as FrameworkElement)?.DataContext as RemoteFileEditTab ?? _vm.SelectedTab;

    private void OnTabMenuClose(object sender, RoutedEventArgs e)
    {
        if (MenuTab(sender) is { } tab)
            _ = TryCloseTabAsync(tab);
    }

    private void OnTabMenuCloseOthers(object sender, RoutedEventArgs e)
    {
        var keep = MenuTab(sender);
        if (keep is null)
            return;
        var targets = _vm.Tabs.Where(t => !ReferenceEquals(t, keep) && !t.IsPinned).ToList();
        _ = CloseTabsAsync(targets);
    }

    private void OnTabMenuCloseRight(object sender, RoutedEventArgs e)
    {
        var tab = MenuTab(sender);
        if (tab is null)
            return;
        var index = _vm.Tabs.IndexOf(tab);
        if (index < 0)
            return;
        var targets = _vm.Tabs.Skip(index + 1).Where(t => !t.IsPinned).ToList();
        _ = CloseTabsAsync(targets);
    }

    private void OnTabMenuCopyPath(object sender, RoutedEventArgs e)
    {
        var tab = MenuTab(sender);
        if (tab is null || string.IsNullOrWhiteSpace(tab.RemotePath))
            return;
        try { Clipboard.SetText(tab.RemotePath); } catch { /* ignore */ }
        tab.Status = "已复制路径";
    }

    private void OnTabMenuPin(object sender, RoutedEventArgs e)
    {
        if (MenuTab(sender) is { } tab)
        {
            tab.IsPinned = !tab.IsPinned;
            PersistTabsState();
        }
    }

    private async Task CloseTabsAsync(IReadOnlyList<RemoteFileEditTab> tabs)
    {
        foreach (var tab in tabs)
        {
            if (!await TryCloseTabAsync(tab))
                return;
        }
    }

    private void OnTabStripPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
            return;
        var item = FindAncestor<TabItem>(e.OriginalSource as DependencyObject);
        _dragTab = item?.DataContext as RemoteFileEditTab;
        _dragStart = e.GetPosition(TabStrip);
    }

    private void OnTabStripPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragTab is null || e.LeftButton != MouseButtonState.Pressed)
            return;
        var point = e.GetPosition(TabStrip);
        if (Math.Abs(point.X - _dragStart.X) < 6 && Math.Abs(point.Y - _dragStart.Y) < 6)
            return;
        DragDrop.DoDragDrop(TabStrip, _dragTab, DragDropEffects.Move);
        _dragTab = null;
    }

    private void OnTabStripDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(RemoteFileEditTab))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTabStripDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(RemoteFileEditTab)) is not RemoteFileEditTab source)
            return;
        var target = FindAncestor<TabItem>(e.OriginalSource as DependencyObject)?.DataContext as RemoteFileEditTab;
        if (target is null || ReferenceEquals(source, target))
            return;

        var sourceIndex = _vm.Tabs.IndexOf(source);
        var targetIndex = _vm.Tabs.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0)
            return;
        _vm.Tabs.Move(sourceIndex, targetIndex);
        PersistTabsState();
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = current is Visual or Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private async Task<bool> TryCloseTabAsync(RemoteFileEditTab tab)
    {
        if (ReferenceEquals(tab, _vm.SelectedTab))
            tab.Content = Editor.Text;

        if (tab.IsDirty)
        {
            var answer = _dialogs.AskYesNoCancel(
                $"文件「{tab.DisplayName}」有未保存修改。\n\n是：保存后关闭\n否：放弃修改\n取消：返回编辑器",
                "关闭文件");
            if (answer == UserDialogAnswer.Cancel)
                return false;
            if (answer == UserDialogAnswer.Yes && !await SaveTabAsync(tab))
                return false;
        }

        _vm.RemoveTab(tab);
        _documents.Remove(tab);
        UpdateEditorFromSelected();
        PersistTabsState();
        if (_vm.Tabs.Count == 0 && !_closeInProgress)
        {
            // 等当前 Tab 容器完成重排后再关闭窗口，避免在 SelectionChanged 回调里直接 Close 被忽略。
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_vm.Tabs.Count != 0 || _closeInProgress)
                    return;
                _forceClose = true;
                Close();
            }));
        }
        return true;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_forceClose)
            return;
        if (_vm.Tabs.Count == 0)
        {
            _forceClose = true;
            return;
        }
        e.Cancel = true;
        if (!_closeInProgress)
            _ = CloseAllAsync();
    }

    private async Task CloseAllAsync()
    {
        if (_closeInProgress)
            return;
        _closeInProgress = true;
        try
        {
            foreach (var tab in _vm.Tabs.ToList())
            {
                if (!await TryCloseTabAsync(tab))
                    return;
            }
        }
        catch
        {
            // 关闭过程中即使 UI/远端检查异常，也不能留下没有 Tab 的空编辑器窗口。
        }
        finally
        {
            _closeInProgress = false;
        }

        if (_vm.Tabs.Count == 0)
        {
            _forceClose = true;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Close));
        }
    }

    private string TabsStateKey => "fileEditor.tabs." + _vm.Server.Id;

    private sealed class SavedTabState
    {
        public string Path { get; set; } = string.Empty;
        public bool Pinned { get; set; }
    }

    private sealed class SavedEditorState
    {
        public List<SavedTabState> Tabs { get; set; } = new();
        public string? ActivePath { get; set; }
    }

    private async Task RestoreTabsAsync()
    {
        if (_restoreStarted)
            return;
        _restoreStarted = true;
        _restoringTabs = true;
        try
        {
            var json = UiPrefs.GetString(TabsStateKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json))
                return;
            var state = JsonSerializer.Deserialize<SavedEditorState>(json);
            if (state?.Tabs is not { Count: > 0 })
                return;

            foreach (var saved in state.Tabs.Take(20))
            {
                if (string.IsNullOrWhiteSpace(saved.Path))
                    continue;
                var item = new RemoteFileItem
                {
                    Name = Path.GetFileName(saved.Path),
                    FullName = saved.Path,
                    Size = 0,
                    IsDirectory = false
                };
                var tab = await _vm.OpenExistingAsync(item);
                if (tab is null)
                    continue;
                tab.IsPinned = saved.Pinned;
            }

            if (!string.IsNullOrWhiteSpace(state.ActivePath))
            {
                var active = _vm.Tabs.FirstOrDefault(t =>
                    string.Equals(t.RemotePath, state.ActivePath, StringComparison.Ordinal));
                if (active is not null)
                    _vm.SelectedTab = active;
            }
            UpdateEditorFromSelected();
        }
        catch
        {
            // 损坏的恢复状态不应阻止编辑器使用
        }
        finally
        {
            _restoringTabs = false;
        }
    }

    private void PersistTabsState()
    {
        if (_restoringTabs)
            return;
        try
        {
            var state = new SavedEditorState
            {
                ActivePath = _vm.SelectedTab?.RemotePath,
                Tabs = _vm.Tabs
                    .Where(t => !string.IsNullOrWhiteSpace(t.RemotePath))
                    .Select(t => new SavedTabState { Path = t.RemotePath!, Pinned = t.IsPinned })
                    .ToList()
            };
            UiPrefs.SetString(TabsStateKey, JsonSerializer.Serialize(state));
        }
        catch
        {
            // 状态持久化失败不影响编辑
        }
    }

    private static string EncodingNameOf(Encoding encoding, bool hasBom)
        => encoding.CodePage switch
        {
            54936 => "gb18030",
            936 => "gbk",
            1200 => "utf-16 le",
            28591 => "latin1",
            _ => hasBom ? "utf-8 bom" : "utf-8"
        };

    private static string JoinPath(string directory, string name)
        => string.IsNullOrWhiteSpace(directory) || directory == "." 
            ? name 
            : directory.TrimEnd('/') + "/" + name;
}
