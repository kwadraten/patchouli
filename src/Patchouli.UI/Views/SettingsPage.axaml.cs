using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using System.Linq;
using System.Threading.Tasks;
using Patchouli.Extensions.Contracts;
using Patchouli.UI.Controls;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.UI.Views;

public sealed partial class SettingsPage : UserControl
{
    private SettingsViewModel? _settings;
    private ISettingsAnchor? _highlightedCard;
    private DispatcherTimer? _highlightTimer;

    public SettingsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            SubscribeToSettings();
            EnableAutoSaveForDataContext();
        };
        AttachedToVisualTree += (_, _) =>
        {
            SubscribeToSettings();
            EnableAutoSaveForDataContext();
        };
        DetachedFromVisualTree += (_, _) => UnsubscribeFromSettings();
    }

    private void SubscribeToSettings()
    {
        SettingsViewModel? next = DataContext as SettingsViewModel;
        if (ReferenceEquals(_settings, next))
        {
            return;
        }

        UnsubscribeFromSettings();
        _settings = next;
        if (_settings is not null)
        {
            _settings.CardNavigationRequested += OnCardNavigationRequested;
        }
    }

    private void UnsubscribeFromSettings()
    {
        _highlightTimer?.Stop();
        _highlightTimer = null;
        if (_highlightedCard is not null)
        {
            _highlightedCard.IsHighlighted = false;
            _highlightedCard = null;
        }

        if (_settings is not null)
        {
            _settings.CardNavigationRequested -= OnCardNavigationRequested;
        }

        _settings = null;
    }

    private void OnCardNavigationRequested(object? sender, SettingsCardNavigationEventArgs args)
    {
        Dispatcher.UIThread.Post(() => HighlightCard(args.AnchorId), DispatcherPriority.Background);
    }

    private void HighlightCard(string anchorId)
    {
        ISettingsAnchor? card = this.GetVisualDescendants().OfType<Control>()
            .OfType<ISettingsAnchor>()
            .FirstOrDefault(candidate => string.Equals(candidate.AnchorId, anchorId, StringComparison.Ordinal));
        if (card is not Control control)
        {
            return;
        }

        _highlightTimer?.Stop();
        if (_highlightedCard is not null && !ReferenceEquals(_highlightedCard, card))
        {
            _highlightedCard.IsHighlighted = false;
        }

        _highlightedCard = card;
        card.IsHighlighted = true;
        control.BringIntoView();
        _highlightTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        _highlightTimer.Tick += (_, _) =>
        {
            _highlightTimer?.Stop();
            _highlightTimer = null;
            card.IsHighlighted = false;
        };
        _highlightTimer.Start();
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

    private void ExtensionSourceClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id } && DataContext is SettingsViewModel settings &&
            TopLevel.GetTopLevel(this) is Window owner)
        {
            OpenExtensionAsync(settings, id, owner, true).Observe(nameof(SettingsPage), "extension-source");
        }
    }

    private static async Task OpenExtensionAsync(SettingsViewModel settings, string id, Window owner,
        bool openSource = false)
    {
        using ExtensionSettingsViewModel editor = settings.CreateExtensionEditor();
        await editor.OpenAsync(id, openSource);
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
