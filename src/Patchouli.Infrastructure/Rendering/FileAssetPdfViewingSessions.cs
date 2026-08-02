using Patchouli.Core.Ids;
using Patchouli.Ocr;

namespace Patchouli.Infrastructure.Rendering;

/// <summary>
/// Owns the reusable PDFium document handles used for interactive viewing. A binding belongs
/// to a <see cref="FileAssetId"/>, rather than to a document instance or an original path: a
/// source rebind (or a newly validated source fingerprint) retires the old handle before the
/// next render can use it. The underlying cache still deduplicates identical physical paths.
/// </summary>
public sealed class FileAssetPdfViewingSessions : IAsyncDisposable
{
    private readonly PdfiumSessionCache _sessions;
    private readonly object _sync = new();
    private readonly Dictionary<FileAssetId, Binding> _bindings = [];

    public FileAssetPdfViewingSessions(IPdfPageSessionRenderer renderer, PdfiumSessionCache? sessions = null)
    {
        _sessions = sessions ?? new PdfiumSessionCache(renderer.OpenSessionAsync);
    }

    public int OpenDocumentSessions => _sessions.Count;

    /// <summary>
    /// Renders through the session currently bound to <paramref name="fileAssetId"/>. Awaiter
    /// cancellation only cancels that caller; it never tears down a shared PDFium open/render.
    /// </summary>
    public async Task<PdfPagePixelBufferOutput> RenderAsync(FileAssetId fileAssetId, string resolvedPath,
        string sourceVersion, int pageIndex, int dpi, CancellationToken cancellationToken = default)
    {
        string normalizedPath = Path.GetFullPath(resolvedPath);
        long bindingGeneration = await BindAsync(fileAssetId, normalizedPath, sourceVersion);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IPdfPageSession session = await _sessions.GetOrOpenAsync(normalizedPath, cancellationToken);
            try
            {
                if (!IsCurrent(fileAssetId, normalizedPath, sourceVersion, bindingGeneration))
                {
                    // A rebind completed while this caller was waiting for a shared open. Return
                    // the stale handle and retry against the current binding instead of rendering
                    // from the previous file.
                    bindingGeneration = await BindAsync(fileAssetId, normalizedPath, sourceVersion);
                    continue;
                }

                return await session.RenderPageAsync(pageIndex, dpi, cancellationToken);
            }
            finally
            {
                _sessions.Return(session);
            }
        }
    }

    /// <summary>Forgets a FileAsset binding and lazily closes its currently open handle.</summary>
    public async Task InvalidateAsync(FileAssetId fileAssetId)
    {
        string? path;
        lock (_sync)
        {
            path = _bindings.Remove(fileAssetId, out Binding? binding) ? binding.Path : null;
        }

        if (path is not null)
        {
            await _sessions.EvictPathAsync(path);
        }
    }

    public Task ReleaseAsync(FileAssetId fileAssetId)
    {
        return InvalidateAsync(fileAssetId);
    }

    public ValueTask DisposeAsync()
    {
        return _sessions.DisposeAsync();
    }

    private async Task<long> BindAsync(FileAssetId fileAssetId, string path, string sourceVersion)
    {
        string? retiredPath = null;
        long generation;
        lock (_sync)
        {
            if (_bindings.TryGetValue(fileAssetId, out Binding? existing) &&
                string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.SourceVersion, sourceVersion, StringComparison.Ordinal))
            {
                return existing.Generation;
            }

            retiredPath = existing?.Path;
            generation = existing is null ? 1 : existing.Generation + 1;
            _bindings[fileAssetId] = new Binding(path, sourceVersion, generation);
        }

        if (retiredPath is not null)
        {
            await _sessions.EvictPathAsync(retiredPath);
        }

        return generation;
    }

    private bool IsCurrent(FileAssetId fileAssetId, string path, string sourceVersion, long generation)
    {
        lock (_sync)
        {
            return _bindings.TryGetValue(fileAssetId, out Binding? binding) &&
                   binding.Generation == generation &&
                   string.Equals(binding.Path, path, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(binding.SourceVersion, sourceVersion, StringComparison.Ordinal);
        }
    }

    private sealed record Binding(string Path, string SourceVersion, long Generation);
}
