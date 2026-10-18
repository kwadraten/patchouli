using Patchouli.Core.Ids;

namespace Patchouli.Core.Pagination;

public sealed record ChapterScanOptions(
    int? StartPdfPage = null,
    int? EndPdfPage = null,
    int MinimumLevel = 1,
    int MaximumLevel = 6);

public sealed record ChapterScanProgress(int CompletedPages, int TotalPages);

public sealed record ChapterScanPageVersion(PageId PageId, int PdfPage, DocumentTreeRevisionId? RevisionId);

public sealed record ChapterScanCandidate(
    ChapterNode Chapter,
    int HeadingLevel,
    DocumentBoxId SourceBoxId,
    DocumentTreeRevisionId SourceRevisionId,
    int SourceOffset);

public sealed record ChapterScanResult(
    DocumentInstanceId DocumentInstanceId,
    IReadOnlyList<ChapterScanCandidate> Candidates,
    IReadOnlyList<ChapterNode> Chapters,
    IReadOnlyList<ChapterScanPageVersion> PageVersions,
    IReadOnlyList<int> MissingTextPages,
    IReadOnlyList<MapDiagnostic> Diagnostics);

/// <summary>Builds the visible hierarchy after filtering, promoting children of excluded headings.</summary>
public static class ChapterScanTree
{
    public static IReadOnlyList<ChapterNode> Build(IEnumerable<ChapterScanCandidate> candidates)
    {
        List<Entry> roots = [];
        List<Entry> ancestors = [];
        foreach (ChapterScanCandidate candidate in candidates)
        {
            while (ancestors.Count > 0 && ancestors[^1].Level >= candidate.HeadingLevel)
            {
                ancestors.RemoveAt(ancestors.Count - 1);
            }

            Entry entry = new(candidate.Chapter, candidate.HeadingLevel);
            (ancestors.Count == 0 ? roots : ancestors[^1].Children).Add(entry);
            ancestors.Add(entry);
        }

        return roots.Select(Convert).ToArray();
    }

    private static ChapterNode Convert(Entry entry)
    {
        return entry.Node with { Children = entry.Children.Select(Convert).ToArray() };
    }

    private sealed record Entry(ChapterNode Node, int Level)
    {
        public List<Entry> Children { get; } = [];
    }
}
