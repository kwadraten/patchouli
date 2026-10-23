using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Ids;
using Patchouli.Infrastructure.Bibliography;

namespace Patchouli.Tests;

public sealed class ItemMergeRowPlannerTests
{
    [Fact]
    public void Creators_without_any_name_part_are_dropped_and_sequence_index_preserved()
    {
        ItemId itemId = ItemId.New();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyList<ItemCreator> creators =
        [
            new("c1", itemId, "author", null, null, null, null, null, 0, now),
            new("c2", itemId, "author", "Doe", null, null, null, null, 1, now),
            new("c3", itemId, "editor", null, null, "Corp", null, null, 2, now)
        ];

        IReadOnlyList<ItemMergeRowPlanner.CreatorRow> rows = ItemMergeRowPlanner.PlanCreators(creators);

        rows.Should().HaveCount(2);
        rows[0].SequenceIndex.Should().Be(1, "the kept creator keeps its original index");
        rows[1].SequenceIndex.Should().Be(2);
    }

    [Fact]
    public void Dates_drop_unsupported_roles_and_empty_payloads()
    {
        ItemId itemId = ItemId.New();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyList<ItemDate> dates =
        [
            new("d1", itemId, "unsupported-role", "[[2020]]", false, null, null, now),
            new("d2", itemId, "issued", "[]", false, null, null, now),
            new("d3", itemId, "issued", "[[2020,5]]", true, "Spring", null, now),
            new("d4", itemId, "issued", "not json", false, null, "May 2020", now)
        ];

        IReadOnlyList<ItemMergeRowPlanner.DateRow> rows = ItemMergeRowPlanner.PlanDates(dates);

        rows.Should().HaveCount(2);
        rows[0].DatePartsJson.Should().Be("[[2020,5]]");
        rows[0].Circa.Should().Be(1);
        rows[1].Literal.Should().Be("May 2020");
        rows[1].DatePartsJson.Should().Be("[]", "non-array payloads normalize to an empty array");
    }

    [Fact]
    public void Identifiers_normalize_dedup_and_drop_blank_pairs()
    {
        ItemId itemId = ItemId.New();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyList<ItemIdentifier> identifiers =
        [
            new(IdentifierId.New(), itemId, " ISBN ", " 978-1 ", null, now),
            new(IdentifierId.New(), itemId, "isbn", "978-1", "duplicate", now),
            new(IdentifierId.New(), itemId, " ", "x", null, now),
            new(IdentifierId.New(), itemId, "doi", "10.1/x", null, now)
        ];

        IReadOnlyList<ItemMergeRowPlanner.IdentifierRow> rows =
            ItemMergeRowPlanner.PlanIdentifiers(identifiers);

        rows.Should().HaveCount(2);
        rows[0].Scheme.Should().Be("isbn");
        rows[0].Value.Should().Be("978-1");
        rows[0].Note.Should().BeNull("the first occurrence wins");
        rows[1].Scheme.Should().Be("doi");
    }
}
