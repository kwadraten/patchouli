namespace Patchouli.UI.Services;

/// <summary>Routes library links inside Patchouli and web links through the desktop launcher.</summary>
internal sealed class MarkdownLinkNavigator(
    Func<string, Task> navigateLibrary,
    Func<Uri, Task<bool>> launchWeb)
{
    public Task<bool> NavigateAsync(Uri? uri)
    {
        if (uri is not { IsAbsoluteUri: true })
        {
            return Task.FromResult(false);
        }

        if (uri.Scheme.Equals("patchouli", StringComparison.OrdinalIgnoreCase))
        {
            return NavigateLibraryAsync(uri);
        }

        return uri.Scheme is "http" or "https" ? launchWeb(uri) : Task.FromResult(false);
    }

    private async Task<bool> NavigateLibraryAsync(Uri uri)
    {
        await navigateLibrary(uri.AbsoluteUri);
        return true;
    }
}
