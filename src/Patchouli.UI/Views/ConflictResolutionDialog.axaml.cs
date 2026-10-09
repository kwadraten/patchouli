using Avalonia.Controls;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Views;

public partial class ConflictResolutionDialog : Window
{
    public ConflictResolutionDialog()
    {
        InitializeComponent();
        DialogCloseBinding.Bind<ConflictResolutionDialogViewModel, object?>(
            this, static (vm, close) => vm.RequestClose = close);
    }
}
