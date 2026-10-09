using Avalonia.Controls;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Views;

public partial class DuplicateItemsDialog : Window
{
    public DuplicateItemsDialog()
    {
        InitializeComponent();
        DialogCloseBinding.Bind<DuplicateItemsDialogViewModel, DuplicateItemsDialogResult>(
            this, static (vm, close) => vm.RequestClose = close);
    }
}
