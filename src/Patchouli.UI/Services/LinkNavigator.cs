namespace Patchouli.UI.Services;

/// <summary>
/// Issue #21: the application's shared link navigation service. Callers hand
/// it the raw URL string (as found in Markdown or user input); it parses the
/// address, routes patchouli:// links to in-app navigation, and gates external
/// http(s) links behind an explicit confirmation before the desktop launcher
/// opens them. Every UI-facing step is an injected callback, so the service
/// has no Avalonia window dependency and stays unit-testable.
/// </summary>
public sealed class LinkNavigator(
    Func<string, Task> navigateInternal,
    Func<Uri, Task<bool>> confirmExternal,
    Func<Uri, Task<bool>> openExternal,
    Func<Uri, Task> reportOpenFailure,
    Func<string, Task> reportRejected)
{
    private int _activationInProgress;

    /// <summary>
    /// Navigates to <paramref name="url"/>. Returns false when the address is
    /// invalid or uses an unsupported scheme, when the user declines the
    /// external-link confirmation, when the launcher fails, or when a previous
    /// activation is still being processed (a single activation must never
    /// prompt or navigate twice).
    /// </summary>
    public async Task<bool> NavigateAsync(string? url)
    {
        if (Interlocked.CompareExchange(ref _activationInProgress, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                await reportRejected(url ?? string.Empty);
                return false;
            }

            if (uri.Scheme.Equals("patchouli", StringComparison.OrdinalIgnoreCase))
            {
                await navigateInternal(uri.AbsoluteUri);
                return true;
            }

            if (uri.Scheme is not ("http" or "https"))
            {
                await reportRejected(uri.AbsoluteUri);
                return false;
            }

            return await OpenExternalAsync(uri);
        }
        finally
        {
            Volatile.Write(ref _activationInProgress, 0);
        }
    }

    private async Task<bool> OpenExternalAsync(Uri uri)
    {
        if (!await confirmExternal(uri))
        {
            return false;
        }

        bool opened;
        try
        {
            opened = await openExternal(uri);
        }
        catch (Exception)
        {
            opened = false;
        }

        if (!opened)
        {
            await reportOpenFailure(uri);
            return false;
        }

        return true;
    }
}
