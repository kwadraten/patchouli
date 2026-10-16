using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Linq;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Views;

public sealed partial class WorkflowEditorDialog : Window
{
    private WorkflowEditorDialogViewModel? _viewModel;

    public WorkflowEditorDialog()
    {
        InitializeComponent();
        DialogCloseBinding.Bind<WorkflowEditorDialogViewModel, bool>(this,
            (viewModel, close) => viewModel.RequestClose = close);
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.FocusFieldRequested -= FocusField;
            }

            _viewModel = DataContext as WorkflowEditorDialogViewModel;
            if (_viewModel is not null)
            {
                _viewModel.FocusFieldRequested += FocusField;
            }
        };
        Opened += (_, _) =>
        {
            _viewModel?.FocusPendingTarget();
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

    private void FocusField(string key)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (key == "script")
            {
                ScriptEditor.BringIntoView();
                ScriptEditor.Focus();
                if (_viewModel?.PendingSourceLine is { } requestedLine)
                {
                    int line = Math.Clamp(requestedLine, 1, ScriptEditor.Document.LineCount);
                    int column = Math.Clamp(_viewModel.PendingSourceColumn ?? 1, 1,
                        ScriptEditor.Document.GetLineByNumber(line).Length + 1);
                    ScriptEditor.ScrollTo(line, column);
                    ScriptEditor.CaretOffset = ScriptEditor.Document.GetOffset(line, column);
                }

                return;
            }

            Control? matching = this.GetVisualDescendants().OfType<Control>()
                .FirstOrDefault(control => control.Tag?.ToString() == key &&
                                           control.IsVisible && control.IsEnabled &&
                                           control is TextBox or ComboBox or CheckBox);
            matching ??= this.GetVisualDescendants().OfType<Control>()
                .FirstOrDefault(control => control.Tag?.ToString() == key && control.IsVisible && control.IsEnabled);
            matching?.BringIntoView();
            matching?.Focus();
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.FocusFieldRequested -= FocusField;
            _viewModel = null;
        }

        base.OnClosed(e);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason is not WindowCloseReason.ApplicationShutdown and not WindowCloseReason.OSShutdown &&
            DataContext is WorkflowEditorDialogViewModel viewModel &&
            (viewModel.Editor.IsDirty || viewModel.Editor.IsConfigurationDirty || viewModel.Editor.IsSaving))
        {
            e.Cancel = true;
            viewModel.RequestCloseFromWindow();
        }

        base.OnClosing(e);
    }
}
