using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.Services.DatasourceAi;

namespace LitSSHmcp.App.Views;

public partial class DatasourceAiSessionManagerWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly DatasourceAiStore _store = new();
    private readonly IUserDialogService _dialogs = new UserDialogService();
    private readonly string _datasourceId;
    private readonly string _datasourceKind;
    private readonly string _database;
    private readonly long _currentSessionId;

    internal DatasourceAiSessionManagerWindow(
        string datasourceId, string datasourceKind, string database, long currentSessionId)
    {
        InitializeComponent();
        _datasourceId = datasourceId;
        _datasourceKind = datasourceKind;
        _database = database;
        _currentSessionId = currentSessionId;
        DataContext = this;
        ScopeText.Text = $"{datasourceId} · {datasourceKind} · {database}";
        Loaded += (_, _) => _ = LoadSessionsAsync();
    }

    public ObservableCollection<DatasourceAiSessionInfo> Sessions { get; } = new();
    public HashSet<long> DeletedSessionIds { get; } = new();
    public long? SelectedSessionId { get; private set; }

    private DatasourceAiSessionInfo? Selected => SessionList.SelectedItem as DatasourceAiSessionInfo;

    private async Task LoadSessionsAsync(long? selectId = null)
    {
        try
        {
            var sessions = await _store.ListSessionsAsync(
                _datasourceId, _datasourceKind, _database, limit: 200);
            Sessions.Clear();
            foreach (var session in sessions)
                Sessions.Add(session);

            var target = selectId is long id
                ? Sessions.FirstOrDefault(x => x.Id == id)
                : Sessions.FirstOrDefault(x => x.Id == _currentSessionId) ?? Sessions.FirstOrDefault();
            if (target is not null)
            {
                SessionList.SelectedItem = target;
                SessionList.ScrollIntoView(target);
            }
            else
            {
                TitleBox.Text = string.Empty;
                StatusText.Text = "当前还没有历史会话。";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "加载会话失败：" + ex.Message;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        TitleBox.Text = Selected?.Title ?? string.Empty;
    }

    private void OnListDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => OpenSelected();

    private void OnOpen(object sender, RoutedEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        var selected = Selected;
        if (selected is null)
        {
            StatusText.Text = "请先选择一个会话。";
            return;
        }
        SelectedSessionId = selected.Id;
        DialogResult = true;
        Close();
    }

    private void OnRename(object sender, RoutedEventArgs e) => _ = RenameAsync();

    private async Task RenameAsync()
    {
        var selected = Selected;
        if (selected is null)
        {
            StatusText.Text = "请先选择一个会话。";
            return;
        }
        var title = TitleBox.Text.Trim();
        if (title.Length == 0)
        {
            StatusText.Text = "会话名称不能为空。";
            return;
        }
        try
        {
            await _store.RenameSessionAsync(selected.Id, title);
            await LoadSessionsAsync(selected.Id);
            StatusText.Text = "已重命名。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "重命名失败：" + ex.Message;
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e) => _ = DeleteAsync();

    private async Task DeleteAsync()
    {
        var selected = Selected;
        if (selected is null)
        {
            StatusText.Text = "请先选择一个会话。";
            return;
        }
        if (!_dialogs.Confirm(
                $"确定删除会话「{selected.Title}」？\n\n执行记录仍保留在审计日志中。",
                "删除数据源 AI 会话"))
            return;
        try
        {
            await _store.DeleteSessionAsync(selected.Id);
            DeletedSessionIds.Add(selected.Id);
            await LoadSessionsAsync();
            StatusText.Text = "已删除。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "删除失败：" + ex.Message;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
