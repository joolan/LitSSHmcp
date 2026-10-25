using System.Windows;
using LitSSHmcp.Agent;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Services.Storage;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class AgentWindow : FluentWindow
{
    private readonly AgentView _view;
    private readonly bool _ownsView;

    public AgentWindow(IConfigService configService, IContextStore store, string skillsDir,
        Func<string, LocalTerminalSessionViewModel?>? localTerminalResolver = null,
        Services.LocalTerminal.IAiTerminalBroker? localTerminalBroker = null,
        IAppServices? services = null)
        : this(new AgentView(configService, store, skillsDir, localTerminalResolver, localTerminalBroker, services), ownsView: true)
    {
    }

    public AgentWindow(AgentView view)
        : this(view, ownsView: false)
    {
    }

    private AgentWindow(AgentView view, bool ownsView)
    {
        InitializeComponent();
        _view = view;
        _ownsView = ownsView;
        WindowLayout.Attach(this, "agent");
        AgentHost.Content = view;
        view.SetDetached(!ownsView);
        Closed += OnAgentClosed;
    }

    public event EventHandler? DockRequested;

    public AgentViewModel ViewModel => _view.ViewModel;

    public void SubmitPrompt(string text, IReadOnlyList<string>? attachmentPaths = null)
        => _view.SubmitPrompt(text, attachmentPaths);

    private void OnAgentClosed(object? sender, EventArgs e)
    {
        Closed -= OnAgentClosed;
        AgentHost.Content = null;

        if (_ownsView)
        {
            AsyncOperation.Run("释放 AI 助手", () => _view.DisposeAsync());
            return;
        }

        DockRequested?.Invoke(this, EventArgs.Empty);
    }
}
