using Patchouli.Core.Bibliography;
using Patchouli.Core.Library;
using Patchouli.Core.Results;

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
