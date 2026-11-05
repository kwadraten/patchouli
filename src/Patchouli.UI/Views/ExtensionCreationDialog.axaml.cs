using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.UI.Views;

public sealed partial class ExtensionCreationDialog : Window
{
    public ExtensionCreationDialog()
    {
        InitializeComponent();
        Closing += (_, args) => args.Cancel = DataContext is ExtensionCreationViewModel { IsBusy: true };
    }

    public ExtensionCreationDialog(ExtensionCreationViewModel model) : this()
    {
        DataContext = model;
        Opened += (_, _) => model.LoadAsync().Observe(nameof(ExtensionCreationDialog), "load-templates");
    }

    private void CancelClicked(object? sender, RoutedEventArgs args)
    {
        Close(null);
    }

    private void CreateClicked(object? sender, RoutedEventArgs args)
    {
        CreateAsync().Observe(nameof(ExtensionCreationDialog), "create-extension");
    }

    private async Task CreateAsync()
    {
        if (DataContext is ExtensionCreationViewModel model && await model.CreateAsync() is { } id)
        {
            Close(id);
        }
    }

    public static async Task ShowAsync(Window owner, ExtensionCreationViewModel model,
        SettingsViewModel settings)
    {
        string? id = await new ExtensionCreationDialog(model).ShowDialog<string?>(owner);
        if (id is null)
        {
            return;
        }

        using ExtensionSettingsViewModel editor = settings.CreateExtensionEditor();
        await editor.OpenAsync(id, true);
        await new ExtensionConfigurationDialog(editor).ShowDialog(owner);
    }
}
