using System.Windows;
using System.Windows.Input;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Controls;
using LitSSHmcp.App.Services.Toolbox;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.App.Views;
using Wpf.Ui.Controls;
using AsyncOperation = LitSSHmcp.App.Services.AsyncOperation;

namespace LitSSHmcp.App.Views.Tools;

public partial class VaultTextEditorWindow : FluentWindow
{
    private readonly VaultEntryView _entry;
    private readonly VaultTextPayload _payload;
    private readonly Func<byte[], DateTimeOffset, Task<string?>> _save;
    private readonly IUserDialogService _dialogs = new UserDialogService();
    private DateTimeOffset _expectedModified;
    private readonly SearchHighlightRenderer _searchRenderer = new();
    private int _highlightIndex = -1;
    private int _highlightLength;
    private string _savedText = string.Empty;
    private string _baseTitle = string.Empty;

    public VaultTextEditorWindow(VaultEntryView entry, VaultTextPayload payload,
        Func<byte[], DateTimeOffset, Task<string?>> save)
    {
        InitializeComponent();
        _entry = entry;
        _payload = payload;
        _save = save;
        _expectedModified = entry.Model.ModifiedAt;

        _baseTitle = "文本编辑 - " + entry.Name;
        Title = _baseTitle;
        FileNameText.Text = entry.Name;
        EncodingText.Text = $"编码：{payload.Encoding.WebName}" +
                           (payload.Preamble.Length > 0 ? "（含 BOM）" : string.Empty) +
                           " · 仅内存打开";
        _savedText = payload.Text;
        Editor.Text = payload.Text;
        Editor.TextArea.TextView.BackgroundRenderers.Add(_searchRenderer);
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateCaretText();
        Editor.TextArea.SelectionChanged += (_, _) => UpdateCaretText();
        if (TryFindResource("AppAccentBrush") is System.Windows.Media.Brush selectionBrush)
            Editor.TextArea.SelectionBrush = selectionBrush;

        StatusText.Text = "尚未修改。";
        UpdateCaretText();
        Closing += OnWindowClosing;
        Closed += (_, _) => Editor.Text = string.Empty;
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (string.Equals(Editor.Text, _savedText, StringComparison.Ordinal))
            return;

        if (!_dialogs.Confirm("当前文本有未保存的修改，关闭后会丢失。\n\n确定关闭？",
                "关闭文本编辑", warning: true, defaultNo: true))
            e.Cancel = true;
    }

    private void OnEditorTextChanged(object sender, EventArgs e)
    {
        Title = string.Equals(Editor.Text, _savedText, StringComparison.Ordinal)
            ? _baseTitle
            : _baseTitle + " *";
        if (FindPanel.Visibility == Visibility.Visible)
        {
            UpdateFindCount();
            UpdateSearchHighlights();
        }
        else
        {
            HideHighlight();
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            ShowFind();
        }
        else if (e.Key == Key.G && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            GoToLine();
        }
        else if (e.Key == Key.F3)
        {
            e.Handled = true;
            FindStep(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
        }
        else if (e.Key == Key.Escape && FindPanel.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            HideFind();
        }
    }

    private void OnShowFind(object sender, RoutedEventArgs e) => ShowFind();

    private void OnCloseFind(object sender, RoutedEventArgs e) => HideFind();

    private void OnFindTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _highlightIndex = -1;
        _highlightLength = 0;
        UpdateFindCount();
        UpdateSearchHighlights();
    }

    private void OnFindOptionChanged(object sender, RoutedEventArgs e)
    {
        _highlightIndex = -1;
        _highlightLength = 0;
        UpdateFindCount();
        UpdateSearchHighlights();
    }

