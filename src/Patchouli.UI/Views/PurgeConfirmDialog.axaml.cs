using Avalonia.Controls;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Views;

public partial class PurgeConfirmDialog : Window
{
    public PurgeConfirmDialog()
    {
        InitializeComponent();
        DialogCloseBinding.Bind<PurgeConfirmDialogViewModel, bool?>(
            this, static (vm, close) => vm.RequestClose = close);
    }
}
