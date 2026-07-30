using System.Text;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Mcp;

internal sealed class CompiledMarkdownCache
{
    internal const long DefaultByteLimit = 32 * 1024 * 1024;

    private readonly long _byteLimit;
    private readonly Dictionary<CacheKey, Entry> _entries = new();
    private readonly LinkedList<CacheKey> _lru = new();
    private readonly object _sync = new();
    private long _cachedBytes;

    public CompiledMarkdownCache(long byteLimit = DefaultByteLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteLimit);
        _byteLimit = byteLimit;
    }

    public Task<Result<CompiledMarkdown>> GetOrCreateAsync(
        DocumentTreeRevisionId revisionId,
        bool includeSuppressed,
        bool includeComplexTableHtml,
        Func<CancellationToken, Task<Result<CompiledMarkdown>>> factory,
        CancellationToken cancellationToken)
    {
        CacheKey key = new(revisionId, includeSuppressed, includeComplexTableHtml);
        Entry entry;
        TaskCompletionSource<Result<CompiledMarkdown>>? completion = null;
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out entry!))
            {
                Touch(entry);
            }
            else
            {
                completion = new TaskCompletionSource<Result<CompiledMarkdown>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                entry = new Entry(completion.Task);
                _entries.Add(key, entry);
            }
        }

        if (completion is not null)
        {
            _ = ProduceAsync(key, entry, completion, factory);
        }

        return entry.Task.WaitAsync(cancellationToken);
    }

    private async Task ProduceAsync(
        CacheKey key,
        Entry entry,
        TaskCompletionSource<Result<CompiledMarkdown>> completion,
        Func<CancellationToken, Task<Result<CompiledMarkdown>>> factory)
    {
        try
        {
            Result<CompiledMarkdown> result = await factory(CancellationToken.None);
            lock (_sync)
            {
                if (result.IsSuccess)
                {
                    CacheSuccessfulResult(key, entry, result.Value);
                }
                else
                {
                    _entries.Remove(key);
                }
            }

            completion.TrySetResult(result);
        }
        catch (OperationCanceledException exception)
        {
            RemoveEntry(key, entry);
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.compiled-markdown-cache"))
        {
            RemoveEntry(key, entry);
            completion.TrySetException(exception);
        }
    }

    private void CacheSuccessfulResult(CacheKey key, Entry entry, CompiledMarkdown result)
    {
        entry.Size = EstimateSize(result);
        if (entry.Size > _byteLimit)
        {
            _entries.Remove(key);
            return;
        }

        entry.Node = _lru.AddFirst(key);
        _cachedBytes += entry.Size;
        while (_cachedBytes > _byteLimit && _lru.Last is { } oldest)
        {
            CacheKey oldestKey = oldest.Value;
            _lru.RemoveLast();
            Entry removed = _entries[oldestKey];
            _entries.Remove(oldestKey);
            _cachedBytes -= removed.Size;
        }
    }

    private static long EstimateSize(CompiledMarkdown result)
    {
        long size = Math.Max(1024, Encoding.UTF8.GetByteCount(result.Markdown) * 2L);
        size += result.SourceMap.Count * 64L;
        size += result.Diagnostics.Sum(diagnostic =>
            64L + Encoding.UTF8.GetByteCount(diagnostic.Code) + Encoding.UTF8.GetByteCount(diagnostic.Message));
        if (result.Document is not null)
        {
            size += result.Document.Blocks.Sum(block =>
                96L + Encoding.UTF8.GetByteCount(block.Kind) + Encoding.UTF8.GetByteCount(block.Text) +
                EstimateInlines(block.Inlines));
        }

        return size;
    }

    private static long EstimateInlines(IReadOnlyList<MarkdownInlineModel>? inlines)
    {
        return inlines?.Sum(inline =>
            64L + Encoding.UTF8.GetByteCount(inline.Kind) + Encoding.UTF8.GetByteCount(inline.Text) +
            EstimateInlines(inline.Children)) ?? 0;
    }

    private void RemoveEntry(CacheKey key, Entry entry)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out Entry? current) && ReferenceEquals(current, entry))
            {
                _entries.Remove(key);
            }
        }
    }

    private void Touch(Entry entry)
    {
        if (entry.Node is null)
        {
            return;
        }

        _lru.Remove(entry.Node);
        _lru.AddFirst(entry.Node);
    }

    private readonly record struct CacheKey(
        DocumentTreeRevisionId RevisionId,
        bool IncludeSuppressed,
        bool IncludeComplexTableHtml);

    private sealed class Entry(Task<Result<CompiledMarkdown>> task)
    {
        public Task<Result<CompiledMarkdown>> Task { get; } = task;
        public LinkedListNode<CacheKey>? Node { get; set; }
        public long Size { get; set; }
    }
}
