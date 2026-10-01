using System.Text;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Ids;

namespace Patchouli.Host.Caching;

/// <summary>
/// In-memory snapshot of the first-screen library read model for fast tag filtering and sidebar
/// tag counts without re-querying SQLite per filter change. The snapshot covers only non-trashed,
/// non-merged items — the trash view keeps querying <see cref="ILibraryItemQueryService"/>
/// directly, matching today's UI behavior. Readers take an immutable snapshot reference under a
/// short lock and filter outside it, so queries never block a refresh in flight.
/// </summary>
public sealed class LibraryItemCache
{
    private readonly ILibraryItemQueryService _libraryItems;
    private readonly IItemTagService _tags;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private static readonly IComparer<string> TagNameOrder = Comparer<string>.Create(CompareTagNames);
    private Snapshot? _snapshot;

    public LibraryItemCache(ILibraryItemQueryService libraryItems, IItemTagService tags)
    {
        _libraryItems = libraryItems;
        _tags = tags;
    }

    public bool IsLoaded
    {
        get
        {
            lock (_gate)
            {
                return _snapshot is not null;
            }
        }
    }

    /// <summary>Loads the snapshot once; subsequent calls are no-ops.</summary>
    public Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        return IsLoaded ? Task.CompletedTask : RefreshAsync(cancellationToken);
    }

    /// <summary>Reloads rows and tag counts from the database and atomically swaps the snapshot.</summary>
    public async Task<Result> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            return await RefreshCoreAsync(cancellationToken);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Refreshes only the identified bibliographic resources. Changes without a
    /// recoverable item scope retain the full reload used for external writes.</summary>
    public async Task<Result> ApplyChangesAsync(LibraryChangeSet changes,
        CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (!IsLoaded || changes.IsEmpty ||
                (changes.ItemIds.Count == 0 && changes.DocumentInstanceIds.Count == 0 &&
                 (changes.PageIds.Count > 0 || changes.OcrRunIds.Count > 0)))
            {
                return await RefreshCoreAsync(cancellationToken);
            }

            HashSet<ItemId> itemIds = changes.ItemIds.ToHashSet();
            if (changes.DocumentInstanceIds.Count > 0)
            {
                HashSet<DocumentInstanceId> documentIds = changes.DocumentInstanceIds.ToHashSet();
                itemIds.UnionWith(GetSnapshot().Rows.Where(row => row.DocumentInstanceId is { } id &&
                                                                  documentIds.Contains(id)).Select(row => row.ItemId));
                Result<IReadOnlyList<ItemId>> owners =
                    await _libraryItems.GetItemIdsByDocumentInstanceIdsAsync(changes.DocumentInstanceIds,
                        cancellationToken);
                if (owners.IsFailure)
                {
                    return Result.Failure(owners.ErrorCode!, owners.ErrorMessage!);
                }

                itemIds.UnionWith(owners.Value);
            }

            if (itemIds.Count == 0)
            {
                // Style and collection-only changes do not alter this read model.
                return Result.Success();
            }

            // Large imports use a controlled reload instead of an unbounded SQL IN parameter list.
            if (itemIds.Count > 500)
            {
                return await RefreshCoreAsync(cancellationToken);
            }

            Result<IReadOnlyList<LibraryItemRow>> updated =
                await _libraryItems.GetRowsByIdsAsync(itemIds, cancellationToken);
            if (updated.IsFailure)
            {
                return Result.Failure(updated.ErrorCode!, updated.ErrorMessage!);
            }

            Snapshot previous = GetSnapshot();
            Dictionary<ItemId, LibraryItemRow> rows = previous.Rows.ToDictionary(row => row.ItemId);
            Dictionary<string, int> counts = previous.Tags.ToDictionary(tag => tag.Name, tag => tag.Count,
                StringComparer.Ordinal);
            int untagged = previous.UntaggedCount;
            foreach (ItemId itemId in itemIds)
            {
                if (rows.Remove(itemId, out LibraryItemRow? oldRow))
                {
                    AdjustCounts(oldRow, -1);
                }
            }

            foreach (LibraryItemRow row in updated.Value)
            {
                rows[row.ItemId] = row;
                AdjustCounts(row, 1);
            }

            LibraryItemRow[] ordered = rows.Values.OrderByDescending(row => row.CreatedAt, StringComparer.Ordinal)
                .ThenByDescending(row => row.ItemId.ToString(), StringComparer.Ordinal).ToArray();
            TagInfo[] tags = counts.Where(pair => pair.Value > 0)
                .OrderBy(pair => pair.Key, TagNameOrder)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new TagInfo(pair.Key, pair.Value)).ToArray();
            lock (_gate)
            {
                _snapshot = new Snapshot(ordered, tags, untagged);
            }

            return Result.Success();

            void AdjustCounts(LibraryItemRow row, int adjustment)
            {
                if (row.Tags is null || row.Tags.Count == 0)
                {
                    untagged += adjustment;
                    return;
                }

                foreach (string tag in row.Tags)
                {
                    counts[tag] = counts.GetValueOrDefault(tag) + adjustment;
                }
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static int CompareTagNames(string left, string right)
    {
        // SQLite NOCASE folds ASCII letters only and otherwise compares Unicode scalar values.
        StringRuneEnumerator leftRunes = left.EnumerateRunes();
        StringRuneEnumerator rightRunes = right.EnumerateRunes();
        while (true)
        {
            bool hasLeft = leftRunes.MoveNext();
            bool hasRight = rightRunes.MoveNext();
            if (!hasLeft || !hasRight)
            {
                return hasLeft ? 1 : hasRight ? -1 : 0;
            }

            int leftValue = leftRunes.Current.Value;
            int rightValue = rightRunes.Current.Value;
            leftValue = leftValue is >= 'A' and <= 'Z' ? leftValue + 32 : leftValue;
            rightValue = rightValue is >= 'A' and <= 'Z' ? rightValue + 32 : rightValue;
            int comparison = leftValue.CompareTo(rightValue);
            if (comparison != 0)
            {
                return comparison;
            }
        }
    }

    private async Task<Result> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<LibraryItemRow>> rowsResult =
            await _libraryItems.ListRowsAsync(cancellationToken: cancellationToken);
        if (rowsResult.IsFailure)
        {
            return Result.Failure(rowsResult.ErrorCode!, rowsResult.ErrorMessage!);
        }

        Result<IReadOnlyList<TagInfo>> tagsResult = await _tags.ListTagsAsync(cancellationToken);
        if (tagsResult.IsFailure)
        {
            return Result.Failure(tagsResult.ErrorCode!, tagsResult.ErrorMessage!);
        }

        Result<int> untaggedResult = await _libraryItems.CountUntaggedItemsAsync(cancellationToken);
        if (untaggedResult.IsFailure)
        {
            return Result.Failure(untaggedResult.ErrorCode!, untaggedResult.ErrorMessage!);
        }

        lock (_gate)
        {
            _snapshot = new Snapshot(rowsResult.Value, tagsResult.Value, untaggedResult.Value);
        }

        return Result.Success();
    }

    /// <summary>
    /// Filters the snapshot with the same semantics as the SQL tag filter in
    /// <c>LibraryItemQueryService</c>: an item matches only when it carries every required tag
    /// (ordinal comparison, matching SQLite's binary <c>IN</c> collation), and an empty or null
    /// filter returns every row. Throws if the snapshot has not been loaded yet.
    /// </summary>
    public IReadOnlyList<LibraryItemRow> QueryByTags(IReadOnlyList<string>? requiredTags)
    {
        Snapshot snapshot = GetSnapshot();
        if (requiredTags is null || requiredTags.Count == 0)
        {
            return snapshot.Rows;
        }

        HashSet<string> distinctRequired = new(requiredTags, StringComparer.Ordinal);
        List<LibraryItemRow> matches = new();
        foreach (LibraryItemRow row in snapshot.Rows)
        {
            if (row.Tags is null)
            {
                continue;
            }

            int matched = 0;
            foreach (string tag in distinctRequired)
            {
                if (row.Tags.Contains(tag, StringComparer.Ordinal))
                {
                    matched++;
                }
            }

            // Mirrors `count(distinct value) ... = @RequiredTagCount`: distinct matching item
            // tags must equal the raw required-tag count, so duplicate entries in the required
            // list never match — exactly like the SQL path.
            if (matched == requiredTags.Count)
            {
                matches.Add(row);
            }
        }

        return matches;
    }

    /// <summary>
    /// Rows carrying no tags at all, matching the "No tag" pseudo-filter the library shell
    /// applies in memory on top of the SQL query.
    /// </summary>
    public IReadOnlyList<LibraryItemRow> QueryUntagged()
    {
        Snapshot snapshot = GetSnapshot();
        List<LibraryItemRow> matches = new();
        foreach (LibraryItemRow row in snapshot.Rows)
        {
            if (row.Tags is null || row.Tags.Count == 0)
            {
                matches.Add(row);
            }
        }

        return matches;
    }

    /// <summary>Tag name/count pairs plus the untagged-item count the sidebar renders.</summary>
    public TagCountsSnapshot GetTagCounts()
    {
        Snapshot snapshot = GetSnapshot();
        return new TagCountsSnapshot(snapshot.Tags, snapshot.UntaggedCount);
    }

    private Snapshot GetSnapshot()
    {
        lock (_gate)
        {
            return _snapshot ??
                   throw new InvalidOperationException(
                       "The library item cache is not loaded yet. Call EnsureLoadedAsync first.");
        }
    }

    private sealed record Snapshot(
        IReadOnlyList<LibraryItemRow> Rows,
        IReadOnlyList<TagInfo> Tags,
        int UntaggedCount);
}

/// <summary>Tag counts as shown in the library sidebar: one pair per tag plus the untagged count.</summary>
public sealed record TagCountsSnapshot(IReadOnlyList<TagInfo> Tags, int UntaggedCount);
