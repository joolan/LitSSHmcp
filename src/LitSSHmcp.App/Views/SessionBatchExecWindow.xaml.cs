using LitSSHmcp.App.Services;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class SessionBatchExecWindow : FluentWindow
{
    public SessionBatchExecWindow()
    {
        InitializeComponent();
        WindowLayout.Attach(this, "session-batch-exec");
    }
}
