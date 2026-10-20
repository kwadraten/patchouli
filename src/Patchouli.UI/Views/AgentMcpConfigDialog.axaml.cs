using Avalonia.Controls;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Views;

public partial class AgentMcpConfigDialog : Window
{
    public AgentMcpConfigDialog()
    {
        InitializeComponent();
        DialogCloseBinding.Bind<AgentMcpConfigDialogViewModel, bool>(
            this, static (viewModel, close) => viewModel.RequestClose = close);
    }
}