    private void OnFindKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            FindStep(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            HideFind();
        }
    }

    private void OnFindNext(object sender, RoutedEventArgs e) => FindStep(1);

    private void OnFindPrevious(object sender, RoutedEventArgs e) => FindStep(-1);

    private void ShowFind()
    {
        FindPanel.Visibility = Visibility.Visible;
        FindBox.Focus();
        FindBox.SelectAll();
        UpdateFindCount();
        UpdateSearchHighlights();
    }

    private void HideFind()
    {
        FindPanel.Visibility = Visibility.Collapsed;
        FindCountText.Text = string.Empty;
        HideHighlight();
        Editor.Focus();
    }

    private void FindStep(int direction)
    {
        var query = FindBox.Text;
        if (string.IsNullOrEmpty(query))
            return;

        var comparison = SearchComparison;
        var wholeWord = WholeWordCheck.IsChecked == true;
        var text = Editor.Text;
        if (text.Length == 0)
            return;

        var start = direction > 0
            ? Math.Min(text.Length, Editor.SelectionStart + Editor.SelectionLength)
            : Math.Max(0, Editor.SelectionStart - 1);
        var index = FindMatch(text, query, start, direction > 0, comparison, wholeWord);
        if (index < 0)
        {
            index = FindMatch(text, query, direction > 0 ? 0 : text.Length - 1,
                direction > 0, comparison, wholeWord);
        }

        if (index < 0)
        {
            FindCountText.Text = "未找到";
            return;
        }

        Editor.Select(index, query.Length);
        Editor.ScrollToLine(Editor.Document.GetLineByOffset(index).LineNumber);
        _highlightIndex = index;
        _highlightLength = query.Length;
        UpdateSearchHighlights();
        UpdateFindCount();
    }

    private static int FindMatch(string text, string query, int start, bool forward,
        StringComparison comparison, bool wholeWord)
    {
        if (text.Length < query.Length)
            return -1;

        if (forward)
        {
            var index = Math.Clamp(start, 0, text.Length);
            while (index <= text.Length - query.Length)
            {
                index = text.IndexOf(query, index, comparison);
                if (index < 0)
                    return -1;
                if (!wholeWord || IsWholeWord(text, index, query.Length))
                    return index;
                index++;
            }
            return -1;
        }

        var backward = Math.Min(Math.Max(0, start), text.Length - query.Length);
        while (backward >= 0)
        {
            var index = text.LastIndexOf(query, backward, comparison);
            if (index < 0)
                return -1;
            if (!wholeWord || IsWholeWord(text, index, query.Length))
                return index;
            backward = index - 1;
        }
        return -1;
    }

    private static bool IsWholeWord(string text, int index, int length)
    {
        if (index > 0 && IsWordChar(text[index - 1]))
            return false;
        var end = index + length;
        return end >= text.Length || !IsWordChar(text[end]);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private StringComparison SearchComparison =>
        CaseSensitiveCheck.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private void UpdateFindCount()
    {
        var query = FindBox.Text;
        if (string.IsNullOrEmpty(query))
        {
            FindCountText.Text = string.Empty;
            return;
        }

        var comparison = SearchComparison;
        var wholeWord = WholeWordCheck.IsChecked == true;
        var text = Editor.Text;
        var count = 0;
        var index = 0;
        while (index <= text.Length - query.Length)
        {
            index = text.IndexOf(query, index, comparison);
            if (index < 0)
                break;
            if (!wholeWord || IsWholeWord(text, index, query.Length))
                count++;
            index++;
        }
        FindCountText.Text = count == 0 ? "未找到" : $"{count} 个";
    }

    private void OnReplace(object sender, RoutedEventArgs e)
    {
        var query = FindBox.Text;
        if (string.IsNullOrEmpty(query))
            return;

        var wholeWord = WholeWordCheck.IsChecked == true;
        var start = Editor.SelectionStart;
        var selected = Editor.SelectionLength == query.Length &&
                       FindMatch(Editor.Text, query, start, true, SearchComparison, wholeWord) == start;
        if (selected)
        {
            var replacement = ReplaceBox.Text;
            Editor.SelectedText = replacement;
            Editor.Select(start, replacement.Length);
        }
        FindStep(1);
    }

    private void OnReplaceAll(object sender, RoutedEventArgs e)
    {
        var query = FindBox.Text;
        if (string.IsNullOrEmpty(query))
            return;

        var text = Editor.Text;
        var replacement = ReplaceBox.Text;
        var comparison = SearchComparison;
        var wholeWord = WholeWordCheck.IsChecked == true;
        var builder = new System.Text.StringBuilder(text.Length);
        var count = 0;
        var position = 0;

        while (position < text.Length)
        {
            var index = text.IndexOf(query, position, comparison);
            if (index < 0)
            {
                builder.Append(text, position, text.Length - position);
                break;
            }

            builder.Append(text, position, index - position);
            if (!wholeWord || IsWholeWord(text, index, query.Length))
            {
                builder.Append(replacement);
                count++;
                position = index + query.Length;
            }
            else
            {
                builder.Append(text[index]);
                position = index + 1;
            }
        }

        if (count > 0)
        {
            Editor.Text = builder.ToString();
            StatusText.Text = $"已替换 {count} 处。";
        }
        else
        {
            StatusText.Text = "没有可替换的匹配。";
        }
        UpdateFindCount();
    }

    private void OnGoToLine(object sender, RoutedEventArgs e) => GoToLine();

    private void GoToLine()
    {
        var value = TextInputDialog.Prompt(this, "转到行", "行号：", "1");
        if (!int.TryParse(value, out var line) || line < 1)
            return;

        var lineNumber = Math.Clamp(line, 1, Editor.Document.LineCount);
        var documentLine = Editor.Document.GetLineByNumber(lineNumber);
        Editor.Focus();
        Editor.Select(documentLine.Offset, 0);
        Editor.ScrollToLine(lineNumber);
        Editor.TextArea.Caret.Offset = documentLine.Offset;
        Editor.TextArea.Caret.BringCaretToView();
        UpdateCaretText();
    }

    private void UpdateCaretText()
    {
        CaretText.Text = $"行 {Editor.TextArea.Caret.Line}，列 {Editor.TextArea.Caret.Column}";
    }

    private void UpdateSearchHighlights()
    {
        var query = FindBox.Text;
        if (string.IsNullOrEmpty(query) || Editor.Text.Length == 0)
        {
            HideHighlight();
            return;
        }

        var comparison = SearchComparison;
        var wholeWord = WholeWordCheck.IsChecked == true;
        var text = Editor.Text;
        var matches = new List<(int Offset, int Length)>();
        var index = 0;
        while (index <= text.Length - query.Length && matches.Count < 1000)
        {
            index = text.IndexOf(query, index, comparison);
            if (index < 0)
                break;
            if (!wholeWord || IsWholeWord(text, index, query.Length))
                matches.Add((index, query.Length));
            index++;
        }

        if (_highlightIndex >= 0 && !matches.Any(m => m.Offset == _highlightIndex))
            matches.Add((_highlightIndex, _highlightLength));

        _searchRenderer.SetMatches(matches, _highlightIndex, _highlightLength);
        Editor.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Selection);
    }

    private void HideHighlight()
    {
        _highlightIndex = -1;
        _highlightLength = 0;
        _searchRenderer.Clear();
        Editor.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Selection);
    }

    private void OnSave(object sender, RoutedEventArgs e) =>
        AsyncOperation.Run("保存加密文本", SaveAsync);

    private async Task SaveAsync()
    {
        var content = VaultTextEncoding.Encode(Editor.Text, _payload);
        try
        {
            var error = await _save(content, _expectedModified);
            if (error is not null)
            {
                StatusText.Text = error;
                return;
            }

            _expectedModified = _entry.Model.ModifiedAt;
            _savedText = Editor.Text;
            Title = _baseTitle;
            StatusText.Text = $"已保存并重新加密（{DateTime.Now:HH:mm:ss}）。";
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(content);
        }
    }
}
