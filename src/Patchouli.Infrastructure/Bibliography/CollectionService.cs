using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.Bibliography;

/// <summary>
/// SQLite-backed one-level Collection store. Names are trimmed and compared ordinally, matching
/// the <c>unique(library_id, name)</c> constraint. Every successful mutation increments the
/// Library revision inside the same transaction and publishes the committed item change set.
/// </summary>
public sealed class CollectionService : ICollectionService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ILibraryIdentityService _library;
    private readonly ILibraryRevisionService? _revisions;
    private readonly IClock _clock;

    public CollectionService(
        SqliteConnectionFactory connectionFactory,
        ILibraryIdentityService library,
        IClock clock,
        ILibraryRevisionService? revisions = null)
    {
        _connectionFactory = connectionFactory;
        _library = library;
        _clock = clock;
        _revisions = revisions;
    }

    public async Task<Result<IReadOnlyList<Collection>>> ListCollectionsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            Result<LibraryId> library = await ResolveLibraryIdAsync(cancellationToken);
            if (library.IsFailure)
            {
                return Result<IReadOnlyList<Collection>>.Failure(library.ErrorCode!, library.ErrorMessage!);
            }

            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            IReadOnlyList<CollectionRow> rows = (await connection.QueryAsync<CollectionRow>(
                """
                select c.collection_id as CollectionId,
                       c.library_id as LibraryId,
                       c.name as Name,
                       c.created_at as CreatedAt,
                       c.updated_at as UpdatedAt,
                       (select count(1)
                        from item_collections ic
                        join items i on i.item_id = ic.item_id
                        where ic.collection_id = c.collection_id
                          and i.deleted_at is null
                          and i.merged_into_item_id is null) as ItemCount
                from collections c
                where c.library_id = @LibraryId
                order by c.name collate binary, c.collection_id;
                """,
                new { LibraryId = library.Value.ToString() })).ToArray();
            return Result<IReadOnlyList<Collection>>.Success(rows.Select(ToModel).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when
            (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.collections"))
        {
            return Result<IReadOnlyList<Collection>>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<Collection>> CreateCollectionAsync(string name,
        CancellationToken cancellationToken = default)
    {
        string? normalized = NormalizeName(name);
        if (normalized is null)
        {
            return Result<Collection>.Failure(AppErrorCodes.ValidationFailed, "Collection name is required.");
        }

        try
        {
            Result<LibraryId> library = await ResolveLibraryIdAsync(cancellationToken);
            if (library.IsFailure)
            {
                return Result<Collection>.Failure(library.ErrorCode!, library.ErrorMessage!);
            }

            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            bool exists = await connection.ExecuteScalarAsync<long>(
                "select count(1) from collections where library_id = @LibraryId and name = @Name;",
                new { LibraryId = library.Value.ToString(), Name = normalized }, transaction) > 0;
            if (exists)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<Collection>.Failure(AppErrorCodes.Conflict, $"集合“{normalized}”已存在。");
            }

            CollectionId collectionId = CollectionId.New();
            string now = FormatUtc(_clock.UtcNow);
            await connection.ExecuteAsync(
                """
                insert into collections (collection_id, library_id, name, created_at, updated_at)
                values (@CollectionId, @LibraryId, @Name, @Now, @Now);
                """,
                new
                {
                    CollectionId = collectionId.ToString(),
                    LibraryId = library.Value.ToString(),
                    Name = normalized,
                    Now = now
                },
                transaction);

            Result<LibraryChangeSet?> revision = await IncrementRevisionAsync(connection, transaction,
                LibraryChangeSet.Empty with { CollectionIds = [collectionId] }, cancellationToken);
            if (revision.IsFailure)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<Collection>.Failure(revision.ErrorCode!, revision.ErrorMessage!);
            }

            await transaction.CommitAsync(cancellationToken);
            PublishRevision(revision.Value);
            return Result<Collection>.Success(new Collection(collectionId, library.Value, normalized, 0,
                DateTimeOffset.Parse(now), DateTimeOffset.Parse(now)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when
            (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.collections"))
        {
            return Result<Collection>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<Collection>> RenameCollectionAsync(CollectionId collectionId, string name,
        CancellationToken cancellationToken = default)
    {
        string? normalized = NormalizeName(name);
        if (normalized is null)
        {
            return Result<Collection>.Failure(AppErrorCodes.ValidationFailed, "Collection name is required.");
        }

        try
        {
            Result<LibraryId> library = await ResolveLibraryIdAsync(cancellationToken);
            if (library.IsFailure)
            {
                return Result<Collection>.Failure(library.ErrorCode!, library.ErrorMessage!);
            }

            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            CollectionRow? existing = await connection.QuerySingleOrDefaultAsync<CollectionRow>(
                """
                select c.collection_id as CollectionId,
                       c.library_id as LibraryId,
                       c.name as Name,
                       c.created_at as CreatedAt,
                       c.updated_at as UpdatedAt,
                       0 as ItemCount
                from collections c
                where c.collection_id = @CollectionId and c.library_id = @LibraryId;
                """,
                new { CollectionId = collectionId.ToString(), LibraryId = library.Value.ToString() }, transaction);
            if (existing is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<Collection>.Failure(AppErrorCodes.NotFound, "集合不存在。");
            }

            bool duplicate = await connection.ExecuteScalarAsync<long>(
                "select count(1) from collections where library_id = @LibraryId and name = @Name and collection_id <> @CollectionId;",
                new { LibraryId = library.Value.ToString(), Name = normalized, CollectionId = collectionId.ToString() },
                transaction) > 0;
            if (duplicate)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<Collection>.Failure(AppErrorCodes.Conflict, $"集合“{normalized}”已存在。");
            }

            string now = FormatUtc(_clock.UtcNow);
            await connection.ExecuteAsync(
                "update collections set name = @Name, updated_at = @Now where collection_id = @CollectionId;",
                new { Name = normalized, Now = now, CollectionId = collectionId.ToString() }, transaction);

            Result<LibraryChangeSet?> revision = await IncrementRevisionAsync(connection, transaction,
                LibraryChangeSet.Empty with { CollectionIds = [collectionId] }, cancellationToken);
            if (revision.IsFailure)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<Collection>.Failure(revision.ErrorCode!, revision.ErrorMessage!);
            }

            await transaction.CommitAsync(cancellationToken);
            PublishRevision(revision.Value);
            int count = await CountMembersAsync(connection, collectionId, cancellationToken);
            return Result<Collection>.Success(new Collection(collectionId, library.Value, normalized, count,
                DateTimeOffset.Parse(existing.CreatedAt), DateTimeOffset.Parse(now)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when
            (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.collections"))
        {
            return Result<Collection>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result> DissolveCollectionAsync(CollectionId collectionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Result<LibraryId> library = await ResolveLibraryIdAsync(cancellationToken);
            if (library.IsFailure)
            {
                return Result.Failure(library.ErrorCode!, library.ErrorMessage!);
            }

            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            string[] memberItemIds = (await connection.QueryAsync<string>(
                "select item_id from item_collections where collection_id = @CollectionId;",
                new { CollectionId = collectionId.ToString() }, transaction)).ToArray();

            await connection.ExecuteAsync(
                "delete from item_collections where collection_id = @CollectionId;",
                new { CollectionId = collectionId.ToString() }, transaction);
            int removed = await connection.ExecuteAsync(
                "delete from collections where collection_id = @CollectionId and library_id = @LibraryId;",
                new { CollectionId = collectionId.ToString(), LibraryId = library.Value.ToString() },
                transaction);
            if (removed == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure(AppErrorCodes.NotFound, "集合不存在。");
            }

            Result<LibraryChangeSet?> revision = await IncrementRevisionAsync(connection, transaction,
                LibraryChangeSet.Empty with
                {
                    ItemIds = memberItemIds.Select(ItemId.Parse).ToArray(),
                    CollectionIds = [collectionId]
                },
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
            (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.collections"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result> AddItemsAsync(CollectionId collectionId, IReadOnlyList<ItemId> itemIds,
        CancellationToken cancellationToken = default)
    {
        if (itemIds.Count == 0)
        {
            return Result.Success();
        }

        return await MutateMembershipAsync(collectionId, itemIds, true, cancellationToken);
    }

    public async Task<Result> RemoveItemsAsync(CollectionId collectionId, IReadOnlyList<ItemId> itemIds,
        CancellationToken cancellationToken = default)
    {
        if (itemIds.Count == 0)
        {
            return Result.Success();
        }

        return await MutateMembershipAsync(collectionId, itemIds, false, cancellationToken);
    }

    public async Task<Result> SetItemCollectionsAsync(ItemId itemId, IReadOnlyList<CollectionId> collectionIds,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Result<LibraryId> library = await ResolveLibraryIdAsync(cancellationToken);
            if (library.IsFailure)
            {
                return Result.Failure(library.ErrorCode!, library.ErrorMessage!);
            }

            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            int itemExists = await connection.ExecuteScalarAsync<int>(
                """
                select count(1) from items
                where item_id = @ItemId
                  and deleted_at is null
                  and merged_into_item_id is null;
                """,
                new { ItemId = itemId.ToString() }, transaction);
            if (itemExists == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure(AppErrorCodes.NotFound, "题录不存在或不可用。");
            }

            string[] targetIds = collectionIds.Select(static id => id.ToString()).Distinct(StringComparer.Ordinal)
                .ToArray();
            string[] validIds = targetIds.Length == 0
                ? []
                : (await connection.QueryAsync<string>(
                    """
                    select collection_id
                    from collections
                    where library_id = @LibraryId and collection_id in @CollectionIds;
                    """,
                    new { LibraryId = library.Value.ToString(), CollectionIds = targetIds }, transaction)).ToArray();
            if (validIds.Length != targetIds.Length)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure(AppErrorCodes.NotFound, "一个或多个集合不存在。");
            }

            string[] previousIds = (await connection.QueryAsync<string>(
                "select collection_id from item_collections where item_id = @ItemId;",
                new { ItemId = itemId.ToString() }, transaction)).ToArray();

            await connection.ExecuteAsync(
                "delete from item_collections where item_id = @ItemId;",
                new { ItemId = itemId.ToString() }, transaction);

            string now = FormatUtc(_clock.UtcNow);
            foreach (string validId in validIds)
            {
                await connection.ExecuteAsync(
                    """
                    insert or ignore into item_collections (collection_id, item_id, added_at)
                    values (@CollectionId, @ItemId, @Now);
                    """,
                    new { CollectionId = validId, ItemId = itemId.ToString(), Now = now }, transaction);
            }

            CollectionId[] affectedCollections = previousIds.Concat(validIds)
                .Distinct(StringComparer.Ordinal)
                .Select(CollectionId.Parse)
                .ToArray();
            Result<LibraryChangeSet?> revision = await IncrementRevisionAsync(connection, transaction,
                LibraryChangeSet.Empty with { ItemIds = [itemId], CollectionIds = affectedCollections },
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
            (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.collections"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<CollectionId>>> GetItemCollectionIdsAsync(ItemId itemId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            string[] ids = (await connection.QueryAsync<string>(
                """
                select ic.collection_id
                from item_collections ic
                join collections c on c.collection_id = ic.collection_id
                where ic.item_id = @ItemId
                order by c.name collate binary, c.collection_id;
                """,
                new { ItemId = itemId.ToString() })).ToArray();
            return Result<IReadOnlyList<CollectionId>>.Success(ids.Select(CollectionId.Parse).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when
            (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.collections"))
        {
            return Result<IReadOnlyList<CollectionId>>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<ItemId>>> GetCollectionItemIdsAsync(CollectionId collectionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            string[] ids = (await connection.QueryAsync<string>(
                """
                select ic.item_id
                from item_collections ic
                join items i on i.item_id = ic.item_id
                where ic.collection_id = @CollectionId
                  and i.deleted_at is null
                  and i.merged_into_item_id is null;
                """,
                new { CollectionId = collectionId.ToString() })).ToArray();
            return Result<IReadOnlyList<ItemId>>.Success(ids.Select(ItemId.Parse).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when
            (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.collections"))
        {
            return Result<IReadOnlyList<ItemId>>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {exception.Message}");
        }
    }

    private async Task<Result> MutateMembershipAsync(CollectionId collectionId, IReadOnlyList<ItemId> itemIds,
        bool add, CancellationToken cancellationToken)
    {
        try
        {
            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            bool collectionExists = await connection.ExecuteScalarAsync<long>(
                "select count(1) from collections where collection_id = @CollectionId;",
                new { CollectionId = collectionId.ToString() }, transaction) > 0;
            if (!collectionExists)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure(AppErrorCodes.NotFound, "集合不存在。");
            }

            string[] itemIdTexts = itemIds.Select(static id => id.ToString()).Distinct(StringComparer.Ordinal)
                .ToArray();
            string[] activeItemIds = (await connection.QueryAsync<string>(
                """
                select item_id
                from items
                where item_id in @ItemIds
                  and deleted_at is null
                  and merged_into_item_id is null;
                """,
                new { ItemIds = itemIdTexts }, transaction)).ToArray();
            if (activeItemIds.Length == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Success();
            }

            int affected;
            if (add)
            {
                string now = FormatUtc(_clock.UtcNow);
                affected = 0;
                foreach (string itemIdText in activeItemIds)
                {
                    affected += await connection.ExecuteAsync(
                        """
                        insert or ignore into item_collections (collection_id, item_id, added_at)
                        values (@CollectionId, @ItemId, @Now);
                        """,
                        new { CollectionId = collectionId.ToString(), ItemId = itemIdText, Now = now },
                        transaction);
                }
            }
            else
            {
                affected = await connection.ExecuteAsync(
                    "delete from item_collections where collection_id = @CollectionId and item_id in @ItemIds;",
                    new { CollectionId = collectionId.ToString(), ItemIds = activeItemIds }, transaction);
            }

            if (affected == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Success();
            }

            Result<LibraryChangeSet?> revision = await IncrementRevisionAsync(connection, transaction,
                LibraryChangeSet.Empty with
                {
                    ItemIds = activeItemIds.Select(ItemId.Parse).ToArray(),
                    CollectionIds = [collectionId]
                },
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
            (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.collections"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    private static async Task<int> CountMembersAsync(SqliteConnection connection, CollectionId collectionId,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return await connection.ExecuteScalarAsync<int>(
            """
            select count(1)
            from item_collections ic
            join items i on i.item_id = ic.item_id
            where ic.collection_id = @CollectionId
              and i.deleted_at is null
              and i.merged_into_item_id is null;
            """,
            new { CollectionId = collectionId.ToString() });
    }

    private async Task<Result<LibraryId>> ResolveLibraryIdAsync(CancellationToken cancellationToken)
    {
        Result<LibraryMetadata> library = await _library.GetCurrentLibraryAsync(cancellationToken);
        return library.IsFailure
            ? Result<LibraryId>.Failure(library.ErrorCode!, library.ErrorMessage!)
            : Result<LibraryId>.Success(library.Value.LibraryId);
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

    private static string? NormalizeName(string? name)
    {
        string? trimmed = name?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static Collection ToModel(CollectionRow row)
    {
        return new Collection(
            CollectionId.Parse(row.CollectionId),
            LibraryId.Parse(row.LibraryId),
            row.Name,
            row.ItemCount,
            DateTimeOffset.Parse(row.CreatedAt),
            DateTimeOffset.Parse(row.UpdatedAt));
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O");
    }

    private sealed class CollectionRow
    {
        public string CollectionId { get; init; } = "";
        public string LibraryId { get; init; } = "";
        public string Name { get; init; } = "";
        public string CreatedAt { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
        public int ItemCount { get; init; }
    }
}
