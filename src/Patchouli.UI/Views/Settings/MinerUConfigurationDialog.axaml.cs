using Avalonia.Controls;
using Avalonia.Interactivity;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.UI.Views.Settings;

/// <summary>
/// Real MinerU engine configuration dialog: model version, polling timeout and masked credential.
/// No FSX source pane — MinerU is built-in and has no extension package.
/// </summary>
public partial class MinerUConfigurationDialog : Window
{
    public MinerUConfigurationDialog()
    {
        InitializeComponent();
        Closing += (_, args) =>
        {
            if (DataContext is MinerUConfigurationDialogViewModel { IsBusy: true })
            {
                args.Cancel = true;
            }
        };
    }

    public MinerUConfigurationDialog(MinerUConfigurationDialogViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.RequestClose = result => Close(result);
    }

    private void CancelClicked(object? sender, RoutedEventArgs args)
    {
        Close(false);
    }
}
