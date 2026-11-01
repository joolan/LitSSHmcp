using LitSSHmcp.App.Services;

namespace LitSSHmcp.App.Tests;

internal sealed class FakeDialogService : IUserDialogService
{
    public bool ConfirmResult { get; set; } = true;
    public bool Confirm(string message, string title, bool warning = true, bool defaultNo = true)
        => ConfirmResult;

    public UserDialogAnswer AskYesNoCancel(string message, string title, bool warning = true)
        => ConfirmResult ? UserDialogAnswer.Yes : UserDialogAnswer.No;

    public void Info(string message, string title)
    {
    }

    public void Warn(string message, string title)
    {
    }

    public void Error(string message, string title)
    {
    }
}
