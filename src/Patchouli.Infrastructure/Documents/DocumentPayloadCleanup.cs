using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Ids;
using Patchouli.Core.Search;
using Patchouli.Infrastructure.Search;
using Patchouli.Ocr;

namespace Patchouli.Infrastructure.Documents;

/// <summary>
/// The document-level payload deletion shared by single-document removal
/// (<see cref="DocumentInstanceService.RemoveDocumentInstanceAsync"/>) and whole-item permanent
/// purge (<see cref="Bibliography.ItemPurgeService"/>). Both destructive paths run the same
/// statements in the same order inside their own write lease and transaction so no second,
/// divergent cleanup sequence can appear. The caller owns validation, transaction lifetime and
/// Library revision publication.
/// </summary>
internal static class DocumentPayloadCleanup
{
    /// <summary>Pages owned by the given document instances; needed for page-scoped deletes.</summary>
    internal static async Task<string[]> ListPageIdsAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        IReadOnlyCollection<string> documentIds,
        CancellationToken cancellationToken)
    {
        return documentIds.Count == 0
            ? []
            : (await connection.QueryAsync<string>(new CommandDefinition(
                "select page_id from pages where document_instance_id in @DocumentIds;",
                new { DocumentIds = documentIds },
                transaction,
                cancellationToken: cancellationToken))).ToArray();
    }

    /// <summary>OCR runs still queued or executing; destructive deletion must refuse these.</summary>
    internal static async Task<int> CountActiveOcrRunsAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        IReadOnlyCollection<string> documentIds,
        CancellationToken cancellationToken)
    {
        return documentIds.Count == 0
            ? 0
            : await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                select count(1)
                from ocr_runs
                where document_instance_id in @DocumentIds
                  and state in (@Pending, @Running);
                """,
                new
                {
                    DocumentIds = documentIds,
                    Pending = OcrRunState.Pending,
                    Running = OcrRunState.Running
                },
                transaction,
                cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Every FileAsset the deleted payload still references: each instance's own source asset
    /// plus payload assets referenced by its boxes. The FileAsset records themselves are never
    /// deleted here — they become FileAsset GC candidates just like after a permanent purge.
    /// </summary>
    internal static async Task<FileAssetId[]> ListReferencedFileAssetIdsAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        IReadOnlyCollection<string> documentIds,
        CancellationToken cancellationToken)
    {
        if (documentIds.Count == 0)
        {
            return [];
        }

        IEnumerable<string> rawIds = await connection.QueryAsync<string>(new CommandDefinition(
            """
            select d.file_asset_id as FileAssetId
            from document_instances d
            where d.document_instance_id in @DocumentIds and d.file_asset_id is not null
            union
            select refs.file_asset_id as FileAssetId
            from file_asset_payload_refs refs
            join document_tree_revisions revisions
              on revisions.tree_revision_id = refs.tree_revision_id
            where revisions.document_instance_id in @DocumentIds;
            """,
            new { DocumentIds = documentIds },
            transaction,
            cancellationToken: cancellationToken));

        // Derived payload references may contain legacy, non-asset strings.
        return rawIds
            .Where(id => Guid.TryParse(id, out _))
            .Select(FileAssetId.Parse)
            .ToArray();
    }

    /// <returns>True only when this call rebuilt the cache; false for an already-ready cache.</returns>
    internal static Task<bool> EnsureFtsCacheReadyAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        return SearchFtsCacheWriter.EnsureReadyAsync(connection, transaction, cancellationToken);
    }

    /// <summary>
    /// Adoption records, per-page results and run rows. <c>ocr_candidate_adoptions</c> has no
    /// cascade on either FK, so skipping it makes the document delete hit a constraint failure.
    /// </summary>
    internal static async Task DeleteOcrHistoryAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        IReadOnlyCollection<string> documentIds,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            delete from ocr_candidate_adoptions
            where document_instance_id in @DocumentIds
               or ocr_run_id in (
                   select ocr_run_id from ocr_runs where document_instance_id in @DocumentIds);
            """,
            new { DocumentIds = documentIds },
            transaction,
            cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            """
            delete from ocr_page_results
            where ocr_run_id in (
                select ocr_run_id from ocr_runs where document_instance_id in @DocumentIds);
            """,
            new { DocumentIds = documentIds },
            transaction,
            cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            "delete from ocr_runs where document_instance_id in @DocumentIds;",
            new { DocumentIds = documentIds },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>Mapped FTS cache rows, per-scope index status and search units.</summary>
    internal static async Task DeleteSearchIndexAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        IReadOnlyCollection<string> documentIds,
        IReadOnlyCollection<string> pageIds,
        CancellationToken cancellationToken)
    {
        await SearchFtsCacheWriter.DeleteForDocumentInstancesAsync(
            connection, transaction, documentIds, cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            "delete from search_index_status where (scope_type = @DocumentScope and scope_id in @DocumentIds) or (scope_type = @PageScope and scope_id in @PageIds);",
            new
            {
                DocumentScope = SearchIndexScopeType.DocumentInstance,
                PageScope = SearchIndexScopeType.Page,
                DocumentIds = documentIds,
                PageIds = pageIds
            },
            transaction,
            cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            "delete from search_units where document_instance_id in @DocumentIds;",
            new { DocumentIds = documentIds },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Commit records, translations, box trees, revisions and pages. Commit pages and OCR
    /// history must already be gone: their tree-revision references have no cascade.
    /// </summary>
    internal static async Task DeleteDocumentHistoryAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        IReadOnlyCollection<string> documentIds,
        IReadOnlyCollection<string> pageIds,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "delete from document_commit_pages where commit_id in (select commit_id from document_commits where document_instance_id in @DocumentIds) or page_id in @PageIds;",
            new { DocumentIds = documentIds, PageIds = pageIds },
            transaction,
            cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            "delete from document_commits where document_instance_id in @DocumentIds;",
            new { DocumentIds = documentIds },
            transaction,
            cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            "delete from translation_boxes where page_id in @PageIds;",
            new { PageIds = pageIds },
            transaction,
            cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            "delete from page_translations where page_id in @PageIds;",
            new { PageIds = pageIds },
            transaction,
            cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            "delete from document_boxes where document_instance_id in @DocumentIds;",
            new { DocumentIds = documentIds },
            transaction,
            cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            "delete from document_tree_revisions where document_instance_id in @DocumentIds;",
            new { DocumentIds = documentIds },
            transaction,
            cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            "delete from pages where document_instance_id in @DocumentIds;",
            new { DocumentIds = documentIds },
            transaction,
            cancellationToken: cancellationToken));
    }
}
