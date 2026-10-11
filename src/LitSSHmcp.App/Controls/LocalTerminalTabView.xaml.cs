using System.Windows.Controls;

namespace LitSSHmcp.App.Controls;

/// <summary>本机 PowerShell 终端标签内容（ConPTY）。</summary>
public partial class LocalTerminalTabView : UserControl
{
    public LocalTerminalTabView()
    {
        InitializeComponent();
    }

    /// <summary>聚焦终端输入（切换标签后调用）。</summary>
    public void FocusInput() => Terminal.FocusInput();
}
