using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.Bibliography;

public sealed class DuplicateItemDetectionService : IDuplicateItemDetectionService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ILibraryIdentityService _libraryIdentityService;

    public DuplicateItemDetectionService(
        SqliteConnectionFactory connectionFactory,
        ILibraryIdentityService libraryIdentityService)
    {
        _connectionFactory = connectionFactory;
        _libraryIdentityService = libraryIdentityService;
    }

    public async Task<IReadOnlyList<DuplicateItemPair>> FindDuplicatesAsync(
        CancellationToken cancellationToken = default)
    {
        Result<LibraryMetadata> libraryResult = await _libraryIdentityService.GetCurrentLibraryAsync(cancellationToken);
        if (libraryResult.IsFailure)
        {
            return Array.Empty<DuplicateItemPair>();
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);

            Dictionary<ItemId, DuplicateItemInfo> activeItems = await LoadActiveItemsAsync(
                connection,
                libraryResult.Value.LibraryId,
                cancellationToken);

            Dictionary<(ItemId First, ItemId Second), PairBuilder> pairs = new();

            await AddIdentifierMatchesAsync(connection, libraryResult.Value.LibraryId, activeItems, pairs,
                cancellationToken);
            await AddFileHashMatchesAsync(connection, libraryResult.Value.LibraryId, activeItems, pairs,
                cancellationToken);
            AddMetadataSimilarityMatches(activeItems, pairs);

            return pairs
                .Values
                .Select(builder => builder.ToPair(activeItems))
                .OrderBy(pair => pair.ItemIdA.ToString(), StringComparer.Ordinal)
                .ThenBy(pair => pair.ItemIdB.ToString(), StringComparer.Ordinal)
                .ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.duplicate-item-detection"))
        {
            return Array.Empty<DuplicateItemPair>();
        }
    }

    private static async Task<Dictionary<ItemId, DuplicateItemInfo>> LoadActiveItemsAsync(
        SqliteConnection connection,
        LibraryId libraryId,
        CancellationToken cancellationToken)
    {
        IEnumerable<ItemRow> rows = await connection.QueryAsync<ItemRow>(
            """
            select
                item_id as ItemId,
                title as Title,
                publication_title as PublicationTitle,
                publisher as Publisher,
                created_at as CreatedAt
            from items
            where library_id = @LibraryId
              and deleted_at is null
              and merged_into_item_id is null;
            """,
            new { LibraryId = libraryId.ToString() });

        ItemRow[] itemRows = rows.ToArray();
        if (itemRows.Length == 0)
        {
            return new Dictionary<ItemId, DuplicateItemInfo>();
        }

        ItemId[] itemIds = itemRows.Select(row => ItemId.Parse(row.ItemId)).ToArray();
        Dictionary<ItemId, IReadOnlyList<ItemCreator>> creators =
            await LoadCreatorsAsync(connection, itemIds, cancellationToken);
        Dictionary<ItemId, IReadOnlyList<ItemDate>>
            dates = await LoadDatesAsync(connection, itemIds, cancellationToken);

        return itemRows.ToDictionary(
            row => ItemId.Parse(row.ItemId),
            row => new DuplicateItemInfo(
                ItemId.Parse(row.ItemId),
                row.Title,
                row.PublicationTitle,
                row.Publisher,
                DateTimeOffset.Parse(row.CreatedAt),
                creators.GetValueOrDefault(ItemId.Parse(row.ItemId)) ?? Array.Empty<ItemCreator>(),
                dates.GetValueOrDefault(ItemId.Parse(row.ItemId)) ?? Array.Empty<ItemDate>()));
    }

    private static async Task<Dictionary<ItemId, IReadOnlyList<ItemCreator>>> LoadCreatorsAsync(
        SqliteConnection connection,
        IReadOnlyList<ItemId> itemIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (itemIds.Count == 0)
        {
            return new Dictionary<ItemId, IReadOnlyList<ItemCreator>>();
        }

        IEnumerable<CreatorRow> rows = await connection.QueryAsync<CreatorRow>(
            """
            select
                creator_id as CreatorId,
                item_id as ItemId,
                role as Role,
                family as Family,
                given as Given,
                literal as Literal,
                suffix as Suffix,
                particles as Particles,
                sequence_index as SequenceIndex,
                created_at as CreatedAt
            from item_creators
            where item_id in @ItemIds
            order by item_id, role, sequence_index, creator_id;
            """,
            new { ItemIds = itemIds.Select(id => id.ToString()).ToArray() });

        return rows
            .GroupBy(row => ItemId.Parse(row.ItemId))
            .ToDictionary(group => group.Key,
                group => (IReadOnlyList<ItemCreator>)group.Select(row => row.ToCreator()).ToArray());
    }

    private static async Task<Dictionary<ItemId, IReadOnlyList<ItemDate>>> LoadDatesAsync(
        SqliteConnection connection,
        IReadOnlyList<ItemId> itemIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (itemIds.Count == 0)
        {
            return new Dictionary<ItemId, IReadOnlyList<ItemDate>>();
        }

        IEnumerable<DateRow> rows = await connection.QueryAsync<DateRow>(
            """
            select
                date_id as DateId,
                item_id as ItemId,
                role as Role,
                date_parts_json as DatePartsJson,
                circa as Circa,
                season as Season,
                literal as Literal,
                created_at as CreatedAt
            from item_dates
            where item_id in @ItemIds
            order by item_id,
                     case role when 'issued' then 0 when 'accessed' then 1 else 2 end,
                     role;
            """,
            new { ItemIds = itemIds.Select(id => id.ToString()).ToArray() });

        return rows
            .GroupBy(row => ItemId.Parse(row.ItemId))
            .ToDictionary(group => group.Key,
                group => (IReadOnlyList<ItemDate>)group.Select(row => row.ToDate()).ToArray());
    }

    private static async Task AddIdentifierMatchesAsync(
        SqliteConnection connection,
        LibraryId libraryId,
        Dictionary<ItemId, DuplicateItemInfo> activeItems,
        Dictionary<(ItemId First, ItemId Second), PairBuilder> pairs,
        CancellationToken cancellationToken)
    {
        IEnumerable<IdentifierRow> rows = await connection.QueryAsync<IdentifierRow>(
            """
            select
                i.identifier_id as IdentifierId,
                i.item_id as ItemId,
                i.scheme as Scheme,
                i.value as Value,
                i.note as Note,
                i.created_at as CreatedAt
            from item_identifiers i
            inner join items it on it.item_id = i.item_id
            where it.library_id = @LibraryId
              and it.deleted_at is null
              and it.merged_into_item_id is null;
            """,
            new { LibraryId = libraryId.ToString() });

        foreach (IGrouping<(string Scheme, string Value), IdentifierRow> group in rows
                     .GroupBy(row => (row.Scheme, row.Value)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            ItemId[] itemIds = group
                .Select(row => ItemId.Parse(row.ItemId))
                .Where(activeItems.ContainsKey)
                .Distinct()
                .ToArray();

            AddPairCombinations(itemIds, DuplicateItemReason.IdentifierMatch, pairs);
        }
    }

    private static async Task AddFileHashMatchesAsync(
        SqliteConnection connection,
        LibraryId libraryId,
        Dictionary<ItemId, DuplicateItemInfo> activeItems,
        Dictionary<(ItemId First, ItemId Second), PairBuilder> pairs,
        CancellationToken cancellationToken)
    {
        IEnumerable<FileHashRow> rows = await connection.QueryAsync<FileHashRow>(
            """
            select
                di.item_id as ItemId,
                fa.full_blake3 as FullBlake3
            from document_instances di
            inner join file_assets fa on fa.file_asset_id = di.file_asset_id
            inner join items it on it.item_id = di.item_id
            where di.is_primary = 1
              and fa.full_blake3 is not null
              and it.library_id = @LibraryId
              and it.deleted_at is null
              and it.merged_into_item_id is null;
            """,
            new { LibraryId = libraryId.ToString() });

        foreach (IGrouping<string, FileHashRow> group in rows
                     .GroupBy(row => row.FullBlake3))
        {
            cancellationToken.ThrowIfCancellationRequested();

            ItemId[] itemIds = group
                .Select(row => ItemId.Parse(row.ItemId))
                .Where(activeItems.ContainsKey)
                .Distinct()
                .ToArray();

            AddPairCombinations(itemIds, DuplicateItemReason.FileHashMatch, pairs);
        }
    }

    private static void AddMetadataSimilarityMatches(
        Dictionary<ItemId, DuplicateItemInfo> activeItems,
        Dictionary<(ItemId First, ItemId Second), PairBuilder> pairs)
    {
        if (activeItems.Count < 2)
        {
            return;
        }

        List<BiblatexMatchCandidateSeed> seeds = activeItems.Values.Select(ToSeed).ToList();
        Dictionary<string, List<BiblatexMatchCandidateSeed>> titleGroups = new();
        List<BiblatexMatchCandidateSeed> nullTitleSeeds = new();

        foreach (BiblatexMatchCandidateSeed seed in seeds)
        {
            string? titleKey = BiblatexFieldMapper.ExactTrim(seed.Title);
            if (titleKey is null)
            {
                nullTitleSeeds.Add(seed);
            }
            else
            {
                if (!titleGroups.TryGetValue(titleKey, out List<BiblatexMatchCandidateSeed>? group))
                {
                    group = new List<BiblatexMatchCandidateSeed>();
                    titleGroups[titleKey] = group;
                }

                group.Add(seed);
            }
        }

        foreach (DuplicateItemInfo item in activeItems.Values)
        {
            string? titleKey = BiblatexFieldMapper.ExactTrim(item.Title);
            List<BiblatexMatchCandidateSeed> candidateSeeds = new();

            if (titleKey is not null)
            {
                if (titleGroups.TryGetValue(titleKey, out List<BiblatexMatchCandidateSeed>? group))
                {
                    candidateSeeds.AddRange(group);
                }

                candidateSeeds.AddRange(nullTitleSeeds);
            }
            else
            {
                candidateSeeds.AddRange(seeds);
            }

            string currentItemId = item.ItemId.ToString();
            candidateSeeds.RemoveAll(seed => string.Equals(seed.ItemId, currentItemId, StringComparison.Ordinal));

            if (candidateSeeds.Count == 0)
            {
                continue;
            }

            BiblatexMappedItem source = ToMappedItem(item);
            IReadOnlyList<BiblatexMatchCandidate> candidates =
                BiblatexCandidateMatcher.FindCandidates(source, candidateSeeds);

            foreach (BiblatexMatchCandidate candidate in candidates)
            {
                ItemId otherItemId = ItemId.Parse(candidate.ItemId);
                if (otherItemId == item.ItemId)
                {
                    continue;
                }

                EnsurePair(item.ItemId, otherItemId, pairs).AddReason(DuplicateItemReason.SimilarMetadata);
            }
        }
    }

    private static void AddPairCombinations(
        IReadOnlyList<ItemId> itemIds,
        DuplicateItemReason reason,
        Dictionary<(ItemId First, ItemId Second), PairBuilder> pairs)
    {
        for (int i = 0; i < itemIds.Count; i++)
        {
            for (int j = i + 1; j < itemIds.Count; j++)
            {
                EnsurePair(itemIds[i], itemIds[j], pairs).AddReason(reason);
            }
        }
    }

    private static PairBuilder EnsurePair(
        ItemId a,
        ItemId b,
        Dictionary<(ItemId First, ItemId Second), PairBuilder> pairs)
    {
        (ItemId First, ItemId Second) key = OrderPair(a, b);
        if (!pairs.TryGetValue(key, out PairBuilder? builder))
        {
            builder = new PairBuilder(key.First, key.Second);
            pairs[key] = builder;
        }

        return builder;
    }

    private static (ItemId First, ItemId Second) OrderPair(ItemId a, ItemId b)
    {
        return string.Compare(a.ToString(), b.ToString(), StringComparison.Ordinal) <= 0
            ? (a, b)
            : (b, a);
    }

    private static BiblatexMatchCandidateSeed ToSeed(DuplicateItemInfo item)
    {
        return new BiblatexMatchCandidateSeed(
            item.ItemId.ToString(),
            item.Title ?? string.Empty,
            item.PublicationTitle,
            item.Publisher,
            BiblatexFieldMapper.AuthorMatchKeys(ToCreatorInputs(item.Creators)),
            BiblatexFieldMapper.ExtractYearsFromItemDates(item.Dates));
    }

    private static BiblatexMappedItem ToMappedItem(DuplicateItemInfo item)
    {
        return new BiblatexMappedItem(
            "general",
            null,
            item.Title ?? string.Empty,
            null,
            null,
            ToCreatorInputs(item.Creators),
            ToDateInputs(item.Dates),
            Array.Empty<ItemIdentifierInput>(),
            item.PublicationTitle,
            null,
            null,
            item.Publisher,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            Array.Empty<string>(),
            null,
            string.Empty,
            "misc");
    }

    private static IReadOnlyList<ItemCreatorInput> ToCreatorInputs(IReadOnlyList<ItemCreator> creators)
    {
        return creators.Select(creator => new ItemCreatorInput(
            creator.Role,
            creator.Family,
            creator.Given,
            creator.Literal,
            creator.Suffix,
            creator.Particles)).ToArray();
    }

    private static IReadOnlyList<ItemDateInput> ToDateInputs(IReadOnlyList<ItemDate> dates)
    {
        return dates.Select(date => new ItemDateInput(
            date.Role,
            date.DatePartsJson,
            date.Circa,
            date.Season,
            date.Literal)).ToArray();
    }

    private sealed class PairBuilder
    {
        private readonly HashSet<DuplicateItemReason> _reasons = new();

        public PairBuilder(ItemId itemIdA, ItemId itemIdB)
        {
            ItemIdA = itemIdA;
            ItemIdB = itemIdB;
        }

        public ItemId ItemIdA { get; }
        public ItemId ItemIdB { get; }

        public void AddReason(DuplicateItemReason reason)
        {
            _reasons.Add(reason);
        }

        public DuplicateItemPair ToPair(IReadOnlyDictionary<ItemId, DuplicateItemInfo> items)
        {
            // Default target is the earlier item in library list order (created_at ascending).
            DuplicateItemInfo infoA = items[ItemIdA];
            DuplicateItemInfo infoB = items[ItemIdB];
            int createdCompare = infoA.CreatedAt.CompareTo(infoB.CreatedAt);
            ItemId defaultTarget = createdCompare < 0
                ? ItemIdA
                : createdCompare > 0
                    ? ItemIdB
                    : string.Compare(ItemIdA.ToString(), ItemIdB.ToString(), StringComparison.Ordinal) <= 0
                        ? ItemIdA
                        : ItemIdB;

            return new DuplicateItemPair(
                ItemIdA,
                ItemIdB,
                _reasons.OrderBy(static r => r).ToArray(),
                defaultTarget);
        }
    }

    private sealed record DuplicateItemInfo(
        ItemId ItemId,
        string? Title,
        string? PublicationTitle,
        string? Publisher,
        DateTimeOffset CreatedAt,
        IReadOnlyList<ItemCreator> Creators,
        IReadOnlyList<ItemDate> Dates);

    private sealed class ItemRow
    {
        public string ItemId { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string? PublicationTitle { get; init; }
        public string? Publisher { get; init; }
        public string CreatedAt { get; init; } = string.Empty;
    }

    private sealed class CreatorRow
    {
        public string CreatorId { get; init; } = string.Empty;
        public string ItemId { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
        public string? Family { get; init; }
        public string? Given { get; init; }
        public string? Literal { get; init; }
        public string? Suffix { get; init; }
        public string? Particles { get; init; }
        public int SequenceIndex { get; init; }
        public string CreatedAt { get; init; } = string.Empty;

        public ItemCreator ToCreator()
        {
            return new ItemCreator(
                CreatorId,
                Patchouli.Core.Ids.ItemId.Parse(ItemId),
                Role,
                Family,
                Given,
                Literal,
                Suffix,
                Particles,
                SequenceIndex,
                DateTimeOffset.Parse(CreatedAt));
        }
    }

    private sealed class DateRow
    {
        public string DateId { get; init; } = string.Empty;
        public string ItemId { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
        public string DatePartsJson { get; init; } = "[]";
        public bool Circa { get; init; }
        public string? Season { get; init; }
        public string? Literal { get; init; }
        public string CreatedAt { get; init; } = string.Empty;

        public ItemDate ToDate()
        {
            return new ItemDate(
                DateId,
                Patchouli.Core.Ids.ItemId.Parse(ItemId),
                Role,
                DatePartsJson,
                Circa,
                Season,
                Literal,
                DateTimeOffset.Parse(CreatedAt));
        }
    }

    private sealed class IdentifierRow
    {
        public string IdentifierId { get; init; } = string.Empty;
        public string ItemId { get; init; } = string.Empty;
        public string Scheme { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
        public string? Note { get; init; }
        public string CreatedAt { get; init; } = string.Empty;
    }

    private sealed class FileHashRow
    {
        public string ItemId { get; init; } = string.Empty;
        public string FullBlake3 { get; init; } = string.Empty;
    }
}
