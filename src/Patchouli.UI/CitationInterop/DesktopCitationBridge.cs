using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Host.CitationInterop;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Citations;
using Patchouli.UI.Views;

namespace Patchouli.UI.CitationInterop;

/// <summary>
/// Desktop implementation of <see cref="ICitationDesktopBridge"/> (issue #14
/// §2/§4): the Host owns the HTTP lifecycle and the request gate; this class
/// only marshals interactions onto the Avalonia UI thread — picker window,
/// selection snapshot, clipboard, library selection, and main-window
/// minimize/restore. Cancellation closes the picker, which the handler then
/// reports as a normal empty pick.
/// </summary>
public sealed class DesktopCitationBridge : ICitationDesktopBridge
{
    private readonly MainWindowViewModel _main;
    private readonly ILibraryItemQueryService _query;

    public DesktopCitationBridge(MainWindowViewModel main, ILibraryItemQueryService query)
    {
        _main = main;
        _query = query;
    }

    public bool IsInteractiveAvailable => GetMainWindow() is not null;

    public Task<CitationPickBridgeResult> PickCitationsAsync(CancellationToken cancellationToken)
    {
        Window? owner = GetMainWindow();
        if (owner is null)
        {
            return Task.FromResult(CitationPickBridgeResult.UnavailableResult);
        }

        TaskCompletionSource<CitationPickBridgeResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
            _ = ShowPickerAsync(owner, completion, cancellationToken));
        return completion.Task;
    }

    private async Task ShowPickerAsync(
        Window owner,
        TaskCompletionSource<CitationPickBridgeResult> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            CitationPickerViewModel viewModel = new(_query);
            CitationPickerWindow window = new() { DataContext = viewModel };
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
                Dispatcher.UIThread.Post(() => window.Close()));
            CitationPickBridgeResult? result =
                await window.ShowDialog<CitationPickBridgeResult?>(owner);
            completion.TrySetResult(result ?? CitationPickBridgeResult.CancelledResult);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    public async Task<IReadOnlyList<ItemId>> GetSelectedItemIdsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Dispatcher.UIThread.InvokeAsync(() => (IReadOnlyList<ItemId>)_main.Shell.SelectedItems
            .Select(static item => ItemId.Parse(item.ItemId))
            .ToArray());
    }

    public async Task SetClipboardTextAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Dispatcher.UIThread.InvokeAsync(() => _main.Clipboard.SetTextAsync(text));
    }

    public async Task SelectItemsAsync(IReadOnlyList<ItemId> itemIds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Dispatcher.UIThread.InvokeAsync(() => _main.Shell.SelectItemsByIds(itemIds));
    }

    public Task MinimizeMainWindowAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Dispatcher.UIThread.Post(() =>
        {
            if (GetMainWindow() is { } window)
            {
                window.WindowState = WindowState.Minimized;
            }
        });
        return Task.CompletedTask;
    }

    public Task ActivateMainWindowAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Dispatcher.UIThread.Post(() =>
        {
            if (GetMainWindow() is { } window)
            {
                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }

                window.Show();
                window.Activate();
            }
        });
        return Task.CompletedTask;
    }

    private static Window? GetMainWindow()
    {
        return Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
    }
}
