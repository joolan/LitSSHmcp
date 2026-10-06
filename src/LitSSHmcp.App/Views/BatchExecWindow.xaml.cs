using LitSSHmcp.App.Services;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class BatchExecWindow : FluentWindow
{
    public BatchExecWindow()
    {
        InitializeComponent();
        WindowLayout.Attach(this, "batch-exec");
    }
}
