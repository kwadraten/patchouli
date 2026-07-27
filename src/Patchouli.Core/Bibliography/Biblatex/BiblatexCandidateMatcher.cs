namespace Patchouli.Core.Bibliography.Biblatex;

public static class BiblatexCandidateMatcher
{
    public const int MinimumMatchCount = 3;

    public static IReadOnlyList<BiblatexMatchCandidate> FindCandidates(
        BiblatexMappedItem source,
        IEnumerable<BiblatexMatchCandidateSeed> existingItems)
    {
        string? sourceTitle = BiblatexFieldMapper.ExactTrim(source.Title);
        IReadOnlyList<string> sourceAuthors = BiblatexFieldMapper.AuthorMatchKeys(source.Creators);
        string? sourceJournalOrPublisher = BiblatexFieldMapper.ExactTrim(source.PublicationTitle)
                                           ?? BiblatexFieldMapper.ExactTrim(source.Publisher);
        IReadOnlySet<int> sourceYears = BiblatexFieldMapper.ExtractIssuedYears(source.Dates);

        List<BiblatexMatchCandidate> matches = [];
        foreach (BiblatexMatchCandidateSeed item in existingItems)
        {
            bool titleMatched = sourceTitle is not null &&
                                BiblatexFieldMapper.ExactTrimEquals(sourceTitle, item.Title);
            bool authorsMatched = BiblatexFieldMapper.AuthorsMatch(sourceAuthors, item.AuthorKeys);
            bool sourceMatched = sourceJournalOrPublisher is not null &&
                                 (BiblatexFieldMapper.ExactTrimEquals(sourceJournalOrPublisher, item.PublicationTitle)
                                  || (item.PublicationTitle is null &&
                                      BiblatexFieldMapper.ExactTrimEquals(sourceJournalOrPublisher, item.Publisher)));
            bool yearMatched = BiblatexFieldMapper.YearsMatch(sourceYears, item.IssuedYears);

            int matchCount = (titleMatched ? 1 : 0)
                             + (authorsMatched ? 1 : 0)
                             + (sourceMatched ? 1 : 0)
                             + (yearMatched ? 1 : 0);

            if (matchCount < MinimumMatchCount)
            {
                continue;
            }

            matches.Add(new BiblatexMatchCandidate(
                item.ItemId,
                item.Title,
                item.PublicationTitle,
                item.Publisher,
                item.AuthorKeys,
                item.IssuedYears,
                matchCount,
                titleMatched,
                authorsMatched,
                sourceMatched,
                yearMatched));
        }

        return matches
            .OrderByDescending(static candidate => candidate.MatchCount)
            .ThenBy(static candidate => candidate.Title, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.ItemId, StringComparer.Ordinal)
            .ToArray();
    }
}

public sealed record BiblatexMatchCandidateSeed(
    string ItemId,
    string Title,
    string? PublicationTitle,
    string? Publisher,
    IReadOnlyList<string> AuthorKeys,
    IReadOnlySet<int> IssuedYears);
