using Avalonia.Controls;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Views;

public partial class InfoDialog : Window
{
    public InfoDialog()
    {
        InitializeComponent();
        DialogCloseBinding.Bind<InfoDialogViewModel, bool>(
            this, static (vm, close) => vm.RequestClose = close);
    }
}
