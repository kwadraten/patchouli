using Avalonia.Controls;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Views;

public partial class ItemMergePreviewDialog : Window
{
    public ItemMergePreviewDialog()
    {
        InitializeComponent();
        DialogCloseBinding.Bind<ItemMergePreviewDialogViewModel, ItemMergeDialogResult>(
            this, static (vm, close) => vm.RequestClose = close);
    }
}
