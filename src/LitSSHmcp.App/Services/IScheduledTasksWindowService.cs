using LitSSHmcp.App.ViewModels;
using LitSSHmcp.App.Views;

namespace LitSSHmcp.App.Services;

public interface IScheduledTasksWindowService
{
    ScheduledTasksWindow CreateScheduledTasksWindow(ScheduledTasksViewModel viewModel);
}
