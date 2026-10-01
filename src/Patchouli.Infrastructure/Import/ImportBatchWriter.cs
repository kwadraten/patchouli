using System.Data.Common;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Files;

namespace Patchouli.Infrastructure.Import;

public sealed record PdfImportPagePlaceholder(
    int PageIndex,
    string DiagnosticCode,
    string Reason);

public sealed record PdfImportBatch(
    DateTimeOffset Now,
    FileAsset FileAsset,
    string NormalizedFilePath,
    ItemMetadata Item,
    IReadOnlyList<ItemCreatorInput> Creators,
    IReadOnlyList<ItemDateInput> Dates,
    DocumentInstance DocumentInstance,
    IReadOnlyList<Page> Pages,
    IReadOnlyList<PdfImportPagePlaceholder> PagePlaceholders);

public sealed class ImportBatchWriter
{
    private const int PageWriteBatchSize = 500;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ILibraryRevisionService? _revisions;

    public ImportBatchWriter(SqliteConnectionFactory connectionFactory, ILibraryRevisionService? revisions = null)
    {
        _connectionFactory = connectionFactory;
        _revisions = revisions;
    }

    public async Task<Result> CommitAsync(PdfImportBatch batch, CancellationToken cancellationToken = default)
    {
        try
        {
            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            FileAsset fileAsset = batch.FileAsset;
            FileAssetProbeRow? existing = await connection.QuerySingleOrDefaultAsync<FileAssetProbeRow>(
                "select library_id as LibraryId from file_assets where file_asset_id = @FileAssetId;",
                new { FileAssetId = fileAsset.FileAssetId.ToString() },
                transaction);

            if (existing is not null)
            {
                if (LibraryId.Parse(existing.LibraryId) != fileAsset.LibraryId)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result.Failure(
                        AppErrorCodes.LibraryMismatch,
                        "File content already exists in a different library.");
                }

                await connection.ExecuteAsync(
                    """
                    update file_assets
                    set size_bytes = @SizeBytes,
                        mtime_utc = @MtimeUtc,
                        quick_hash = @QuickHash,
                        full_blake3 = @FullBlake3,
                        status = @Status,
                        updated_at = @UpdatedAt
                    where file_asset_id = @FileAssetId;
                    """,
                    new
                    {
                        FileAssetId = fileAsset.FileAssetId.ToString(),
                        fileAsset.SizeBytes,
                        MtimeUtc = fileAsset.MtimeUtc?.ToUniversalTime().ToString("O"),
                        fileAsset.QuickHash,
                        fileAsset.FullBlake3,
                        fileAsset.Status,
                        UpdatedAt = FormatUtc(batch.Now)
                    },
                    transaction);
            }
            else
            {
                await connection.ExecuteAsync(
                    """
                    insert into file_assets (
                        file_asset_id, library_id, original_path, file_name, size_bytes,
                        mtime_utc, quick_hash, full_blake3, page_count, pdf_trailer_id,
                        status, created_at, updated_at
                    )
                    values (
                        @FileAssetId, @LibraryId, @OriginalPath, @FileName, @SizeBytes,
                        @MtimeUtc, @QuickHash, @FullBlake3, @PageCount, @PdfTrailerId,
                        @Status, @CreatedAt, @UpdatedAt
                    );
                    """,
                    FileAssetService.ToParameters(fileAsset),
                    transaction);
            }

            await FileAssetService.UpsertKnownLocationAsync(
                connection,
                transaction,
                fileAsset.FileAssetId,
                batch.NormalizedFilePath,
                FileAssetStatus.Available,
                batch.Now);

            await connection.ExecuteAsync(
                """
                insert into items (
                    item_id, library_id, item_type, citation_key, title, subtitle, title_short, creators_json, date,
                    publication_title, container_title_short, collection_title, publisher, place, edition, genre,
                    number, chapter_number, volume, version, issue, pages, language, status, note, abstract,
                    tags_json, collections_json, custom_fields_json, created_at, updated_at, deleted_at
                )
                values (
                    @ItemId, @LibraryId, @ItemType, @CitationKey, @Title, @Subtitle, @TitleShort, @CreatorsJson, @Date,
                    @PublicationTitle, @ContainerTitleShort, @CollectionTitle, @Publisher, @Place, @Edition, @Genre,
                    @Number, @ChapterNumber, @Volume, @Version, @Issue, @Pages, @Language, @Status, @Note, @Abstract,
                    @TagsJson, @CollectionsJson, @CustomFieldsJson, @CreatedAt, @UpdatedAt, null
                );
                """,
                ItemService.ToParameters(batch.Item),
                transaction);

            await ItemService.ReplaceCreatorsAsync(
                connection, transaction, batch.Item.ItemId, batch.Creators, batch.Now);
            await ItemService.ReplaceDatesAsync(
                connection, transaction, batch.Item.ItemId, batch.Dates, batch.Now);

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
                new
                {
                    DocumentInstanceId = batch.DocumentInstance.DocumentInstanceId.ToString(),
                    ItemId = batch.DocumentInstance.ItemId.ToString(),
                    FileAssetId = batch.DocumentInstance.FileAssetId?.ToString(),
                    batch.DocumentInstance.Title,
                    batch.DocumentInstance.InstanceType,
                    IsPrimary = batch.DocumentInstance.IsPrimary ? 1 : 0,
                    batch.DocumentInstance.Status,
                    CreatedAt = FormatUtc(batch.DocumentInstance.CreatedAt),
                    UpdatedAt = FormatUtc(batch.DocumentInstance.UpdatedAt)
                },
                transaction);

