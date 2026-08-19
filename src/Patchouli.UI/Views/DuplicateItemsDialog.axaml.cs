using Avalonia.Controls;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Views;

public partial class DuplicateItemsDialog : Window
{
    public DuplicateItemsDialog()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is DuplicateItemsDialogViewModel vm)
            {
                vm.RequestClose = _ => Close();
            }
        };
    }
}
