using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.Documents;

/// <summary>
/// Resolves versioned evidence URIs directly against immutable committed document tree revisions.
/// A URI with an explicit revision id reads that exact revision; a URI without one reads the
/// current committed HEAD. Working revisions and legacy status rows are never externally readable.
/// </summary>
public sealed class VersionedEvidenceReader : IVersionedEvidenceReader
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ILibraryIdentityService _libraryIdentityService;
    private readonly IDocumentTreeService _trees;
    private readonly IDocumentMarkdownCompiler _markdownCompiler;

    public VersionedEvidenceReader(
        SqliteConnectionFactory connectionFactory,
        ILibraryIdentityService libraryIdentityService,
        IDocumentTreeService trees,
        IDocumentMarkdownCompiler markdownCompiler)
    {
        _connectionFactory = connectionFactory;
        _libraryIdentityService = libraryIdentityService;
        _trees = trees;
        _markdownCompiler = markdownCompiler;
    }

    public async Task<Result<EvidencePageText>> GetBoxTextAsync(
        DocumentInstanceId documentInstanceId,
        int pageIndex1Based,
        DocumentTreeRevisionId? revisionId = null,
        DocumentBoxId? boxId = null,
        CancellationToken cancellationToken = default)
    {
        Result<LibraryMetadata> libraryResult = await _libraryIdentityService.GetCurrentLibraryAsync(cancellationToken);
        if (libraryResult.IsFailure)
        {
            return Result<EvidencePageText>.Failure(libraryResult.ErrorCode!, libraryResult.ErrorMessage!);
        }

        string libraryId = libraryResult.Value.LibraryId.ToString();
        string documentId = documentInstanceId.ToString();

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);

            DocumentOwnerRow? owner = await connection.QuerySingleOrDefaultAsync<DocumentOwnerRow>(
                """
                select di.document_instance_id as DocumentInstanceId, di.title as Title, di.item_id as ItemId
                from document_instances di
                join items i on i.item_id = di.item_id
                where di.document_instance_id = @DocumentInstanceId
                  and i.library_id = @LibraryId;
                """,
                new { DocumentInstanceId = documentId, LibraryId = libraryId });
            if (owner is null)
            {
                return Result<EvidencePageText>.Failure(
                    AppErrorCodes.NotFound,
                    "Document instance was not found in the current library.");
            }

            if (pageIndex1Based < 1)
            {
                return Result<EvidencePageText>.Failure(
                    AppErrorCodes.ValidationFailed,
                    "Page index must be one-based.");
            }

            PageRow? page = await connection.QuerySingleOrDefaultAsync<PageRow>(
                """
                select page_id as PageId, page_label as PageLabel, page_index as PageIndex
                from pages
                where document_instance_id = @DocumentInstanceId and page_index = @PageIndex
                order by page_index
                limit 1;
                """,
                new { DocumentInstanceId = documentId, PageIndex = pageIndex1Based - 1 });
            if (page is null)
            {
                return Result<EvidencePageText>.Failure(
                    AppErrorCodes.NotFound,
                    "Physical page was not found for the document instance.");
            }

            string? resolvedRevisionId;
            if (revisionId is not null)
            {
                string requestedRevisionId = revisionId.Value.ToString();
                int committedBelongs = await connection.ExecuteScalarAsync<int>(
                    """
                    select count(1)
                    from document_tree_revisions
                    where tree_revision_id = @RevisionId
                      and document_instance_id = @DocumentInstanceId
                      and page_id = @PageId
                      and status = @Committed;
                    """,
                    new
                    {
                        RevisionId = requestedRevisionId,
                        DocumentInstanceId = documentId,
                        PageId = page.PageId,
                        Committed = DocumentTreeRevisionStatus.Committed
                    });
                if (committedBelongs == 0)
                {
                    return Result<EvidencePageText>.Failure(
                        AppErrorCodes.NotFound,
                        "Committed document tree revision was not found for the requested page.");
                }

                resolvedRevisionId = requestedRevisionId;
            }
            else
            {
                resolvedRevisionId = await connection.ExecuteScalarAsync<string?>(
                    """
                    select tree_revision_id
                    from document_tree_revisions
                    where document_instance_id = @DocumentInstanceId
                      and page_id = @PageId
                      and status = @Committed
                      and is_current = 1
                    order by committed_at desc, tree_revision_id desc
                    limit 1;
                    """,
                    new
                    {
                        DocumentInstanceId = documentId,
                        PageId = page.PageId,
                        Committed = DocumentTreeRevisionStatus.Committed
                    });
                if (resolvedRevisionId is null)
                {
                    return Result<EvidencePageText>.Failure(
                        AppErrorCodes.NotFound,
                        "Current committed document tree revision was not found for the page.");
                }
            }

            DocumentTreeRevisionId treeRevisionId = DocumentTreeRevisionId.Parse(resolvedRevisionId);
            string markdown;
            DocumentBoxId? resolvedBoxId = boxId;
            if (boxId is not null)
            {
                Result<IReadOnlyList<DocumentBox>> boxesResult = await _trees.ListBoxesAsync(
                    treeRevisionId, cancellationToken);
                if (boxesResult.IsFailure)
                {
                    return Result<EvidencePageText>.Failure(boxesResult.ErrorCode!, boxesResult.ErrorMessage!);
                }

                DocumentBox? box = boxesResult.Value.SingleOrDefault(b => b.BoxId == boxId.Value);
                if (box is null)
                {
                    return Result<EvidencePageText>.Failure(
                        AppErrorCodes.NotFound,
                        "Document box was not found in the specified revision.");
                }

                markdown = BoxMarkdown(box);
            }
            else
            {
                Result<CompiledMarkdown> compiled = await _markdownCompiler.CompilePageMarkdownAsync(
                    treeRevisionId, false, cancellationToken, false);
                if (compiled.IsFailure)
                {
                    return Result<EvidencePageText>.Failure(compiled.ErrorCode!, compiled.ErrorMessage!);
                }

                markdown = compiled.Value.Markdown;
            }

            return Result<EvidencePageText>.Success(new EvidencePageText(
                documentInstanceId,
                PageId.Parse(page.PageId),
                pageIndex1Based,
                page.PageLabel,
                owner.Title ?? string.Empty,
                treeRevisionId,
                resolvedBoxId,
                markdown));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                              exception,
                                              "infrastructure.versioned-evidence-reader"))
        {
            return Result<EvidencePageText>.Failure(
                AppErrorCodes.DatabaseError,
                $"Database operation failed: {exception.Message}");
        }
    }

    private static string BoxMarkdown(DocumentBox box)
    {
        return box.Payload switch
        {
            TextBoxPayload text when box.BoxType == DocumentBoxType.Title =>
                $"{new string('#', box.HeadingLevel ?? 1)} {text.Markdown.Trim()}",
            TextBoxPayload text => text.Markdown,
            EquationBoxPayload equation => $"$$\n{equation.Latex.Trim()}\n$$",
            ListBoxPayload list => list.Markdown,
            TableBoxPayload table => table.Markdown,
            CodeBoxPayload code => CompileCode(code.Code, box.CodeLanguage),
            MediaBoxPayload media => CompileMedia(box.BoxType, media),
            _ => string.Empty
        };
    }

    private static string CompileCode(string code, string? language)
    {
        int longestRun = LongestBacktickRun(code);
        string fence = new('`', Math.Max(3, longestRun + 1));
        return $"{fence}{language}\n{code.TrimEnd()}\n{fence}";
    }

    private static string CompileMedia(string boxType, MediaBoxPayload media)
    {
        string label = boxType == DocumentBoxType.Chart ? "Chart" : "Image";
        return string.IsNullOrWhiteSpace(media.Description)
            ? $"[{label}]"
            : $"[{label}: {media.Description.Trim()}]";
    }

    private static int LongestBacktickRun(string value)
    {
        int maximum = 0;
        int current = 0;
        foreach (char character in value)
        {
            if (character == '`')
            {
                maximum = Math.Max(maximum, ++current);
            }
            else
            {
                current = 0;
            }
        }

        return maximum;
    }

    private sealed class DocumentOwnerRow
    {
        public string DocumentInstanceId { get; set; } = string.Empty;
        public string? Title { get; set; }
        public string ItemId { get; set; } = string.Empty;
    }

    private sealed class PageRow
    {
        public string PageId { get; set; } = string.Empty;
        public string? PageLabel { get; set; }
        public int PageIndex { get; set; }
    }
}
