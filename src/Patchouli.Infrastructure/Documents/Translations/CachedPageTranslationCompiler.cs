using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Documents.Translations;

/// <summary>
/// Routes page translation reads through a bounded cache. The key carries the page id, tree
/// revision and translation version, so a tree change or a new write naturally selects a new
/// entry; the writer still calls <see cref="Invalidate"/> to release the superseded entries.
/// </summary>
public sealed class CachedPageTranslationCompiler(
    IPageTranslationCompiler inner,
    PageTranslationCache cache) : IPageTranslationCompiler
{
    public PageTranslationCache Cache { get; } = cache;

    public Task<Result<TranslatedPageMarkdown>> CompilePageTranslationAsync(
        PageId pageId,
        DocumentTreeRevisionId treeRevisionId,
        int version,
        CancellationToken cancellationToken = default)
    {
        return Cache.GetOrCreateAsync(
            pageId,
            treeRevisionId,
            version,
            sharedCancellationToken => inner.CompilePageTranslationAsync(
                pageId, treeRevisionId, version, sharedCancellationToken),
            cancellationToken);
    }
}

/// <summary>
/// Small insertion-ordered cache of compiled page translations. Entries are content-free keys
/// plus the compiled result, bounded by an entry count so a long session cannot grow without
/// limit; failed compilations are never retained.
/// </summary>
public sealed class PageTranslationCache
{
    private const int DefaultEntryLimit = 256;

    private readonly int _entryLimit;
    private readonly Dictionary<CacheKey, Task<Result<TranslatedPageMarkdown>>> _entries = new();
    private readonly LinkedList<CacheKey> _order = new();
    private readonly object _sync = new();

    public PageTranslationCache(int entryLimit = DefaultEntryLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(entryLimit);
        _entryLimit = entryLimit;
    }

    public Task<Result<TranslatedPageMarkdown>> GetOrCreateAsync(
        PageId pageId,
        DocumentTreeRevisionId treeRevisionId,
        int version,
        Func<CancellationToken, Task<Result<TranslatedPageMarkdown>>> factory,
        CancellationToken cancellationToken = default)
    {
        CacheKey key = new(pageId, treeRevisionId, version);
        Task<Result<TranslatedPageMarkdown>> task;
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out task!))
            {
                task = ProduceAsync(key, factory);
                _entries[key] = task;
                _order.AddLast(key);
                Trim();
            }
        }

        return task.WaitAsync(cancellationToken);
    }

    public void Invalidate(PageId pageId)
    {
        lock (_sync)
        {
            CacheKey[] keys = _entries.Keys.Where(key => key.PageId == pageId).ToArray();
            foreach (CacheKey key in keys)
            {
                _entries.Remove(key);
                _order.Remove(key);
            }
        }
    }

    private async Task<Result<TranslatedPageMarkdown>> ProduceAsync(
        CacheKey key,
        Func<CancellationToken, Task<Result<TranslatedPageMarkdown>>> factory)
    {
        try
        {
            Result<TranslatedPageMarkdown> result = await factory(CancellationToken.None);
            if (result.IsFailure)
            {
                Remove(key);
            }

            return result;
        }
        catch
        {
            Remove(key);
            throw;
        }
    }

    private void Remove(CacheKey key)
    {
        lock (_sync)
        {
            _entries.Remove(key);
            _order.Remove(key);
        }
    }

    private void Trim()
    {
        while (_entries.Count > _entryLimit && _order.First is { } oldest)
        {
            _order.RemoveFirst();
            _entries.Remove(oldest.Value);
        }
    }

    private readonly record struct CacheKey(
        PageId PageId,
        DocumentTreeRevisionId TreeRevisionId,
        int Version);
}
