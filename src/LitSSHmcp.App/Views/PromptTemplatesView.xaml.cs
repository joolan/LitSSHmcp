using System.Windows.Controls;
using LitSSHmcp.App.ViewModels;

namespace LitSSHmcp.App.Views;

public partial class PromptTemplatesView : UserControl
{
    public PromptTemplatesView()
    {
        InitializeComponent();
        DataContext = new PromptTemplatesViewModel();
    }
}
