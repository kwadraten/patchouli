using Avalonia.Controls;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Views;

public partial class ConflictResolutionDialog : Window
{
    public ConflictResolutionDialog()
    {
        InitializeComponent();

        DataContextChanged += (s, e) =>
        {
            if (DataContext is ConflictResolutionDialogViewModel vm)
            {
                vm.RequestClose = (result) => Close(result);
            }
        };
    }
}
