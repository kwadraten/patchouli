using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Patchouli.Reading;

namespace Patchouli.UI.Reading;

/// <summary>
/// Whole-book reading media source. Media blocks in a streamed page are keyed <c>page:{index}</c>
/// (see <see cref="BookReadingStream"/>); this source renders each page's pixel buffer on demand
/// and keeps only the pages currently being read plus one neighbour on either side, so a long book
/// never holds its whole render set in memory. Stale or cancelled loads release their buffer
/// instead of caching it. Renders are in-memory only: no cropped image file is produced, and each
/// media block draws its normalized BBox sub-rect out of the page bitmap.
/// </summary>
public sealed class BookReadingImageSource : IReadingImageSource, IDisposable
{
    // The view requests only blocks near its viewport and calls ReleaseImage when they leave it.
    // Entries are never disposed while the view may still draw their bitmap.
    private readonly Dictionary<int, Bitmap> _cache = new();
    private readonly HashSet<int> _inFlight = [];
    private readonly HashSet<int> _failed = [];

    private readonly Func<int, CancellationToken, Task<Bitmap?>> _renderPage;
    private bool _disposed;

    /// <param name="renderPage">Renders one page index to an in-memory bitmap (or null when the
    /// page cannot be rendered). The source owns and disposes every bitmap it caches.</param>
    public BookReadingImageSource(Func<int, CancellationToken, Task<Bitmap?>> renderPage)
    {
        ArgumentNullException.ThrowIfNull(renderPage);
        _renderPage = renderPage;
    }

    /// <summary>Parses a media image key of the form <c>page:{index}</c> to its page index.</summary>
    public static bool TryGetPageIndex(string imageKey, out int pageIndex)
    {
        pageIndex = -1;
        if (string.IsNullOrEmpty(imageKey) || !imageKey.StartsWith("page:", StringComparison.Ordinal))
        {
            return false;
        }

        return int.TryParse(imageKey.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out pageIndex);
    }

    public async Task<IImage?> LoadImageAsync(string imageKey, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!TryGetPageIndex(imageKey, out int pageIndex))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_cache.TryGetValue(pageIndex, out Bitmap? cached))
        {
            return cached;
        }

        if (_failed.Contains(pageIndex) || !_inFlight.Add(pageIndex))
        {
            // A failed page is not retried within a session; an in-flight page returns no image
            // yet and the view re-requests after the load completes.
            return null;
        }

        Bitmap? rendered = null;
        try
        {
            rendered = await _renderPage(pageIndex, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A cancelled load releases whatever it produced and never caches it.
            (rendered as IDisposable)?.Dispose();
            throw;
        }
        finally
        {
            _inFlight.Remove(pageIndex);
        }

        if (rendered is null)
        {
            _failed.Add(pageIndex);
            return null;
        }

        if (_disposed)
        {
            rendered.Dispose();
            return null;
        }

        _cache[pageIndex] = rendered;
        return rendered;
    }

    public void ReleaseImage(string imageKey, IImage image)
    {
        if (!TryGetPageIndex(imageKey, out int pageIndex) ||
            !_cache.TryGetValue(pageIndex, out Bitmap? cached) ||
            !ReferenceEquals(cached, image))
        {
            return;
        }

        _cache.Remove(pageIndex);
        cached.Dispose();
    }

    /// <summary>Releases every cached page bitmap; safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (Bitmap bitmap in _cache.Values)
        {
            bitmap.Dispose();
        }

        _cache.Clear();
    }
}
