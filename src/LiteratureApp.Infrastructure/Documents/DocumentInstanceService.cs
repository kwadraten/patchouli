using Dapper;
using LiteratureApp.Core.Documents;
using LiteratureApp.Core.Files;
using LiteratureApp.Core.Ids;
using LiteratureApp.Core.Results;
using LiteratureApp.Core.Time;
using LiteratureApp.Infrastructure.Database;

namespace LiteratureApp.Infrastructure.Documents;

public sealed class DocumentInstanceService : IDocumentInstanceService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;

    public DocumentInstanceService(SqliteConnectionFactory connectionFactory, IClock clock)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
    }

    public async Task<Result<DocumentInstance>> AttachDocumentInstanceAsync(
        ItemId itemId,
        FileAssetId? fileAssetId,
        string instanceType,
        string? title = null,
        bool makePrimary = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(instanceType))
        {
            return Result<DocumentInstance>.Failure(AppErrorCodes.ValidationFailed, "Document instance type is required.");
        }

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var item = await connection.QuerySingleOrDefaultAsync<ItemProbeRow>(
                "select item_id as ItemId, library_id as LibraryId from items where item_id = @ItemId;",
                new { ItemId = itemId.ToString() },
                transaction);

            if (item is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result<DocumentInstance>.Failure(AppErrorCodes.NotFound, "Item was not found.");
            }

            FileAssetProbeRow? fileAsset = null;
            if (fileAssetId is not null)
            {
                fileAsset = await connection.QuerySingleOrDefaultAsync<FileAssetProbeRow>(
                    """
                    select
                        file_asset_id as FileAssetId,
                        library_id as LibraryId,
                        status as Status
                    from file_assets
                    where file_asset_id = @FileAssetId;
                    """,
                    new { FileAssetId = fileAssetId.Value.ToString() },
                    transaction);

                if (fileAsset is null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result<DocumentInstance>.Failure(AppErrorCodes.NotFound, "File asset was not found.");
                }

                if (!string.Equals(fileAsset.LibraryId, item.LibraryId, StringComparison.Ordinal))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result<DocumentInstance>.Failure(
                        AppErrorCodes.LibraryMismatch,
                        "File asset belongs to a different library than the item.");
                }
            }

            var existingCount = await connection.ExecuteScalarAsync<int>(
                "select count(1) from document_instances where item_id = @ItemId;",
                new { ItemId = itemId.ToString() },
                transaction);

            var shouldBePrimary = existingCount == 0 || makePrimary;
            var status = fileAsset?.Status == FileAssetStatus.Missing
                ? DocumentInstanceStatus.MissingSource
                : DocumentInstanceStatus.Active;
            var now = _clock.UtcNow.ToUniversalTime();
            var instance = new DocumentInstance(
                DocumentInstanceId.New(),
                itemId,
                fileAssetId,
                string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
                instanceType.Trim(),
                shouldBePrimary,
                status,
                now,
                now);

            if (shouldBePrimary)
            {
                await connection.ExecuteAsync(
                    "update document_instances set is_primary = 0, updated_at = @UpdatedAt where item_id = @ItemId;",
                    new { UpdatedAt = FormatUtc(now), ItemId = itemId.ToString() },
                    transaction);
            }

            await connection.ExecuteAsync(
                """
                insert into document_instances (
                    document_instance_id, item_id, file_asset_id, title, instance_type,
                    is_primary, status, created_at, updated_at
                )
                values (
                    @DocumentInstanceId, @ItemId, @FileAssetId, @Title, @InstanceType,
                    @IsPrimary, @Status, @CreatedAt, @UpdatedAt
                );
                """,
                ToParameters(instance),
                transaction);

            await transaction.CommitAsync(cancellationToken);
            return Result<DocumentInstance>.Success(instance);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return DatabaseFailure<DocumentInstance>(exception);
        }
    }

    public async Task<Result<DocumentInstance>> GetDocumentInstanceAsync(
        DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var row = await connection.QuerySingleOrDefaultAsync<DocumentInstanceRow>(
                SelectDocumentInstancesSql + " where document_instance_id = @DocumentInstanceId;",
                new { DocumentInstanceId = documentInstanceId.ToString() });

            return row is null
                ? Result<DocumentInstance>.Failure(AppErrorCodes.NotFound, "Document instance was not found.")
                : Result<DocumentInstance>.Success(row.ToDocumentInstance());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return DatabaseFailure<DocumentInstance>(exception);
        }
    }

    public async Task<Result<IReadOnlyList<DocumentInstance>>> ListDocumentInstancesForItemAsync(
        ItemId itemId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var itemExists = await connection.ExecuteScalarAsync<int>(
                "select count(1) from items where item_id = @ItemId;",
                new { ItemId = itemId.ToString() });

            if (itemExists == 0)
            {
                return Result<IReadOnlyList<DocumentInstance>>.Failure(AppErrorCodes.NotFound, "Item was not found.");
            }

            var rows = await connection.QueryAsync<DocumentInstanceRow>(
                SelectDocumentInstancesSql + " where item_id = @ItemId order by created_at, document_instance_id;",
                new { ItemId = itemId.ToString() });

            return Result<IReadOnlyList<DocumentInstance>>.Success(rows.Select(row => row.ToDocumentInstance()).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return DatabaseFailure<IReadOnlyList<DocumentInstance>>(exception);
        }
    }

    public async Task<Result> SetPrimaryDocumentInstanceAsync(
        ItemId itemId,
        DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var itemExists = await connection.ExecuteScalarAsync<int>(
                "select count(1) from items where item_id = @ItemId;",
                new { ItemId = itemId.ToString() },
                transaction);

            if (itemExists == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure(AppErrorCodes.NotFound, "Item was not found.");
            }

            var belongsToItem = await connection.ExecuteScalarAsync<int>(
                """
                select count(1)
                from document_instances
                where document_instance_id = @DocumentInstanceId and item_id = @ItemId;
                """,
                new
                {
                    DocumentInstanceId = documentInstanceId.ToString(),
                    ItemId = itemId.ToString()
                },
                transaction);

            if (belongsToItem == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Result.Failure(
                    AppErrorCodes.InvalidState,
                    "Document instance does not belong to the item.");
            }

            var now = FormatUtc(_clock.UtcNow.ToUniversalTime());
            await connection.ExecuteAsync(
                "update document_instances set is_primary = 0, updated_at = @UpdatedAt where item_id = @ItemId;",
                new { UpdatedAt = now, ItemId = itemId.ToString() },
                transaction);
            await connection.ExecuteAsync(
                """
                update document_instances
                set is_primary = 1, updated_at = @UpdatedAt
                where document_instance_id = @DocumentInstanceId;
                """,
                new { UpdatedAt = now, DocumentInstanceId = documentInstanceId.ToString() },
                transaction);

            await transaction.CommitAsync(cancellationToken);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    private const string SelectDocumentInstancesSql =
        """
        select
            document_instance_id as DocumentInstanceId,
            item_id as ItemId,
            file_asset_id as FileAssetId,
            title as Title,
            instance_type as InstanceType,
            is_primary as IsPrimary,
            status as Status,
            created_at as CreatedAt,
            updated_at as UpdatedAt
        from document_instances
        """;

    private static object ToParameters(DocumentInstance instance)
    {
        return new
        {
            DocumentInstanceId = instance.DocumentInstanceId.ToString(),
            ItemId = instance.ItemId.ToString(),
            FileAssetId = instance.FileAssetId?.ToString(),
            instance.Title,
            instance.InstanceType,
            IsPrimary = instance.IsPrimary ? 1 : 0,
            instance.Status,
            CreatedAt = FormatUtc(instance.CreatedAt),
            UpdatedAt = FormatUtc(instance.UpdatedAt)
        };
    }

    private static string FormatUtc(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    private static Result<T> DatabaseFailure<T>(Exception exception)
    {
        return Result<T>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
    }

    private sealed class ItemProbeRow
    {
        public string ItemId { get; set; } = string.Empty;
        public string LibraryId { get; set; } = string.Empty;
    }

    private sealed class FileAssetProbeRow
    {
        public string FileAssetId { get; set; } = string.Empty;
        public string LibraryId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }

    private sealed class DocumentInstanceRow
    {
        public string DocumentInstanceId { get; set; } = string.Empty;
        public string ItemId { get; set; } = string.Empty;
        public string? FileAssetId { get; set; }
        public string? Title { get; set; }
        public string InstanceType { get; set; } = string.Empty;
        public int IsPrimary { get; set; }
        public string Status { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;

        public DocumentInstance ToDocumentInstance()
        {
            return new DocumentInstance(
                LiteratureApp.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId),
                LiteratureApp.Core.Ids.ItemId.Parse(ItemId),
                FileAssetId is null ? null : LiteratureApp.Core.Ids.FileAssetId.Parse(FileAssetId),
                Title,
                InstanceType,
                IsPrimary == 1,
                Status,
                DateTimeOffset.Parse(CreatedAt),
                DateTimeOffset.Parse(UpdatedAt));
        }
    }
}