            foreach (Page[] chunk in batch.Pages.Chunk(PageWriteBatchSize))
            {
                await InsertPagesAsync(connection, transaction, chunk);
            }

            foreach (PdfImportPagePlaceholder placeholder in batch.PagePlaceholders)
            {
                await InsertPagePlaceholderAsync(connection, transaction, batch, placeholder);
            }

            Result<LibraryChangeSet?> revision = await IncrementRevisionAsync(
                connection, transaction, batch, cancellationToken);
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
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.import-batch"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    private async Task<Result<LibraryChangeSet?>> IncrementRevisionAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        PdfImportBatch batch,
        CancellationToken cancellationToken)
    {
        if (_revisions is null)
        {
            return Result<LibraryChangeSet?>.Success(null);
        }

        Result<LibraryChangeSet> revision = await _revisions.IncrementInTransactionAsync(
            connection,
            transaction,
            LibraryChangeSet.Empty with
            {
                ItemIds = [batch.Item.ItemId],
                DocumentInstanceIds = [batch.DocumentInstance.DocumentInstanceId],
                PageIds = batch.Pages.Select(page => page.PageId).ToArray()
            },
            cancellationToken);
        return revision.IsSuccess
            ? Result<LibraryChangeSet?>.Success(revision.Value)
            : Result<LibraryChangeSet?>.Failure(revision.ErrorCode!, revision.ErrorMessage!);
    }

    private void PublishRevision(LibraryChangeSet? changeSet)
    {
        if (changeSet is not null)
        {
            _revisions!.PublishCommitted(changeSet);
        }
    }

    private static async Task InsertPagesAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        IReadOnlyList<Page> pages)
    {
        StringBuilder values = new();
        Dictionary<string, object?> parameters = new();
        for (int i = 0; i < pages.Count; i++)
        {
            Page page = pages[i];
            if (i > 0)
            {
                values.Append(',');
            }

            values.Append("(@p").Append(i).Append("_PageId,@p").Append(i).Append("_DocumentInstanceId,@p").Append(i)
                .Append("_PageIndex,@p").Append(i).Append("_PageLabel,@p").Append(i).Append("_Width,@p").Append(i)
                .Append("_Height,@p").Append(i).Append("_Rotation,@p").Append(i).Append("_CoordinateBasis,@p")
                .Append(i).Append("_BasisWidth,@p").Append(i).Append("_BasisHeight,@p").Append(i)
                .Append("_RendererBasisVersion,@p").Append(i).Append("_SourceFileHash,@p").Append(i)
                .Append("_CreatedAt,@p").Append(i).Append("_UpdatedAt)");
            string prefix = "p" + i + "_";
            parameters[prefix + "PageId"] = page.PageId.ToString();
            parameters[prefix + "DocumentInstanceId"] = page.DocumentInstanceId.ToString();
            parameters[prefix + "PageIndex"] = page.PageIndex;
            parameters[prefix + "PageLabel"] = page.PageLabel;
            parameters[prefix + "Width"] = page.Width;
            parameters[prefix + "Height"] = page.Height;
            parameters[prefix + "Rotation"] = page.Rotation;
            parameters[prefix + "CoordinateBasis"] = page.CoordinateBasis;
            parameters[prefix + "BasisWidth"] = page.BasisWidth;
            parameters[prefix + "BasisHeight"] = page.BasisHeight;
            parameters[prefix + "RendererBasisVersion"] = page.RendererBasisVersion;
            parameters[prefix + "SourceFileHash"] = page.SourceFileHash;
            parameters[prefix + "CreatedAt"] = FormatUtc(page.CreatedAt);
            parameters[prefix + "UpdatedAt"] = FormatUtc(page.UpdatedAt);
        }

        await connection.ExecuteAsync(
            "insert into pages (" +
            "page_id, document_instance_id, page_index, page_label, width, height, rotation, coordinate_basis, " +
            "basis_width, basis_height, renderer_basis_version, source_file_hash, created_at, updated_at) values " +
            values,
            parameters,
            transaction);
    }

    private static async Task InsertPagePlaceholderAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        PdfImportBatch batch,
        PdfImportPagePlaceholder placeholder)
    {
        Page page = batch.Pages[placeholder.PageIndex];
        DocumentTreeRevisionId revisionId = DocumentTreeRevisionId.New();
        await connection.ExecuteAsync(
            """
            insert into document_tree_revisions (
                tree_revision_id, document_instance_id, page_id, parent_tree_revision_id,
                source, status, is_current, edit_session_id, created_at, committed_at,
                reverted_from_tree_revision_id)
            values (@TreeRevisionId, @DocumentInstanceId, @PageId, @ParentTreeRevisionId,
                @Source, @Status, @IsCurrent, @EditSessionId, @CreatedAt, @CommittedAt,
                @RevertedFromTreeRevisionId);
            """,
            new
            {
                TreeRevisionId = revisionId.ToString(),
                DocumentInstanceId = batch.DocumentInstance.DocumentInstanceId.ToString(),
                PageId = page.PageId.ToString(),
                ParentTreeRevisionId = (string?)null,
                Source = DocumentTreeRevisionSource.Import,
                Status = DocumentTreeRevisionStatus.Committed,
                IsCurrent = 1,
                EditSessionId = (string?)null,
                CreatedAt = FormatUtc(batch.Now),
                CommittedAt = FormatUtc(batch.Now),
                RevertedFromTreeRevisionId = (string?)null
            },
            transaction);

        await MarkSearchStaleAsync(
            connection, transaction, batch.DocumentInstance.DocumentInstanceId, page.PageId, batch.Now);

        DocumentBox box = new(
            revisionId,
            DocumentBoxId.New(),
            batch.DocumentInstance.DocumentInstanceId,
            page.PageId,
            null,
            null,
            DocumentBoxType.LogicalPage,
            null,
            null,
            new NormalizedBBox(0, 0, 1, 1),
            new TextBoxPayload($"Import failed for this page: {placeholder.Reason}"),
            null,
            null,
            null,
            false,
            null);
        await connection.ExecuteAsync(
            """
            insert into document_boxes (
                tree_revision_id, box_id, document_instance_id, page_id, parent_box_id, next_sibling_box_id,
                box_type, sub_type, base_type, bbox_x, bbox_y, bbox_width, bbox_height, payload_json,
                heading_level, code_language, confidence, suppressed, continues_from_box_id
            )
            values (
                @TreeRevisionId, @BoxId, @DocumentInstanceId, @PageId, @ParentBoxId, @NextSiblingBoxId,
                @BoxType, @SubType, @BaseType, @BBoxX, @BBoxY, @BBoxWidth, @BBoxHeight, @PayloadJson,
                @HeadingLevel, @CodeLanguage, @Confidence, @Suppressed, @ContinuesFromBoxId
            );
            """,
            new
            {
                TreeRevisionId = box.TreeRevisionId.ToString(),
                BoxId = box.BoxId.ToString(),
                DocumentInstanceId = box.DocumentInstanceId.ToString(),
                PageId = box.PageId.ToString(),
                ParentBoxId = box.ParentBoxId?.ToString(),
                NextSiblingBoxId = box.NextSiblingBoxId?.ToString(),
                box.BoxType,
                box.SubType,
                box.BaseType,
                BBoxX = box.BBox.X,
                BBoxY = box.BBox.Y,
                BBoxWidth = box.BBox.Width,
                BBoxHeight = box.BBox.Height,
                PayloadJson = DocumentBoxPayloadSerializer.Serialize(box.Payload),
                box.HeadingLevel,
                box.CodeLanguage,
                box.Confidence,
                Suppressed = box.Suppressed ? 1 : 0,
                ContinuesFromBoxId = box.ContinuesFromBoxId?.ToString()
            },
            transaction);
    }

    private static Task MarkSearchStaleAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        DateTimeOffset now)
    {
        return connection.ExecuteAsync(
            """
            update search_units set status = 'stale', updated_at = @Now
            where document_instance_id = @DocumentInstanceId and page_id = @PageId and status = 'current';
            """,
            new
            {
                DocumentInstanceId = documentInstanceId.ToString(),
                PageId = pageId.ToString(),
                Now = FormatUtc(now)
            },
            transaction);
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O");
    }

    private sealed class FileAssetProbeRow
    {
        public string LibraryId { get; init; } = string.Empty;
    }
}
