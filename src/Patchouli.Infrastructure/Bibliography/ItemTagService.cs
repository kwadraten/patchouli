using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.Bibliography;

public sealed class ItemTagService : IItemTagService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ILibraryRevisionService? _revisions;

    public ItemTagService(SqliteConnectionFactory connectionFactory, ILibraryRevisionService? revisions = null)
    {
        _connectionFactory = connectionFactory;
        _revisions = revisions;
    }

    public async Task<Result<IReadOnlyList<TagInfo>>> ListTagsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            IEnumerable<TagCountRow> rows = await connection.QueryAsync<TagCountRow>(
                """
                select value as Name, count(*) as Count
                from items, json_each(tags_json)
                where deleted_at is null
                  and merged_into_item_id is null
                  and json_type(tags_json) = 'array'
                group by value
                order by value collate nocase;
                """);
            return Result<IReadOnlyList<TagInfo>>.Success(
                rows.Select(row => new TagInfo(row.Name, row.Count)).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when
            (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.item-tags"))
        {
            return Result<IReadOnlyList<TagInfo>>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result> AddTagsToItemsAsync(
        IReadOnlyList<ItemId> itemIds,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> normalizedTags = TagNormalizer.NormalizeMany(tags);
        if (normalizedTags.Count == 0 || itemIds.Count == 0)
        {
            return Result.Success();
        }

        return await MutateItemsAsync(
            itemIds,
            existing =>
            {
                HashSet<string> set = new(existing, StringComparer.Ordinal);
                List<string> result = new(existing);
                foreach (string tag in normalizedTags)
                {
                    if (set.Add(tag))
                    {
                        result.Add(tag);
                    }
                }

                return result;
            },
            cancellationToken);
    }

    public async Task<Result> RemoveTagFromItemsAsync(
        IReadOnlyList<ItemId> itemIds,
        string tag,
        CancellationToken cancellationToken = default)
    {
        string? normalized = TagNormalizer.Normalize(tag);
        if (normalized is null || itemIds.Count == 0)
        {
            return Result.Success();
        }

        return await MutateItemsAsync(
            itemIds,
            existing => existing.Where(t => !string.Equals(t, normalized, StringComparison.Ordinal)).ToArray(),
            cancellationToken);
    }

    public async Task<Result> SetTagsAsync(
        IReadOnlyList<ItemId> itemIds,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> normalizedTags = TagNormalizer.NormalizeMany(tags);
        if (itemIds.Count == 0)
        {
            return Result.Success();
        }

        string tagsJson = JsonSerializer.Serialize(normalizedTags);
        return await WriteTagsAsync(itemIds, _ => normalizedTags, tagsJson, cancellationToken);
    }

    public async Task<Result> RenameTagAsync(
        string oldTag,
        string newTag,
        CancellationToken cancellationToken = default)
    {
        string? normalizedOld = TagNormalizer.Normalize(oldTag);
        string? normalizedNew = TagNormalizer.Normalize(newTag);
        if (normalizedOld is null || normalizedNew is null ||
            string.Equals(normalizedOld, normalizedNew, StringComparison.Ordinal))
        {
            return Result.Success();
        }

        return await MutateAllActiveAsync(
            existing => RenameInPlace(existing, normalizedOld, normalizedNew),
            cancellationToken);
    }

    public async Task<Result> MergeTagsAsync(
        string sourceTag,
        string targetTag,
        CancellationToken cancellationToken = default)
    {
        string? normalizedSource = TagNormalizer.Normalize(sourceTag);
        string? normalizedTarget = TagNormalizer.Normalize(targetTag);
        if (normalizedSource is null || normalizedTarget is null ||
            string.Equals(normalizedSource, normalizedTarget, StringComparison.Ordinal))
        {
            return Result.Success();
        }

        return await MutateAllActiveAsync(
            existing => MergeInPlace(existing, normalizedSource, normalizedTarget),
            cancellationToken);
    }

    public async Task<Result> RemoveTagAsync(
        string tag,
        CancellationToken cancellationToken = default)
    {
        string? normalized = TagNormalizer.Normalize(tag);
        if (normalized is null)
        {
            return Result.Success();
        }

        return await MutateAllActiveAsync(
            existing => existing.Where(t => !string.Equals(t, normalized, StringComparison.Ordinal)).ToArray(),
            cancellationToken);
    }

    private static IReadOnlyList<string> RenameInPlace(IReadOnlyList<string> tags, string oldTag, string newTag)
    {
        if (!tags.Contains(oldTag, StringComparer.Ordinal))
        {
            return tags;
        }

        bool hasTarget = tags.Contains(newTag, StringComparer.Ordinal);
        List<string> result = new(tags.Count);
        foreach (string tag in tags)
        {
            if (string.Equals(tag, oldTag, StringComparison.Ordinal))
            {
                if (!hasTarget)
                {
                    result.Add(newTag);
                    hasTarget = true;
                }
            }
            else
            {
                result.Add(tag);
            }
        }

        return result;
    }

    private static IReadOnlyList<string> MergeInPlace(IReadOnlyList<string> tags, string sourceTag, string targetTag)
    {
        if (!tags.Contains(sourceTag, StringComparer.Ordinal))
        {
            return tags;
        }

        bool hasTarget = tags.Contains(targetTag, StringComparer.Ordinal);
        List<string> result = new(tags.Count);
        foreach (string tag in tags)
        {
            if (string.Equals(tag, sourceTag, StringComparison.Ordinal))
            {
                if (!hasTarget)
                {
                    result.Add(targetTag);
                    hasTarget = true;
                }
            }
            else
            {
                result.Add(tag);
            }
        }

        return result;
    }

    private async Task<Result> MutateItemsAsync(
        IReadOnlyList<ItemId> itemIds,
        Func<IReadOnlyList<string>, IReadOnlyList<string>> transform,
        CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return Result.Success();
        }

        try
        {
            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            string[] itemIdTexts = itemIds.Select(static id => id.ToString()).Distinct().ToArray();
            Dictionary<string, string> tagsByItem = (await connection.QueryAsync<ItemTagRow>(
                    """
                    select item_id as ItemId, tags_json as TagsJson
                    from items
                    where item_id in @ItemIds
                      and deleted_at is null
                      and merged_into_item_id is null;
                    """,
                    new { ItemIds = itemIdTexts }, transaction))
                .ToDictionary(static row => row.ItemId, static row => row.TagsJson, StringComparer.Ordinal);

            if (tagsByItem.Count == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Success();
            }

            List<ItemId> affectedItemIds = new();
            foreach (string itemIdText in tagsByItem.Keys)
            {
                IReadOnlyList<string> existing = ParseTags(tagsByItem[itemIdText]);
                IReadOnlyList<string> next = transform(existing);
                if (SequenceEqual(existing, next))
                {
                    continue;
                }

                affectedItemIds.Add(ItemId.Parse(itemIdText));
                await connection.ExecuteAsync(
                    "update items set tags_json = @TagsJson, updated_at = @UpdatedAt where item_id = @ItemId;",
                    new
                    {
                        ItemId = itemIdText,
                        TagsJson = JsonSerializer.Serialize(next),
                        UpdatedAt = FormatUtc(DateTimeOffset.UtcNow)
                    }, transaction);
            }

            if (affectedItemIds.Count == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Success();
            }

            Result<LibraryChangeSet?> revision = await IncrementRevisionAsync(
                connection, transaction, LibraryChangeSet.Empty with { ItemIds = affectedItemIds }, cancellationToken);
            if (revision.IsFailure)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure(revision.ErrorCode!, revision.ErrorMessage!);
            }

            await transaction.CommitAsync(cancellationToken);
            PublishRevision(revision.Value);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when
            (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.item-tags"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    private async Task<Result> WriteTagsAsync(
        IReadOnlyList<ItemId> itemIds,
        Func<IReadOnlyList<string>, IReadOnlyList<string>> transform,
        string fallbackTagsJson,
        CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return Result.Success();
        }

        try
        {
            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            string[] itemIdTexts = itemIds.Select(static id => id.ToString()).Distinct().ToArray();
            HashSet<string> activeItemIds = (await connection.QueryAsync<string>(
                    """
                    select item_id
                    from items
                    where item_id in @ItemIds
                      and deleted_at is null
                      and merged_into_item_id is null;
                    """,
                    new { ItemIds = itemIdTexts }, transaction))
                .ToHashSet(StringComparer.Ordinal);

            if (activeItemIds.Count == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Success();
            }

            string tagsJson = fallbackTagsJson;
            string updatedAt = FormatUtc(DateTimeOffset.UtcNow);
            await connection.ExecuteAsync(
                """
                update items
                set tags_json = @TagsJson, updated_at = @UpdatedAt
                where item_id in @ItemIds
                  and deleted_at is null
                  and merged_into_item_id is null;
                """,
                new { TagsJson = tagsJson, UpdatedAt = updatedAt, ItemIds = activeItemIds.ToArray() }, transaction);

            Result<LibraryChangeSet?> revision = await IncrementRevisionAsync(
                connection,
                transaction,
                LibraryChangeSet.Empty with { ItemIds = activeItemIds.Select(ItemId.Parse).ToArray() },
                cancellationToken);
            if (revision.IsFailure)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure(revision.ErrorCode!, revision.ErrorMessage!);
            }

            await transaction.CommitAsync(cancellationToken);
            PublishRevision(revision.Value);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when
            (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.item-tags"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    private async Task<Result> MutateAllActiveAsync(
        Func<IReadOnlyList<string>, IReadOnlyList<string>> transform,
        CancellationToken cancellationToken)
    {
        try
        {
            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            IEnumerable<ItemTagRow> rows = await connection.QueryAsync<ItemTagRow>(
                """
                select item_id as ItemId, tags_json as TagsJson
                from items
                where deleted_at is null
                  and merged_into_item_id is null
                  and json_type(tags_json) = 'array';
                """,
                transaction: transaction);

            List<ItemId> affectedItemIds = new();
            foreach (ItemTagRow row in rows)
            {
                IReadOnlyList<string> existing = ParseTags(row.TagsJson);
                IReadOnlyList<string> next = transform(existing);
                if (SequenceEqual(existing, next))
                {
                    continue;
                }

                affectedItemIds.Add(ItemId.Parse(row.ItemId));
                await connection.ExecuteAsync(
                    "update items set tags_json = @TagsJson, updated_at = @UpdatedAt where item_id = @ItemId;",
                    new
                    {
                        ItemId = row.ItemId,
                        TagsJson = JsonSerializer.Serialize(next),
                        UpdatedAt = FormatUtc(DateTimeOffset.UtcNow)
                    }, transaction);
            }

            if (affectedItemIds.Count == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Success();
            }

            Result<LibraryChangeSet?> revision = await IncrementRevisionAsync(
                connection, transaction, LibraryChangeSet.Empty with { ItemIds = affectedItemIds }, cancellationToken);
            if (revision.IsFailure)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure(revision.ErrorCode!, revision.ErrorMessage!);
            }

            await transaction.CommitAsync(cancellationToken);
            PublishRevision(revision.Value);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when
            (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.item-tags"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    private static IReadOnlyList<string> ParseTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson))
        {
            return Array.Empty<string>();
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(tagsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return document.RootElement.EnumerateArray()
                .Select(element => element.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool SequenceEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index], right[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<Result<LibraryChangeSet?>> IncrementRevisionAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        LibraryChangeSet changeSet,
        CancellationToken cancellationToken)
    {
        if (_revisions is null)
        {
            return Result<LibraryChangeSet?>.Success(null);
        }

        Result<LibraryChangeSet> revision = await _revisions.IncrementInTransactionAsync(
            connection, transaction, changeSet, cancellationToken);
        return revision.IsSuccess
            ? Result<LibraryChangeSet?>.Success(revision.Value)
            : Result<LibraryChangeSet?>.Failure(revision.ErrorCode!, revision.ErrorMessage!);
    }

    private void PublishRevision(LibraryChangeSet? changeSet)
    {
        if (changeSet is not null)
        {
            _revisions?.PublishCommitted(changeSet);
        }
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O");
    }

    private sealed class TagCountRow
    {
        public string Name { get; init; } = "";
        public int Count { get; init; }
    }

    private sealed class ItemTagRow
    {
        public string ItemId { get; init; } = "";
        public string TagsJson { get; init; } = "[]";
    }
}
