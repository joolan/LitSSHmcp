using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.ViewModels;

public partial class MainViewModel
{
    private bool _isLoadingServers;
    public bool IsLoadingServers
    {
        get => _isLoadingServers;
        private set { _isLoadingServers = value; OnPropertyChanged(); }
    }

    private string _serversErrorMessage = string.Empty;
    public string ServersErrorMessage
    {
        get => _serversErrorMessage;
        private set
        {
            _serversErrorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasServersError));
        }
    }

    public bool HasServersError => ServersErrorMessage.Length > 0;

    private void NavigateHome()
    {
        CurrentPage = MainPage.Home;
        _ = RefreshHomeCountsAsync();
    }

    private string _serverSearchText = string.Empty;

    /// <summary>服务器管理页搜索关键字（名称/主机/账号/描述/分组/标签）。</summary>
    public string ServerSearchText
    {
        get => _serverSearchText;
        set
        {
            if (_serverSearchText == value) return;
            _serverSearchText = value;
            OnPropertyChanged();
            GroupedServers.Refresh();
        }
    }

    private bool FilterServer(object item)
    {
        if (item is not SshServerConfig server || string.IsNullOrWhiteSpace(ServerSearchText))
            return true;

        var keyword = ServerSearchText.Trim();
        return Contains(server.Name, keyword)
            || Contains(server.Host, keyword)
            || Contains(server.Username, keyword)
            || Contains(server.Description, keyword)
            || Contains(server.Group, keyword)
            || server.Tags.Any(tag => Contains(tag, keyword));

        static bool Contains(string? value, string keyword)
            => value?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true;
    }
}
