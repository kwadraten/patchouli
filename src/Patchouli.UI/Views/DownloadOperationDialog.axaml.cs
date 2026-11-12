using Avalonia.Controls;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Views;

public partial class DownloadOperationDialog : Window
{
    public DownloadOperationDialog()
    {
        InitializeComponent();
        DialogCloseBinding.Bind<DownloadOperationDialogViewModel, object?>(
            this, static (vm, close) => vm.RequestClose = close);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is DownloadOperationDialogViewModel vm && vm.IsRunning &&
            !vm.BackgroundRequested && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is DownloadOperationDialogViewModel vm)
        {
            vm.NotifyClosed();
        }

        base.OnClosed(e);
    }
}
