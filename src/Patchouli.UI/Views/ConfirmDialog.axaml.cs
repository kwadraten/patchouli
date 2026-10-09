using Avalonia.Controls;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
        DialogCloseBinding.Bind<ConfirmDialogViewModel, ConfirmDialogResult>(
            this, static (vm, close) => vm.RequestClose = close);
    }
}
