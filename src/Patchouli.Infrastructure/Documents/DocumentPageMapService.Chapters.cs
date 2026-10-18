using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Pagination;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Documents;

public sealed partial class DocumentPageMapService
{
    public async Task<Result<ChapterEditingSnapshot>> GetChapterEditingSnapshotAsync(
        DocumentInstanceId documentInstanceId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            // A deferred transaction provides a consistent snapshot without requesting a write lock
            // on the read-only connection.
            await using DbTransaction transaction = connection.BeginTransaction(true);
            if (!await ChapterDocumentExistsAsync(connection, transaction, documentInstanceId, cancellationToken))
            {
                return Result<ChapterEditingSnapshot>.Failure(AppErrorCodes.NotFound,
                    "Document instance was not found.");
            }

            PageMapRow? row =
                await ReadChapterMapRowAsync(connection, transaction, documentInstanceId, cancellationToken);
            return Result<ChapterEditingSnapshot>.Success(ToEditingSnapshot(documentInstanceId, row));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.chapter-editing"))
        {
            return Result<ChapterEditingSnapshot>.Failure(AppErrorCodes.DatabaseError, ex.Message);
        }
    }

    public async Task<Result<ChapterEditingSnapshot>> SaveChaptersAsync(
        DocumentInstanceId documentInstanceId, ChapterSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        // Freeze caller-owned lists before awaiting so the exact validated tree is persisted.
        if (request is null || request.Chapters is null)
        {
            return Result<ChapterEditingSnapshot>.Failure(AppErrorCodes.ValidationFailed, "章节保存请求不能为空。");
        }

        Result<IReadOnlyList<ChapterNode>> frozen = FreezeChapterTree(request.Chapters);
        if (frozen.IsFailure)
        {
            return Result<ChapterEditingSnapshot>.Failure(frozen.ErrorCode!, frozen.ErrorMessage!);
        }

        ChapterPageRevisionExpectation[]? expectedPages = request.ExpectedPageRevisions?.ToArray();
        try
        {
            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = _connectionFactory.CreateWriteConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            if (!await ChapterDocumentExistsAsync(connection, transaction, documentInstanceId, cancellationToken))
            {
                return Result<ChapterEditingSnapshot>.Failure(AppErrorCodes.NotFound,
                    "Document instance was not found.");
            }

            PageMapRow? row =
                await ReadChapterMapRowAsync(connection, transaction, documentInstanceId, cancellationToken);
            ChapterEditingSnapshot current = ToEditingSnapshot(documentInstanceId, row);
            if (!string.Equals(current.VersionToken, request.ExpectedVersionToken, StringComparison.Ordinal))
            {
                return Result<ChapterEditingSnapshot>.Failure(AppErrorCodes.Conflict,
                    "章节映射已被其他操作修改，请重新加载后再保存。");
            }

            ChapterSourcePage[] pages = (await connection.QueryAsync<ChapterSourcePage>(new CommandDefinition(
                """
                select p.page_id as PageId, p.page_index + 1 as PdfPage, r.tree_revision_id as TreeRevisionId
                from pages p left join document_tree_revisions r
                  on r.page_id = p.page_id and r.document_instance_id = p.document_instance_id
                 and r.is_current = 1 and r.status = 'committed'
                where p.document_instance_id = @DocumentInstanceId order by p.page_index;
                """, new { DocumentInstanceId = documentInstanceId.ToString() }, transaction,
                cancellationToken: cancellationToken))).ToArray();
            if (expectedPages is not null && !PageExpectationsMatch(expectedPages, pages))
            {
                return Result<ChapterEditingSnapshot>.Failure(AppErrorCodes.Conflict,
                    "扫描来源页面或 OCR 版本已变化，请重新扫描后再应用。");
            }

            // Shape validation concerns chapters only: unrelated stale pagination stays untouched.
            DocumentPageMap chaptersOnly = DocumentPageMap.Empty(documentInstanceId) with { Chapters = frozen.Value };
            IReadOnlyList<PageMapValidationError> errors = PageMapValidator.Validate(chaptersOnly);
            if (errors.Count > 0)
            {
                return Result<ChapterEditingSnapshot>.Failure(AppErrorCodes.ValidationFailed,
                    string.Join("; ", errors.Select(error => error.Message)));
            }

            Dictionary<string, NavigationPosition> oldTargets = FlattenChapters(current.Definition.Chapters)
                .ToDictionary(node => node.ChapterId, node => node.Target, StringComparer.Ordinal);
            foreach (ChapterNode chapter in FlattenChapters(frozen.Value))
            {
                if (oldTargets.TryGetValue(chapter.ChapterId, out NavigationPosition? target) &&
                    target == chapter.Target)
                {
                    continue;
                }

                ChapterSourcePage? page =
                    pages.SingleOrDefault(candidate => candidate.PdfPage == chapter.Target.PdfPage);
                if (page is null)
                {
                    return Result<ChapterEditingSnapshot>.Failure(AppErrorCodes.ValidationFailed,
                        $"Chapter '{chapter.Title}' targets PDF page {chapter.Target.PdfPage}, beyond the document page count ({pages.Length}).");
                }

                if (chapter.Target.LogicalPage is { } logicalPage)
                {
                    int found = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                        """
                        select count(1) from document_boxes
                        where tree_revision_id = @RevisionId and document_instance_id = @DocumentInstanceId
                          and page_id = @PageId and box_id = @BoxId and box_type = @BoxType;
                        """, new
                        {
                            RevisionId = page.TreeRevisionId, DocumentInstanceId = documentInstanceId.ToString(),
                            page.PageId, BoxId = logicalPage.ToString(), BoxType = DocumentBoxType.LogicalPage
                        }, transaction, cancellationToken: cancellationToken));
                    if (found != 1)
                    {
                        return Result<ChapterEditingSnapshot>.Failure(AppErrorCodes.ValidationFailed,
                            $"章节“{chapter.Title}”的逻辑页不是目标物理页中已提交的逻辑页。");
                    }
                }
            }

