using LitSSHmcp.App.ViewModels;
using LitSSHmcp.App.Views;

namespace LitSSHmcp.App.Services;

public sealed partial class WpfWindowService
{
    public FtpManagerWindow CreateFtpManagerWindow(FtpManagerViewModel viewModel) =>
        new(viewModel) { Topmost = false };
}
