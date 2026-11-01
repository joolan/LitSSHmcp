using LitSSHmcp.App.ViewModels;
using LitSSHmcp.App.Views;

namespace LitSSHmcp.App.Services;

public sealed partial class WpfWindowService : IScheduledTasksWindowService
{
    public ScheduledTasksWindow CreateScheduledTasksWindow(ScheduledTasksViewModel viewModel) =>
        new(viewModel) { Topmost = false };
}
