using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using System.Threading.Tasks;
using Patchouli.Extensions.Contracts;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.UI.Views;

public sealed partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => EnableAutoSaveForDataContext();
        AttachedToVisualTree += (_, _) => EnableAutoSaveForDataContext();
    }

    // The auto-save pipeline is activated by the view so that view-less hosts (tests) keep the
    // manual save/discard semantics; enabling is idempotent and lives as long as the view model.
    private void EnableAutoSaveForDataContext()
    {
        (DataContext as SettingsViewModel)?.EnableAutoSave();
    }

    private void ConfigureExtensionClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id } && DataContext is SettingsViewModel settings &&
            TopLevel.GetTopLevel(this) is Window owner)
        {
            OpenExtensionAsync(settings, id, owner).Observe(nameof(SettingsPage), "configure-extension");
        }
    }

    private static async Task OpenExtensionAsync(SettingsViewModel settings, string id, Window owner)
    {
        using ExtensionSettingsViewModel editor = settings.CreateExtensionEditor();
        await editor.OpenAsync(id);
        ExtensionConfigurationDialog dialog = new(editor);
        await dialog.ShowDialog(owner);
    }

    private void AddExtensionClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string kindName } && Enum.TryParse(kindName, out ExtensionKind kind) &&
            DataContext is SettingsViewModel settings && TopLevel.GetTopLevel(this) is Window owner)
        {
            AddExtensionAsync(settings, kind, owner).Observe(nameof(SettingsPage), "add-extension");
        }
    }

    private static async Task AddExtensionAsync(SettingsViewModel settings, ExtensionKind kind, Window owner)
    {
        await ExtensionCreationDialog.ShowAsync(owner, settings.CreateExtensionDraft(kind), settings);
        if (kind == ExtensionKind.Translation)
        {
            await settings.LlmSettings.RefreshTranslationEnginesCommand.ExecuteAsync();
        }
        else if (kind == ExtensionKind.Metadata)
        {
            await settings.MetadataLookupSettings.RefreshExtensionsAsync();
        }
    }
}
