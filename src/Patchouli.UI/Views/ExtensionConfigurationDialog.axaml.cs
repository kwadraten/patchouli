using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.UI.Views;

public sealed partial class ExtensionConfigurationDialog : Window
{
    private double _formWidth = 560;
    private PixelPoint _formPosition;

    public ExtensionConfigurationDialog()
    {
        InitializeComponent();
        Closing += (_, args) =>
        {
            if (DataContext is ExtensionSettingsViewModel { IsEditorBusy: true })
            {
                args.Cancel = true;
                return;
            }

            (DataContext as ExtensionSettingsViewModel)?.CancelPendingLoad();
        };
    }

    public ExtensionConfigurationDialog(ExtensionSettingsViewModel editor) : this()
    {
        DataContext = editor;
        Opened += (_, _) =>
        {
            // Component catalogs can request the FSX source pane directly (custom, never-activated
            // or invalid-source packages); the same toggle still controls it afterwards.
            if (editor.OpenWithSourceExpanded && !IsSourceVisible)
            {
                ToggleSource();
            }

            editor.LoadAsync().Observe(nameof(ExtensionConfigurationDialog), "load-extension-dialog");
        };
        Closed += (_, _) => editor.CancelPendingLoad();
    }

    public bool IsSourceVisible => SourcePanel.IsVisible;

    public void ToggleSource()
    {
        if (SourcePanel.IsVisible)
        {
            SourcePanel.IsVisible = false;
            EditorColumns.ColumnDefinitions[1].Width = new GridLength(0);
            EditorColumns.ColumnSpacing = 0;
            MinWidth = 480;
            Width = _formWidth;
            Position = _formPosition;
            ToggleSourceButton.Content = "编辑扩展源码 >>";
        }
        else
        {
            _formWidth = Width;
            _formPosition = Position;
            double availableWidth = Screens.ScreenFromWindow(this)?.WorkingArea.Width / RenderScaling - 48 ?? 1160;
            Width = Math.Min(1160, Math.Max(760, availableWidth));
            if (Screens.ScreenFromWindow(this) is { } screen)
            {
                int left = Math.Max(screen.WorkingArea.X,
                    Math.Min(Position.X, screen.WorkingArea.Right - (int)(Width * RenderScaling)));
                Position = new PixelPoint(left, Position.Y);
            }

            MinWidth = 760;
            EditorColumns.ColumnDefinitions[1].Width = new GridLength(1.3, GridUnitType.Star);
            EditorColumns.ColumnSpacing = 20;
            SourcePanel.IsVisible = true;
            ToggleSourceButton.Content = "隐藏源码 <<";
        }
    }

    private void ToggleSourceClicked(object? sender, RoutedEventArgs args)
    {
        ToggleSource();
    }

    private void DiscardClicked(object? sender, RoutedEventArgs args)
    {
        Close(false);
    }

    private void SaveClicked(object? sender, RoutedEventArgs args)
    {
        SaveAndCloseAsync().Observe(nameof(ExtensionConfigurationDialog), "save-extension");
    }

    private async Task SaveAndCloseAsync()
    {
        if (DataContext is ExtensionSettingsViewModel editor && await editor.SaveDialogAsync())
        {
            Close(true);
        }
    }
}
