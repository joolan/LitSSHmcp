using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Models;

namespace LitSSHmcp.App.Services.DatasourceAi;

/// <summary>数据源 AI 的硬作用域：一个会话只允许访问一个数据源与当前库。</summary>
public sealed record DatasourceAiScope(
    DataSourceConfig Datasource,
    string Database,
    string SessionKey,
    bool ReadOnly = true)
{
    public string Kind => Datasource.Type;
    public string DatasourceId => Datasource.Id;
    public DataSourceConfig Effective => DbWorkbenchViewModel.Effective(Datasource, Database);
}
