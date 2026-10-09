using Avalonia.Controls;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Views;

public sealed partial class WorkflowEditorDialog : Window
{
    public WorkflowEditorDialog()
    {
        InitializeComponent();
        DialogCloseBinding.Bind<WorkflowEditorDialogViewModel, bool>(this,
            (viewModel, close) => viewModel.RequestClose = close);
        Opened += (_, _) =>
        {
            if (Screens.ScreenFromWindow(this) is { } screen)
            {
                double scaling = screen.Scaling > 0 ? screen.Scaling : 1;
                double maxWidth = screen.WorkingArea.Width / scaling - 40;
                double maxHeight = screen.WorkingArea.Height / scaling - 80;
                MinWidth = Math.Min(MinWidth, maxWidth);
                MinHeight = Math.Min(MinHeight, maxHeight);
                Width = Math.Min(Width, maxWidth);
                Height = Math.Min(Height, maxHeight);
            }
        };
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason is not WindowCloseReason.ApplicationShutdown and not WindowCloseReason.OSShutdown &&
            DataContext is WorkflowEditorDialogViewModel viewModel &&
            (viewModel.Editor.IsDirty || viewModel.Editor.IsSaving))
        {
            e.Cancel = true;
            viewModel.RequestCloseFromWindow();
        }

        base.OnClosing(e);
    }
}
