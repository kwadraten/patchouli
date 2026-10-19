using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Patchouli.UI.ViewModels.Translation;

namespace Patchouli.UI.Views;

/// <summary>
/// Modeless window that shows one translation run. Closing it stops polling but never cancels the
/// host-side run, so a user can reopen progress only through a new run report.
/// </summary>
public partial class TranslationProgressWindow : Window
{
    private TranslationProgressViewModel? _viewModel;

    public TranslationProgressWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachViewModel();
        Closed += (_, _) => DetachViewModel();
    }

    /// <summary>Shows the run modelessly over the main window; the run keeps going after close.</summary>
    public static void ShowModeless(TranslationProgressViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            viewModel.Dispose();
            return;
        }

        TranslationProgressWindow window = new() { DataContext = viewModel };
        if (desktop.MainWindow is { } owner)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }

    private void AttachViewModel()
    {
        DetachViewModel();
        if (DataContext is TranslationProgressViewModel viewModel)
        {
            _viewModel = viewModel;
            viewModel.RequestClose = Close;
        }
    }

    private void DetachViewModel()
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.RequestClose = null;
        _viewModel.Dispose();
        _viewModel = null;
    }
}
