using Avalonia.Controls;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Views;

public partial class TaskQueueErrorsDialog : Window
{
    public TaskQueueErrorsDialog()
    {
        InitializeComponent();
        DialogCloseBinding.Bind<TaskQueueErrorsDialogViewModel, bool>(
            this, static (vm, close) => vm.RequestClose = close);
    }
}
