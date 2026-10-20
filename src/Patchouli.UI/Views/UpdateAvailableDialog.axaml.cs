using Avalonia.Controls;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Views;

public partial class UpdateAvailableDialog : Window
{
    public UpdateAvailableDialog()
    {
        InitializeComponent();
        DialogCloseBinding.Bind<UpdateAvailableDialogViewModel, bool>(
            this, static (vm, close) => vm.RequestClose = close);
    }
}
