using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Hashing;

namespace Patchouli.Infrastructure.Documents.Translations;

/// <summary>
/// The persisted translation header for one page.
/// </summary>
internal sealed class TranslationHeader
{
    public string SourceTreeRevisionId { get; set; } = string.Empty;
    public int Version { get; set; }
}

/// <summary>
/// Aligns stored translation rows with the current committed tree revision. Rows are keyed by
/// box id and carry the source payload hash captured at write time: a row survives realignment
/// only while its box still exists and its source content is unchanged. Everything else is
/// dropped, so an OCR re-import (which issues fresh box ids) expires every translation.
/// </summary>
public sealed class TranslationRealigner
{
    private readonly IClock _clock;

    public TranslationRealigner(IClock clock)
    {
        _clock = clock;
    }

    internal async Task<Result<TranslationHeader>> RealignAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        PageId pageId,
        DocumentTreeRevisionId treeRevisionId,
        IReadOnlyList<DocumentBox> boxes)
    {
        string pageIdValue = pageId.ToString();
        string? priorRevisionId = await connection.ExecuteScalarAsync<string?>(
            "select source_tree_revision_id from page_translations where page_id = @PageId;",
            new { PageId = pageIdValue }, transaction);
        Dictionary<string, TableBoxPayload> priorTables = await LoadPriorTablePayloadsAsync(
            connection, transaction, priorRevisionId);
        Dictionary<string, DocumentBox> currentBoxes = DocumentBoxProjection.ContentBoxes(boxes)
            .ToDictionary(box => box.BoxId.ToString(), StringComparer.Ordinal);
        Dictionary<string, string> currentHashes = new(StringComparer.Ordinal);
        foreach ((string boxId, DocumentBox box) in currentBoxes)
        {
            currentHashes[boxId] = ComputeSourceHash(box);
        }

        TranslationBoxHashRow[] rows = (await connection.QueryAsync<TranslationBoxHashRow>(
            "select box_id as BoxId, source_hash as SourceHash from translation_boxes where page_id = @PageId;",
            new { PageId = pageIdValue },
            transaction)).ToArray();

        foreach (TranslationBoxHashRow row in rows)
        {
            if (currentHashes.TryGetValue(row.BoxId, out string? currentHash) &&
                string.Equals(currentHash, row.SourceHash, StringComparison.Ordinal))
            {
                continue;
            }

            if (currentHash is not null && currentBoxes.TryGetValue(row.BoxId, out DocumentBox? currentBox) &&
                currentBox.Payload is TableBoxPayload currentTable &&
                priorTables.TryGetValue(row.BoxId, out TableBoxPayload? priorTable) &&
                string.Equals(row.SourceHash, ComputeLegacyTableSourceHash(priorTable), StringComparison.Ordinal) &&
                string.Equals(TableContentAdapter.GetSemanticFingerprint(priorTable),
                    TableContentAdapter.GetSemanticFingerprint(currentTable), StringComparison.Ordinal))
            {
                await connection.ExecuteAsync(
                    "update translation_boxes set source_hash = @SourceHash where page_id = @PageId and box_id = @BoxId;",
                    new { PageId = pageIdValue, BoxId = row.BoxId, SourceHash = currentHash }, transaction);
                continue;
            }

            await connection.ExecuteAsync(
                "delete from translation_boxes where page_id = @PageId and box_id = @BoxId;",
                new { PageId = pageIdValue, BoxId = row.BoxId },
                transaction);
        }

        int version = (await connection.ExecuteScalarAsync<int?>(
            "select version from page_translations where page_id = @PageId;",
            new { PageId = pageIdValue },
            transaction) ?? 0) + 1;

        await connection.ExecuteAsync(
            """
            update page_translations
            set source_tree_revision_id = @RevisionId, version = @Version, updated_at = @UpdatedAt
            where page_id = @PageId;
            """,
            new
            {
                PageId = pageIdValue,
                RevisionId = treeRevisionId.ToString(),
                Version = version,
                UpdatedAt = FormatUtc(_clock.UtcNow)
            },
            transaction);

        return Result<TranslationHeader>.Success(new TranslationHeader
        {
            SourceTreeRevisionId = treeRevisionId.ToString(),
            Version = version
        });
    }

    /// <summary>
    /// Content hash of a box's compiled source fragment. Two boxes produce the same hash only
    /// when their type and rendered markdown are identical, which is precisely the condition
    /// under which a stored translation stays valid.
    /// </summary>
    public static string ComputeSourceHash(DocumentBox box)
    {
        string fragment = box.Payload is TableBoxPayload table
            ? TableContentAdapter.GetSemanticFingerprint(table)
            : DocumentMarkdownRenderer.CompileBoxFragment(box, false) ?? string.Empty;
        return Blake3Hash.ComputeUtf8($"{box.BoxType}\u001f{fragment}");
    }

    private static string ComputeLegacyTableSourceHash(TableBoxPayload table)
    {
        string fragment = table.Markdown;
        if (table.Annotations is { Count: > 0 } annotations)
        {
            fragment = string.Join("\n\n", new[] { fragment }.Concat(annotations
                    .Select(static annotation => annotation.Markdown))
                .Where(static value => !string.IsNullOrWhiteSpace(value)));
        }

        return Blake3Hash.ComputeUtf8($"{DocumentBoxType.Table}\u001f{fragment}");
    }

    private static async Task<Dictionary<string, TableBoxPayload>> LoadPriorTablePayloadsAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        string? revisionId)
    {
        Dictionary<string, TableBoxPayload> tables = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(revisionId))
        {
            return tables;
        }

        IEnumerable<PriorTableRow> rows = await connection.QueryAsync<PriorTableRow>(
            "select box_id as BoxId, payload_json as PayloadJson from document_boxes where tree_revision_id = @RevisionId and box_type = @TableType;",
            new { RevisionId = revisionId, TableType = DocumentBoxType.Table }, transaction);
        foreach (PriorTableRow row in rows)
        {
            try
            {
                if (DocumentBoxPayloadSerializer.Deserialize(DocumentBoxType.Table, null, row.PayloadJson) is
                    TableBoxPayload payload)
                {
                    tables[row.BoxId] = payload;
                }
            }
            catch (JsonException)
            {
                // A malformed historical table cannot be used as migration evidence; its translation expires.
            }
        }

        return tables;
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O");
    }

    private sealed class TranslationBoxHashRow
    {
        public string BoxId { get; set; } = string.Empty;
        public string SourceHash { get; set; } = string.Empty;
    }

    private sealed class PriorTableRow
    {
        public string BoxId { get; set; } = string.Empty;
        public string? PayloadJson { get; set; }
    }
}
