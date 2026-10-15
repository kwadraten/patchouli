using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.LibraryIdentity;

namespace Patchouli.Infrastructure.Documents.Translations;

/// <summary>
/// Reads and writes box-derived page translations. Stored rows always track the page's current
/// committed tree revision: reading a stale translation realigns it first, and writing replaces
/// the whole page atomically after validating the submitted markdown against the source.
/// </summary>
public sealed class PageTranslationService : IPageTranslationService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IDocumentTreeService _trees;
    private readonly IDocumentMarkdownCompiler _markdownCompiler;
    private readonly IPageTranslationCompiler _translationCompiler;
    private readonly TranslationStructureValidator _validator;
    private readonly TranslationRealigner _realigner;
    private readonly PageTranslationCache? _cache;
    private readonly IClock _clock;
    private readonly ILibraryRevisionService? _revisions;
    private readonly IMarkdownEngine _markdown;

    public PageTranslationService(
        SqliteConnectionFactory connectionFactory,
        IDocumentTreeService trees,
        IDocumentMarkdownCompiler markdownCompiler,
        IMarkdownEngine markdown,
        IPageTranslationCompiler translationCompiler,
        PageTranslationCache? cache = null,
        IClock? clock = null,
        ILibraryRevisionService? revisions = null)
    {
        _connectionFactory = connectionFactory;
        _trees = trees;
        _markdownCompiler = markdownCompiler;
        _translationCompiler = translationCompiler;
        _validator = new TranslationStructureValidator(markdown);
        _markdown = markdown;
        _realigner = new TranslationRealigner(clock ?? new SystemClock());
        _cache = cache;
        _clock = clock ?? new SystemClock();
        _revisions = revisions;
    }

    public async Task<Result<PageXmlTranslation>> GetPageXmlAsync(
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        int pageIndex,
        CancellationToken cancellationToken = default)
    {
        if (pageIndex <= 0)
        {
            return Result<PageXmlTranslation>.Failure(AppErrorCodes.ValidationFailed, "pageIndex must be one-based.");
        }

        Result<DocumentTreeRevision> revisionResult = await _trees.GetCurrentRevisionAsync(
            documentInstanceId, pageId, cancellationToken);
        if (revisionResult.IsFailure)
        {
            return Result<PageXmlTranslation>.Failure(revisionResult.ErrorCode!, revisionResult.ErrorMessage!);
        }

        DocumentTreeRevision revision = revisionResult.Value;
        Result<TranslationHeader?> headerResult = await RealignIfNeededAsync(revision, cancellationToken);
        if (headerResult.IsFailure)
        {
            return Result<PageXmlTranslation>.Failure(headerResult.ErrorCode!, headerResult.ErrorMessage!);
        }

        Result<IReadOnlyList<DocumentBox>> boxesResult = await _trees.ListBoxesAsync(
            revision.TreeRevisionId, cancellationToken);
        if (boxesResult.IsFailure)
        {
            return Result<PageXmlTranslation>.Failure(boxesResult.ErrorCode!, boxesResult.ErrorMessage!);
        }

        await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction readTransaction = connection.BeginTransaction(true);
        string? head = await connection.QuerySingleOrDefaultAsync<string>(
            "select tree_revision_id from document_tree_revisions where document_instance_id = @DocumentId and page_id = @PageId and status = 'committed' and is_current = 1;",
            new { DocumentId = documentInstanceId.ToString(), PageId = pageId.ToString() }, readTransaction);
        if (!string.Equals(head, revision.TreeRevisionId.ToString(), StringComparison.Ordinal))
        {
            return Result<PageXmlTranslation>.Failure(
                AppErrorCodes.Conflict, "The source page revision changed while reading the XML translation.");
        }

        TranslationHeader? header = await connection.QuerySingleOrDefaultAsync<TranslationHeader>(
            "select source_tree_revision_id as SourceTreeRevisionId, version as Version from page_translations where page_id = @PageId;",
            new { PageId = pageId.ToString() }, readTransaction);
        if (header is not null && !string.Equals(
                header.SourceTreeRevisionId, revision.TreeRevisionId.ToString(), StringComparison.Ordinal))
        {
            return Result<PageXmlTranslation>.Failure(
                AppErrorCodes.Conflict, "The page translations changed while reading the XML translation.");
        }

        Dictionary<DocumentBoxId, string> translations = (await connection.QueryAsync<TranslationBoxRow>(
                "select box_id as BoxId, translated_md as TranslatedMd from translation_boxes where page_id = @PageId;",
                new { PageId = pageId.ToString() }, readTransaction))
            .ToDictionary(row => DocumentBoxId.Parse(row.BoxId), row => row.TranslatedMd);
        await readTransaction.CommitAsync(cancellationToken);
        int version = header?.Version ?? 0;
        IReadOnlyList<DocumentBox> boxes = boxesResult.Value;
        string xml = PageXmlCodec.EncodePage(
            documentInstanceId, pageIndex, revision.TreeRevisionId, boxes, translations, version);
        PageTranslationStatus status = BuildStatus(
            boxes, translations, revision.TreeRevisionId, true, version);
        return Result<PageXmlTranslation>.Success(new PageXmlTranslation(xml, status));
    }

    public async Task<Result<PageTranslationStatus>> PutPageXmlAsync(
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        int pageIndex,
        string xml,
        CancellationToken cancellationToken = default)
    {
        if (pageIndex <= 0)
        {
            return Result<PageTranslationStatus>.Failure(AppErrorCodes.ValidationFailed,
                "pageIndex must be one-based.");
        }

        Result<DocumentTreeRevision> revisionResult = await _trees.GetCurrentRevisionAsync(
            documentInstanceId, pageId, cancellationToken);
        if (revisionResult.IsFailure)
        {
            return Result<PageTranslationStatus>.Failure(revisionResult.ErrorCode!, revisionResult.ErrorMessage!);
        }

        DocumentTreeRevision revision = revisionResult.Value;
        Result<IReadOnlyList<DocumentBox>> boxesResult = await _trees.ListBoxesAsync(
            revision.TreeRevisionId, cancellationToken);
        if (boxesResult.IsFailure)
        {
            return Result<PageTranslationStatus>.Failure(boxesResult.ErrorCode!, boxesResult.ErrorMessage!);
        }

        Result<PageXmlDocument> decoded = PageXmlCodec.DecodePage(
            xml, documentInstanceId, pageIndex, revision.TreeRevisionId, boxesResult.Value);
        if (decoded.IsFailure)
        {
            return Result<PageTranslationStatus>.Failure(decoded.ErrorCode!, decoded.ErrorMessage!);
        }

        Dictionary<DocumentBoxId, DocumentBox> boxesById = boxesResult.Value.ToDictionary(box => box.BoxId);
        Dictionary<DocumentBoxId, string> translated = [];
        foreach ((DocumentBoxId boxId, DocumentBoxPayload payload) in decoded.Value.TranslatedPayloads)
        {
            Result<string> validated = ValidateBoxReplacement(boxesById[boxId], payload);
            if (validated.IsFailure)
            {
                return Result<PageTranslationStatus>.Failure(
                    validated.ErrorCode!, validated.ErrorMessage!, details: validated.Details);
            }

            translated.Add(boxId, validated.Value);
        }

        Result<PageTranslationStatus> persisted = await InTransactionAsync(async (connection, transaction) =>
        {
            string pageIdValue = pageId.ToString();
            string? head = await connection.QuerySingleOrDefaultAsync<string>(
                "select tree_revision_id from document_tree_revisions where document_instance_id = @DocumentId and page_id = @PageId and status = 'committed' and is_current = 1;",
                new { DocumentId = documentInstanceId.ToString(), PageId = pageIdValue }, transaction);
            if (!string.Equals(head, revision.TreeRevisionId.ToString(), StringComparison.Ordinal))
            {
                return Result<PageTranslationStatus>.Failure(AppErrorCodes.Conflict,
                    "The source page revision changed while applying the XML translation.");
            }

            TranslationHeader? storedHeader = await connection.QuerySingleOrDefaultAsync<TranslationHeader>(
                "select source_tree_revision_id as SourceTreeRevisionId, version as Version from page_translations where page_id = @PageId;",
                new { PageId = pageIdValue }, transaction);
            int expectedVersion = storedHeader?.Version ?? 0;
            if (expectedVersion != decoded.Value.TranslationVersion)
            {
                return Result<PageTranslationStatus>.Failure(AppErrorCodes.Conflict,
                    "The translation version changed while applying the XML translation.");
            }

            int currentVersion = expectedVersion;
            if (storedHeader is not null && !string.Equals(
                    storedHeader.SourceTreeRevisionId, revision.TreeRevisionId.ToString(), StringComparison.Ordinal))
            {
                Result<TranslationHeader> realigned = await _realigner.RealignAsync(
                    connection, transaction, pageId, revision.TreeRevisionId, boxesResult.Value);
                if (realigned.IsFailure)
                {
                    return Result<PageTranslationStatus>.Failure(realigned.ErrorCode!, realigned.ErrorMessage!);
                }

                currentVersion = realigned.Value.Version;
            }

            int nextVersion = currentVersion + 1;
            await connection.ExecuteAsync("delete from translation_boxes where page_id = @PageId;",
                new { PageId = pageIdValue }, transaction);
            await connection.ExecuteAsync("delete from page_translations where page_id = @PageId;",
                new { PageId = pageIdValue }, transaction);
            await connection.ExecuteAsync(
                "insert into page_translations(page_id, source_tree_revision_id, version, updated_at) values (@PageId, @RevisionId, @Version, @UpdatedAt);",
                new
                {
                    PageId = pageIdValue, RevisionId = revision.TreeRevisionId.ToString(), Version = nextVersion,
                    UpdatedAt = FormatUtc(_clock.UtcNow)
                }, transaction);

            int ordinal = 0;
            foreach ((DocumentBoxId boxId, string value) in translated)
            {
                DocumentBox box = boxesById[boxId];
                await connection.ExecuteAsync(
                    "insert into translation_boxes(page_id, box_id, ordinal, translated_md, source_hash) values (@PageId, @BoxId, @Ordinal, @TranslatedMd, @SourceHash);",
                    new
                    {
                        PageId = pageIdValue, BoxId = boxId.ToString(), Ordinal = ordinal++, TranslatedMd = value,
                        SourceHash = TranslationRealigner.ComputeSourceHash(box)
                    },
                    transaction);
            }

            return Result<PageTranslationStatus>.Success(BuildStatus(
                boxesResult.Value, translated, revision.TreeRevisionId, true, nextVersion));
        }, cancellationToken, TranslationChange(documentInstanceId, pageId));

        if (persisted.IsSuccess)
        {
            _cache?.Invalidate(pageId);
        }

        return persisted;
    }

    public async Task<Result<PageTranslationStatus>> PutBoxPayloadsAsync(
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        DocumentTreeRevisionId expectedRevision,
        int expectedTranslationVersion,
        IReadOnlyDictionary<DocumentBoxId, DocumentBoxPayload> replacements,
        CancellationToken cancellationToken = default)
    {
        if (replacements is null || replacements.Count is < 1 or > 100)
        {
            return Result<PageTranslationStatus>.Failure(
                AppErrorCodes.ValidationFailed, "Provide between 1 and 100 box payload replacements.");
        }

        if (expectedTranslationVersion < 0)
        {
            return Result<PageTranslationStatus>.Failure(
                AppErrorCodes.ValidationFailed, "expectedTranslationVersion cannot be negative.");
        }

        Result<IReadOnlyList<DocumentBox>> boxesResult = await _trees.ListBoxesAsync(
            expectedRevision, cancellationToken);
        if (boxesResult.IsFailure)
        {
            return Result<PageTranslationStatus>.Failure(boxesResult.ErrorCode!, boxesResult.ErrorMessage!);
        }

        IReadOnlyList<DocumentBox> boxes = boxesResult.Value;
        Dictionary<DocumentBoxId, DocumentBox> boxesById = boxes.ToDictionary(box => box.BoxId);
        Result<PageTranslationStatus> persisted = await InTransactionAsync(async (connection, transaction) =>
        {
            string pageIdValue = pageId.ToString();
            string? head = await connection.QuerySingleOrDefaultAsync<string>(
                "select tree_revision_id from document_tree_revisions where document_instance_id = @DocumentId and page_id = @PageId and status = 'committed' and is_current = 1;",
                new { DocumentId = documentInstanceId.ToString(), PageId = pageIdValue }, transaction);
            if (!string.Equals(head, expectedRevision.ToString(), StringComparison.Ordinal))
            {
                return Result<PageTranslationStatus>.Failure(
                    AppErrorCodes.Conflict,
                    "The source page revision changed before the box translations were applied.");
            }

            TranslationHeader? header = await connection.QuerySingleOrDefaultAsync<TranslationHeader>(
                "select source_tree_revision_id as SourceTreeRevisionId, version as Version from page_translations where page_id = @PageId;",
                new { PageId = pageIdValue }, transaction);
            int expectedStoredVersion = header?.Version ?? 0;
            if (expectedStoredVersion != expectedTranslationVersion)
            {
                return Result<PageTranslationStatus>.Failure(
                    AppErrorCodes.Conflict,
                    "The page translation version changed before the box translations were applied.");
            }

            int currentVersion = expectedStoredVersion;
            if (header is not null && !string.Equals(
                    header.SourceTreeRevisionId, expectedRevision.ToString(), StringComparison.Ordinal))
            {
                Result<TranslationHeader> realigned = await _realigner.RealignAsync(
                    connection, transaction, pageId, expectedRevision, boxes);
                if (realigned.IsFailure)
                {
                    return Result<PageTranslationStatus>.Failure(realigned.ErrorCode!, realigned.ErrorMessage!);
                }

                currentVersion = realigned.Value.Version;
            }

            int nextVersion = currentVersion + 1;
            if (header is null)
            {
                await connection.ExecuteAsync(
                    "insert into page_translations(page_id, source_tree_revision_id, version, updated_at) values (@PageId, @RevisionId, @Version, @UpdatedAt);",
                    new
                    {
                        PageId = pageIdValue,
                        RevisionId = expectedRevision.ToString(),
                        Version = nextVersion,
                        UpdatedAt = FormatUtc(_clock.UtcNow)
                    }, transaction);
            }

            foreach ((DocumentBoxId boxId, DocumentBoxPayload payload) in replacements)
            {
                if (!boxesById.TryGetValue(boxId, out DocumentBox? source))
                {
                    return PageWriteBoxFailure(boxId, "The box does not belong to the source page.");
                }

                if (DocumentBoxProjection.Siblings(boxes, boxId).Any())
                {
                    return PageWriteBoxFailure(boxId, "The box has children and cannot be translated as a leaf.");
                }

                Result<string> validated = ValidateBoxReplacement(source, payload);
                if (validated.IsFailure)
                {
                    return Result<PageTranslationStatus>.Failure(
                        validated.ErrorCode!, validated.ErrorMessage!, details: validated.Details);
                }

                string? existingOrdinal = await connection.QuerySingleOrDefaultAsync<string>(
                    "select cast(ordinal as text) from translation_boxes where page_id = @PageId and box_id = @BoxId;",
                    new { PageId = pageIdValue, BoxId = boxId.ToString() }, transaction);
                int ordinal;
                if (existingOrdinal is not null && int.TryParse(existingOrdinal, out int currentOrdinal))
                {
                    ordinal = currentOrdinal;
                }
                else
                {
                    ordinal = await connection.ExecuteScalarAsync<int>(
                        "select coalesce(max(ordinal), -1) + 1 from translation_boxes where page_id = @PageId;",
                        new { PageId = pageIdValue }, transaction);
                }

                await connection.ExecuteAsync(
                    "insert into translation_boxes(page_id, box_id, ordinal, translated_md, source_hash) values (@PageId, @BoxId, @Ordinal, @TranslatedMd, @SourceHash) on conflict(page_id, box_id) do update set ordinal = excluded.ordinal, translated_md = excluded.translated_md, source_hash = excluded.source_hash;",
                    new
                    {
                        PageId = pageIdValue,
                        BoxId = boxId.ToString(),
                        Ordinal = ordinal,
                        TranslatedMd = validated.Value,
                        SourceHash = TranslationRealigner.ComputeSourceHash(source)
                    }, transaction);
            }

            await connection.ExecuteAsync(
                "update page_translations set source_tree_revision_id = @RevisionId, version = @Version, updated_at = @UpdatedAt where page_id = @PageId;",
                new
                {
                    PageId = pageIdValue,
                    RevisionId = expectedRevision.ToString(),
                    Version = nextVersion,
                    UpdatedAt = FormatUtc(_clock.UtcNow)
                }, transaction);

            Dictionary<DocumentBoxId, string> currentTranslations = (await connection.QueryAsync<TranslationBoxRow>(
                    "select box_id as BoxId, translated_md as TranslatedMd from translation_boxes where page_id = @PageId;",
                    new { PageId = pageIdValue }, transaction))
                .ToDictionary(row => DocumentBoxId.Parse(row.BoxId), row => row.TranslatedMd);
            return Result<PageTranslationStatus>.Success(
                BuildStatus(boxes, currentTranslations, expectedRevision, true, nextVersion));
        }, cancellationToken, TranslationChange(documentInstanceId, pageId));

        if (persisted.IsSuccess)
        {
            _cache?.Invalidate(pageId);
        }

        return persisted;
    }

    private Result<string> ValidateBoxReplacement(DocumentBox source, DocumentBoxPayload replacement)
    {
        if (source.BoxType == DocumentBoxType.LogicalPage)
        {
            return BoxPayloadFailure(source.BoxId, "Logical-page boxes cannot be translated as leaf payloads.");
        }

        if (replacement is null || source.Payload is null || source.Payload.GetType() != replacement.GetType())
        {
            return BoxPayloadFailure(source.BoxId, "The replacement payload type must match the source payload type.");
        }

        string validationType = DocumentBoxType.IsKnown(source.BoxType)
            ? source.BoxType
            : source.BaseType switch
            {
                "image" => DocumentBoxType.Image,
                "table" => DocumentBoxType.Table,
                "code" => DocumentBoxType.Code,
                _ => DocumentBoxType.Text
            };
        Result leafValidation = _markdown.ValidateLeaf(validationType, replacement);
        if (leafValidation.IsFailure)
        {
            return BoxPayloadFailure(source.BoxId, leafValidation.ErrorMessage ?? "The translated payload is invalid.");
        }

        if (source.Payload is TableBoxPayload sourceTable && replacement is TableBoxPayload replacementTable &&
            !string.Equals(sourceTable.Html, replacementTable.Html, StringComparison.Ordinal))
        {
            return BoxPayloadFailure(source.BoxId, "The replacement cannot change diagnostic table HTML.");
        }

        if (source.Payload is MediaBoxPayload sourceMedia && replacement is MediaBoxPayload replacementMedia &&
            !string.Equals(sourceMedia.AssetId, replacementMedia.AssetId, StringComparison.Ordinal))
        {
            return BoxPayloadFailure(source.BoxId, "The replacement cannot change its media asset id.");
        }

        DocumentBox renderSource = source with { ParentBoxId = null, NextSiblingBoxId = null };
        DocumentBox changed = renderSource with { Payload = replacement };
        CompiledMarkdown original = DocumentMarkdownRenderer.Render([renderSource], _markdown, true, false, null);
        string fragment = DocumentMarkdownRenderer.CompileBoxFragment(changed, false) ?? string.Empty;
        TranslationValidationResult validation = _validator.Validate(original, fragment, [renderSource]);
        if (!validation.IsValid)
        {
            return BoxPayloadFailure(source.BoxId, "The translated payload changes its markdown structure.");
        }

        if (!validation.BoxFragments.TryGetValue(source.BoxId, out string? storage))
        {
            return BoxPayloadFailure(source.BoxId, "The translation has no valid payload fragment.");
        }

        return Result<string>.Success(storage);
    }

    private static Result<string> BoxPayloadFailure(DocumentBoxId boxId, string message)
    {
        BoxContentError error = new(boxId.ToString(), message);
        return Result<string>.Failure(
            AppErrorCodes.ValidationFailed,
            $"Box {boxId}: {message}",
            details: new BoxContentFailureDetails([error]));
    }

    private static Result<PageTranslationStatus> PageWriteBoxFailure(DocumentBoxId boxId, string message)
    {
        BoxContentError error = new(boxId.ToString(), message);
        return Result<PageTranslationStatus>.Failure(
            AppErrorCodes.ValidationFailed,
            $"Box {boxId}: {message}",
            details: new BoxContentFailureDetails([error]));
    }

    private static string PayloadText(DocumentBoxPayload payload)
    {
        return payload switch
        {
            TextBoxPayload text => text.Markdown,
            ListBoxPayload list => list.Markdown,
            TableBoxPayload table => table.Markdown,
            EquationBoxPayload equation => equation.Latex,
            CodeBoxPayload code => code.Code,
            MediaBoxPayload media => media.Description ?? string.Empty,
            _ => string.Empty
        };
    }

    private static LibraryChangeSet TranslationChange(DocumentInstanceId documentInstanceId, PageId pageId)
    {
        return LibraryChangeSet.Empty with
        {
            DocumentInstanceIds = [documentInstanceId],
            PageIds = [pageId]
        };
    }

    public async Task<TranslatedPageMarkdown?> GetPageTranslationAsync(
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Result<DocumentTreeRevision> revisionResult = await _trees.GetCurrentRevisionAsync(
                documentInstanceId, pageId, cancellationToken);
            if (revisionResult.IsFailure)
            {
                return null;
            }

            DocumentTreeRevision revision = revisionResult.Value;
            Result<TranslationHeader?> headerResult = await RealignIfNeededAsync(revision, cancellationToken);
            if (headerResult.IsFailure || headerResult.Value is null)
            {
                return null;
            }

            Result<TranslatedPageMarkdown> compiled = await _translationCompiler.CompilePageTranslationAsync(
                pageId, revision.TreeRevisionId, headerResult.Value.Version, cancellationToken);
            return compiled.IsSuccess ? compiled.Value : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                              exception,
                                              "infrastructure.page-translation-service"))
        {
            return null;
        }
    }

    public async Task<Result<PageTranslationStatus>> PutPageTranslationAsync(
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        string markdown,
        CancellationToken cancellationToken = default)
    {
        Result<DocumentTreeRevision> revisionResult = await _trees.GetCurrentRevisionAsync(
            documentInstanceId, pageId, cancellationToken);
        if (revisionResult.IsFailure)
        {
            return Result<PageTranslationStatus>.Failure(
                revisionResult.ErrorCode!, revisionResult.ErrorMessage!);
        }

        DocumentTreeRevision revision = revisionResult.Value;
        Result<IReadOnlyList<DocumentBox>> boxesResult = await _trees.ListBoxesAsync(
            revision.TreeRevisionId, cancellationToken);
        if (boxesResult.IsFailure)
        {
            return Result<PageTranslationStatus>.Failure(boxesResult.ErrorCode!, boxesResult.ErrorMessage!);
        }

        Result<CompiledMarkdown> compiledResult = await _markdownCompiler.CompilePageMarkdownAsync(
            revision.TreeRevisionId, false, cancellationToken);
        if (compiledResult.IsFailure)
        {
            return Result<PageTranslationStatus>.Failure(compiledResult.ErrorCode!, compiledResult.ErrorMessage!);
        }

        IReadOnlyList<DocumentBox> boxes = boxesResult.Value;
        TranslationValidationResult validation = _validator.Validate(
            compiledResult.Value, markdown ?? string.Empty, boxes);
        if (!validation.IsValid)
        {
            return Result<PageTranslationStatus>.Failure(
                AppErrorCodes.ValidationFailed,
                $"Translated markdown does not match the page structure ({validation.Errors.Count} mismatch(es)).",
                details: new TranslationStructureFailureDetails(validation.Errors));
        }

        Result<PageTranslationStatus> persisted = await InTransactionAsync(async (connection, transaction) =>
        {
            string pageIdValue = pageId.ToString();
            string? head = await connection.QuerySingleOrDefaultAsync<string>(
                "select tree_revision_id from document_tree_revisions where document_instance_id = @DocumentId and page_id = @PageId and status = 'committed' and is_current = 1;",
                new { DocumentId = documentInstanceId.ToString(), PageId = pageIdValue }, transaction);
            if (!string.Equals(head, revision.TreeRevisionId.ToString(), StringComparison.Ordinal))
            {
                return Result<PageTranslationStatus>.Failure(
                    AppErrorCodes.Conflict, "The source page revision changed while applying the translation.");
            }

            int version = (await connection.ExecuteScalarAsync<int?>(
                "select version from page_translations where page_id = @PageId;",
                new { PageId = pageIdValue },
                transaction) ?? 0) + 1;

            await connection.ExecuteAsync(
                "delete from translation_boxes where page_id = @PageId;",
                new { PageId = pageIdValue },
                transaction);
            await connection.ExecuteAsync(
                "delete from page_translations where page_id = @PageId;",
                new { PageId = pageIdValue },
                transaction);
            await connection.ExecuteAsync(
                """
                insert into page_translations(page_id, source_tree_revision_id, version, updated_at)
                values (@PageId, @RevisionId, @Version, @UpdatedAt);
                """,
                new
                {
                    PageId = pageIdValue,
                    RevisionId = revision.TreeRevisionId.ToString(),
                    Version = version,
                    UpdatedAt = FormatUtc(_clock.UtcNow)
                },
                transaction);

            int ordinal = 0;
            foreach (DocumentBox box in DocumentBoxProjection.ContentBoxes(boxes))
            {
                if (!validation.BoxFragments.TryGetValue(box.BoxId, out string? fragment))
                {
                    continue;
                }

                await connection.ExecuteAsync(
                    """
                    insert into translation_boxes(page_id, box_id, ordinal, translated_md, source_hash)
                    values (@PageId, @BoxId, @Ordinal, @TranslatedMd, @SourceHash);
                    """,
                    new
                    {
                        PageId = pageIdValue,
                        BoxId = box.BoxId.ToString(),
                        Ordinal = ordinal,
                        TranslatedMd = fragment,
                        SourceHash = TranslationRealigner.ComputeSourceHash(box)
                    },
                    transaction);
                ordinal++;
            }

            return Result<PageTranslationStatus>.Success(BuildStatus(
                boxes, validation.BoxFragments, revision.TreeRevisionId, true, version));
        }, cancellationToken, TranslationChange(documentInstanceId, pageId));

        if (persisted.IsSuccess)
        {
            _cache?.Invalidate(pageId);
        }

        return persisted;
    }

    private async Task<Result<TranslationHeader?>> RealignIfNeededAsync(
        DocumentTreeRevision revision,
        CancellationToken cancellationToken)
    {
        TranslationHeader? header = await LoadHeaderAsync(revision.PageId, cancellationToken);
        if (header is null)
        {
            return Result<TranslationHeader?>.Success(null);
        }

        if (string.Equals(header.SourceTreeRevisionId, revision.TreeRevisionId.ToString(), StringComparison.Ordinal))
        {
            return Result<TranslationHeader?>.Success(header);
        }

        Result<TranslationHeader?> realigned = await InTransactionAsync(async (connection, transaction) =>
        {
            string? head = await connection.QuerySingleOrDefaultAsync<string>(
                "select tree_revision_id from document_tree_revisions where document_instance_id = @DocumentId and page_id = @PageId and status = 'committed' and is_current = 1;",
                new
                {
                    DocumentId = revision.DocumentInstanceId.ToString(),
                    PageId = revision.PageId.ToString()
                }, transaction);
            if (!string.Equals(head, revision.TreeRevisionId.ToString(), StringComparison.Ordinal))
            {
                return Result<TranslationHeader?>.Failure(
                    AppErrorCodes.Conflict, "The source page revision changed before translations could be realigned.");
            }

            TranslationHeader? currentHeader = await connection.QuerySingleOrDefaultAsync<TranslationHeader>(
                "select source_tree_revision_id as SourceTreeRevisionId, version as Version from page_translations where page_id = @PageId;",
                new { PageId = revision.PageId.ToString() }, transaction);
            if (currentHeader is null || string.Equals(
                    currentHeader.SourceTreeRevisionId, revision.TreeRevisionId.ToString(), StringComparison.Ordinal))
            {
                return Result<TranslationHeader?>.Success(currentHeader);
            }

            Result<IReadOnlyList<DocumentBox>> boxesResult = await _trees.ListBoxesAsync(
                revision.TreeRevisionId, cancellationToken);
            if (boxesResult.IsFailure)
            {
                return Result<TranslationHeader?>.Failure(boxesResult.ErrorCode!, boxesResult.ErrorMessage!);
            }

            Result<TranslationHeader> result = await _realigner.RealignAsync(
                connection, transaction, revision.PageId, revision.TreeRevisionId, boxesResult.Value);
            return result.IsFailure
                ? Result<TranslationHeader?>.Failure(result.ErrorCode!, result.ErrorMessage!)
                : Result<TranslationHeader?>.Success(result.Value);
        }, cancellationToken);

        if (realigned.IsSuccess)
        {
            _cache?.Invalidate(revision.PageId);
        }

        return realigned;
    }

    private async Task<TranslationHeader?> LoadHeaderAsync(PageId pageId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
        await connection.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<TranslationHeader>(
            """
            select source_tree_revision_id as SourceTreeRevisionId, version as Version
            from page_translations
            where page_id = @PageId;
            """,
            new { PageId = pageId.ToString() });
    }

    private static PageTranslationStatus BuildStatus(
        IReadOnlyList<DocumentBox> boxes,
        IReadOnlyDictionary<DocumentBoxId, string> fragments,
        DocumentTreeRevisionId treeRevisionId,
        bool isCurrent,
        int version = 0)
    {
        DocumentBox[] contentBoxes = DocumentBoxProjection.ContentBoxes(boxes).ToArray();
        List<DocumentBoxId> staleBoxIds = [];
        int translatedBoxCount = 0;
        foreach (DocumentBox box in contentBoxes)
        {
            if (fragments.ContainsKey(box.BoxId))
            {
                translatedBoxCount++;
            }
            else
            {
                staleBoxIds.Add(box.BoxId);
            }
        }

        return new PageTranslationStatus(
            translatedBoxCount, contentBoxes.Length, staleBoxIds, treeRevisionId, isCurrent, version);
    }

    private async Task<Result<T>> InTransactionAsync<T>(
        Func<SqliteConnection, DbTransaction, Task<Result<T>>> action,
        CancellationToken cancellationToken,
        LibraryChangeSet? changeSet = null)
    {
        try
        {
            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            Result<T> result = await action(connection, transaction);
            if (result.IsFailure)
            {
                await transaction.RollbackAsync(cancellationToken);
                return result;
            }

            LibraryChangeSet? committedChanges = null;
            if (changeSet is not null && _revisions is not null)
            {
                Result<LibraryChangeSet> incremented = await _revisions.IncrementInTransactionAsync(
                    connection, transaction, changeSet, cancellationToken);
                if (incremented.IsFailure)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result<T>.Failure(incremented.ErrorCode!, incremented.ErrorMessage!);
                }

                committedChanges = incremented.Value;
            }

            await transaction.CommitAsync(cancellationToken);
            if (committedChanges is not null)
            {
                _revisions!.PublishCommitted(committedChanges);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                              exception,
                                              "infrastructure.page-translation-service"))
        {
            return Result<T>.Failure(
                AppErrorCodes.DatabaseError,
                $"Database operation failed: {exception.Message}");
        }
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O");
    }

    private sealed class TranslationBoxRow
    {
        public string BoxId { get; set; } = string.Empty;
        public string TranslatedMd { get; set; } = string.Empty;
    }
}
