using Patchouli.Core.CitationInterop;
using Patchouli.Core.Ids;

namespace Patchouli.Host.CitationInterop;

/// <summary>One entry chosen in the desktop picker, before domain validation.
/// The citation key is intentionally not carried here: the interop service
/// re-resolves keys from the current library when the pick is confirmed, so a
/// stale picker can never emit a key that no longer belongs to the item.</summary>
public sealed record CitationBridgePickEntry(
    ItemId ItemId,
    string? Locator = null,
    string? Label = null,
    string? Prefix = null,
    string? Suffix = null,
    bool SuppressAuthor = false);

public enum CitationPickOutcome
{
    /// <summary>The user confirmed a pick (which may still be empty).</summary>
    Confirmed,

    /// <summary>The user cancelled or closed the picker: a normal empty result.</summary>
    Cancelled,

    /// <summary>No interactive UI exists in this host (headless).</summary>
    Unavailable
}

public sealed record CitationPickBridgeResult(
    CitationPickOutcome Outcome,
    IReadOnlyList<CitationBridgePickEntry> Entries)
{
    public static readonly CitationPickBridgeResult CancelledResult = new(CitationPickOutcome.Cancelled, []);
    public static readonly CitationPickBridgeResult UnavailableResult = new(CitationPickOutcome.Unavailable, []);
}

/// <summary>
/// The desktop interaction surface the citation interop service needs, defined
/// by the Host and implemented by the UI (issue #14 §2). The Host never
/// references Avalonia types; the UI never owns the HTTP listener or the
/// request gate. Every method is invoked by the interop host while it owns the
/// request lifecycle; implementations must honor the cancellation token (the
/// picker closes when it fires) and must not block an HTTP thread on UI work
/// without dispatching.
/// </summary>
public interface ICitationDesktopBridge
{
    /// <summary>True when a user-facing picker can be shown in this process.</summary>
    bool IsInteractiveAvailable { get; }

    /// <summary>Shows the citation picker and waits for the user's decision.
    /// Cancellation closes the picker and completes as
    /// <see cref="CitationPickOutcome.Cancelled"/> where possible.</summary>
    Task<CitationPickBridgeResult> PickCitationsAsync(CancellationToken cancellationToken);

    /// <summary>Returns an immutable snapshot of the library's currently selected
    /// item ids, in selection order, taken on the UI dispatcher.</summary>
    Task<IReadOnlyList<ItemId>> GetSelectedItemIdsAsync(CancellationToken cancellationToken);

    /// <summary>Writes text to the system clipboard (CAYW <c>clipboard</c> flag).</summary>
    Task SetClipboardTextAsync(string text, CancellationToken cancellationToken);

    /// <summary>Selects the given items in the library view (CAYW <c>select</c> flag).</summary>
    Task SelectItemsAsync(IReadOnlyList<ItemId> itemIds, CancellationToken cancellationToken);

    /// <summary>Minimizes the main window (CAYW <c>minimize</c> flag; only invoked
    /// after a result has been produced).</summary>
    Task MinimizeMainWindowAsync(CancellationToken cancellationToken);

    /// <summary>Restores and activates the main window, e.g. when a picker is
    /// requested while the window is minimized or in the tray.</summary>
    Task ActivateMainWindowAsync(CancellationToken cancellationToken);
}

/// <summary>The bridge used by hosts without a desktop UI: every interaction is
/// unavailable, and non-interactive work (JSON-RPC search/export) never touches it.</summary>
public sealed class UnavailableCitationDesktopBridge : ICitationDesktopBridge
{
    public static readonly UnavailableCitationDesktopBridge Instance = new();

    public bool IsInteractiveAvailable => false;

    public Task<CitationPickBridgeResult> PickCitationsAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(CitationPickBridgeResult.UnavailableResult);
    }

    public Task<IReadOnlyList<ItemId>> GetSelectedItemIdsAsync(CancellationToken cancellationToken)
    {
        throw new CitationUiUnavailableException();
    }

    public Task SetClipboardTextAsync(string text, CancellationToken cancellationToken)
    {
        throw new CitationUiUnavailableException();
    }

    public Task SelectItemsAsync(IReadOnlyList<ItemId> itemIds, CancellationToken cancellationToken)
    {
        throw new CitationUiUnavailableException();
    }

    public Task MinimizeMainWindowAsync(CancellationToken cancellationToken)
    {
        throw new CitationUiUnavailableException();
    }

    public Task ActivateMainWindowAsync(CancellationToken cancellationToken)
    {
        throw new CitationUiUnavailableException();
    }
}

/// <summary>Raised when a desktop-only CAYW side effect is requested from a host
/// without an interactive UI. The HTTP layer maps it to a stable
/// <c>ui_unavailable</c> error.</summary>
public sealed class CitationUiUnavailableException : InvalidOperationException
{
    public CitationUiUnavailableException()
        : base("No interactive desktop UI is available in this host.")
    {
    }
}
