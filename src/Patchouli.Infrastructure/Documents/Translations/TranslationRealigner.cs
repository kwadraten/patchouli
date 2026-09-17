using System.Data.Common;
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
        Dictionary<string, string> currentHashes = new(StringComparer.Ordinal);
        foreach (DocumentBox box in DocumentBoxProjection.ContentBoxes(boxes))
        {
            currentHashes[box.BoxId.ToString()] = ComputeSourceHash(box);
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
        string fragment = DocumentMarkdownRenderer.CompileBoxFragment(box, false) ?? string.Empty;
        return Blake3Hash.ComputeUtf8($"{box.BoxType}\u001f{fragment}");
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
}
