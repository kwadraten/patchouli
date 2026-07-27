using Avalonia.Controls;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();

        DataContextChanged += (s, e) =>
        {
            if (DataContext is ConfirmDialogViewModel vm)
            {
                vm.RequestClose = result => Close(result);
            }
        };
    }
}
