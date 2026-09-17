using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;

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

    public PageTranslationService(
        SqliteConnectionFactory connectionFactory,
        IDocumentTreeService trees,
        IDocumentMarkdownCompiler markdownCompiler,
        IMarkdownEngine markdown,
        IPageTranslationCompiler translationCompiler,
        PageTranslationCache? cache = null,
        IClock? clock = null)
    {
        _connectionFactory = connectionFactory;
        _trees = trees;
        _markdownCompiler = markdownCompiler;
        _translationCompiler = translationCompiler;
        _validator = new TranslationStructureValidator(markdown);
        _realigner = new TranslationRealigner(clock ?? new SystemClock());
        _cache = cache;
        _clock = clock ?? new SystemClock();
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
                boxes, validation.BoxFragments, revision.TreeRevisionId, true));
        }, cancellationToken);

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
        bool isCurrent)
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
            translatedBoxCount, contentBoxes.Length, staleBoxIds, treeRevisionId, isCurrent);
    }

    private async Task<Result<T>> InTransactionAsync<T>(
        Func<SqliteConnection, DbTransaction, Task<Result<T>>> action,
        CancellationToken cancellationToken)
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

            await transaction.CommitAsync(cancellationToken);
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
}