            DocumentPageMap map = current.Definition with { Chapters = frozen.Value };
            string now = FormatUtc(_clock.UtcNow.ToUniversalTime());
            if (map.IsEmpty)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "delete from document_page_maps where document_instance_id = @DocumentInstanceId;",
                    new { DocumentInstanceId = documentInstanceId.ToString() }, transaction,
                    cancellationToken: cancellationToken));
            }
            else
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    insert into document_page_maps (document_instance_id, definition_json, created_at, updated_at)
                    values (@DocumentInstanceId, @DefinitionJson, @Now, @Now)
                    on conflict (document_instance_id) do update set
                        definition_json = excluded.definition_json, updated_at = excluded.updated_at;
                    """,
                    new
                    {
                        DocumentInstanceId = documentInstanceId.ToString(),
                        DefinitionJson = DefinitionJsonCodec.Serialize(map), Now = now
                    },
                    transaction, cancellationToken: cancellationToken));
            }

            LibraryChangeSet? published = null;
            if (_revisions is not null)
            {
                Result<LibraryChangeSet> revision = await _revisions.IncrementInTransactionAsync(connection,
                    transaction,
                    new LibraryChangeSet(0, [], [documentInstanceId], [], [], [], []), cancellationToken);
                if (revision.IsFailure)
                {
                    return Result<ChapterEditingSnapshot>.Failure(revision.ErrorCode!, revision.ErrorMessage!);
                }

                published = revision.Value;
            }

            PageMapRow? saved =
                await ReadChapterMapRowAsync(connection, transaction, documentInstanceId, cancellationToken);
            ChapterEditingSnapshot snapshot = ToEditingSnapshot(documentInstanceId, saved);
            await transaction.CommitAsync(cancellationToken);
            if (published is not null)
            {
                _revisions!.PublishCommitted(published);
            }

            return Result<ChapterEditingSnapshot>.Success(snapshot);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.chapter-editing"))
        {
            return Result<ChapterEditingSnapshot>.Failure(AppErrorCodes.DatabaseError, ex.Message);
        }
    }

    private static bool PageExpectationsMatch(ChapterPageRevisionExpectation[] expected, ChapterSourcePage[] actual)
    {
        return expected.Length == actual.Length && expected.Select((page, index) =>
            page is not null && page.PdfPage == actual[index].PdfPage &&
            page.PageId.ToString() == actual[index].PageId &&
            page.TreeRevisionId?.ToString() == actual[index].TreeRevisionId).All(matches => matches);
    }

    private static async Task<bool> ChapterDocumentExistsAsync(SqliteConnection connection, DbTransaction transaction,
        DocumentInstanceId id, CancellationToken cancellationToken)
    {
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "select count(1) from document_instances where document_instance_id = @Id;", new { Id = id.ToString() },
            transaction, cancellationToken: cancellationToken)) == 1;
    }

    private static Task<PageMapRow?> ReadChapterMapRowAsync(SqliteConnection connection, DbTransaction transaction,
        DocumentInstanceId id, CancellationToken cancellationToken)
    {
        return connection.QuerySingleOrDefaultAsync<PageMapRow>(new CommandDefinition(
            """
            select document_instance_id as DocumentInstanceId, definition_json as DefinitionJson,
                   created_at as CreatedAt, updated_at as UpdatedAt
            from document_page_maps where document_instance_id = @Id;
            """, new { Id = id.ToString() }, transaction, cancellationToken: cancellationToken));
    }

    private static ChapterEditingSnapshot ToEditingSnapshot(DocumentInstanceId id, PageMapRow? row)
    {
        string source = id + "\n" +
                        (row is null ? "absent" : row.CreatedAt + "\n" + row.UpdatedAt + "\n" + row.DefinitionJson);
        return new ChapterEditingSnapshot(
            row is null ? DocumentPageMap.Empty(id) : DefinitionJsonCodec.Deserialize(id, row.DefinitionJson),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))));
    }

    private static IEnumerable<ChapterNode> FlattenChapters(IReadOnlyList<ChapterNode> chapters)
    {
        foreach (ChapterNode chapter in chapters)
        {
            yield return chapter;
            foreach (ChapterNode child in FlattenChapters(chapter.Children))
            {
                yield return child;
            }
        }
    }

    private static Result<IReadOnlyList<ChapterNode>> FreezeChapterTree(IReadOnlyList<ChapterNode> chapters)
    {
        HashSet<ChapterNode> ancestors = new(ReferenceEqualityComparer.Instance);
        int count = 0;
        string? error = null;

        IReadOnlyList<ChapterNode> Copy(IReadOnlyList<ChapterNode> nodes, int depth)
        {
            if (depth > PageMapValidator.MaxChapterDepth || nodes.Count > PageMapValidator.MaxChapterNodes)
            {
                error = "章节树超过允许的深度或节点数量。";
                return [];
            }

            List<ChapterNode> result = [];
            foreach (ChapterNode node in nodes)
            {
                if (node is null || node.Target is null || node.Children is null)
                {
                    error = "章节及其目标和子节点列表不能为空。";
                    break;
                }

                if (++count > PageMapValidator.MaxChapterNodes || !ancestors.Add(node))
                {
                    error = "章节树包含循环或超过允许的节点数量。";
                    break;
                }

                IReadOnlyList<ChapterNode> children = node.Children.Count == 0 ? [] : Copy(node.Children, depth + 1);
                result.Add(node with { Children = children });
                ancestors.Remove(node);
                if (error is not null)
                {
                    break;
                }
            }

            return result.ToArray();
        }

        IReadOnlyList<ChapterNode> frozen = Copy(chapters, 1);
        return error is null
            ? Result<IReadOnlyList<ChapterNode>>.Success(frozen)
            : Result<IReadOnlyList<ChapterNode>>.Failure(AppErrorCodes.ValidationFailed, error);
    }

    private sealed class ChapterSourcePage
    {
        public string PageId { get; init; } = "";
        public int PdfPage { get; init; }
        public string? TreeRevisionId { get; init; }
    }
}
