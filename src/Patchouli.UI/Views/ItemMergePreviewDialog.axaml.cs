using Avalonia.Controls;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Views;

public partial class ItemMergePreviewDialog : Window
{
    public ItemMergePreviewDialog()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is ItemMergePreviewDialogViewModel vm)
            {
                vm.RequestClose = result => Close(result);
            }
        };
    }
}
