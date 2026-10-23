namespace LitSSHmcp.App.Services;

/// <summary>配置冲突后可由 App 统一触发“重新加载”的窗口数据源（未保存修改会丢失）。</summary>
public interface IConfigReloadable
{
    void ReloadConfig();
}
