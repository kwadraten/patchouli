using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Pagination;

namespace Patchouli.Tests;

/// <summary>
/// Tests for <c>logical_page</c> pagination rules: the caller-supplied reading order is
/// trusted verbatim (the resolver never re-sorts by Box ID or geometry), logical pages are
/// enumerated +1 each, and the validator rejects mixed rule kinds, gapped logical ranges,
/// and illegal range shapes.
/// </summary>
public sealed class PageMapLogicalRuleTests
{
    private static DocumentInstanceId DocId => new(new Guid("11111111-1111-1111-1111-111111111111"));

    private static DocumentBoxId Box(string suffix)
    {
        return new DocumentBoxId(Guid.Parse($"22222222-2222-2222-2222-2222222222{suffix}"));
    }

    private static IReadOnlyList<LogicalRuleHealth> Healthy(DocumentPageMap map)
    {
        return map.LogicalRules.Select((_, i) => LogicalRuleHealth.Healthy(i)).ToArray();
    }

    [Fact]
    public void Logical_rule_enumerates_physical_then_reading_order_plus_one_each()
    {
        DocumentBoxId a = Box("01");
        DocumentBoxId b = Box("02");
        DocumentBoxId c = Box("03");
        DocumentBoxId d = Box("04");
        DocumentPageMap map = new(DocId, [],
            [new LogicalPaginationRule(5, 7, 1,
                [
                    new LogicalPageReadingEntry(a, 5),
                    new LogicalPageReadingEntry(b, 5),
                    new LogicalPageReadingEntry(c, 6),
                    new LogicalPageReadingEntry(d, 7)
                ])],
            [], []);

        PageMapValidator.Validate(map).Should().BeEmpty();

        IReadOnlyList<LogicalRuleHealth> health = Healthy(map);
        PageMapResolver.ResolveForward(map, health, new NavigationPosition(5, a)).PrintedNumber.Should().Be(1);
        PageMapResolver.ResolveForward(map, health, new NavigationPosition(5, b)).PrintedNumber.Should().Be(2);
        PageMapResolver.ResolveForward(map, health, new NavigationPosition(6, c)).PrintedNumber.Should().Be(3);
        PageMapResolver.ResolveForward(map, health, new NavigationPosition(7, d)).PrintedNumber.Should().Be(4);

        PageMapResolver.ResolveReverse(map, health, 3).Should()
            .BeEquivalentTo<NavigationPosition>([new NavigationPosition(6, c)]);
    }

    [Fact]
    public void Logical_rule_trusts_caller_reading_order_without_resorting()
    {
        // The caller supplies the reading order; even when it is not in physical order the
        // resolver must NOT sort by Box ID, PdfPage, or geometry — index in the list wins.
        DocumentBoxId a = Box("01");
        DocumentBoxId b = Box("02");
        DocumentBoxId c = Box("03");
        DocumentPageMap map = new(DocId, [],
            [new LogicalPaginationRule(5, 6, 7,
                [
                    new LogicalPageReadingEntry(b, 6),
                    new LogicalPageReadingEntry(a, 5),
                    new LogicalPageReadingEntry(c, 6)
                ])],
            [], []);

        IReadOnlyList<LogicalRuleHealth> health = Healthy(map);
        PageMapResolver.ResolveForward(map, health, new NavigationPosition(6, b)).PrintedNumber.Should().Be(7);
        PageMapResolver.ResolveForward(map, health, new NavigationPosition(5, a)).PrintedNumber.Should().Be(8);
        PageMapResolver.ResolveForward(map, health, new NavigationPosition(6, c)).PrintedNumber.Should().Be(9);
    }

    [Fact]
    public void Logical_forward_reports_unknown_logical_page_without_throwing()
    {
        DocumentBoxId a = Box("01");
        DocumentPageMap map = new(DocId, [],
            [new LogicalPaginationRule(5, 5, 1, [new LogicalPageReadingEntry(a, 5)])],
            [], []);

        ForwardResolution resolution = PageMapResolver.ResolveForward(
            map, Healthy(map), new NavigationPosition(5, Box("99")));

        resolution.PrintedNumber.Should().BeNull();
        resolution.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "logical_page_unknown");
    }

    [Fact]
    public void Validator_rejects_partially_overlapping_physical_and_logical_ranges()
    {
        DocumentPageMap map = new(DocId,
            [new PhysicalPaginationRule(6, 10, 1)],
            [new LogicalPaginationRule(5, 6, 50,
                [new LogicalPageReadingEntry(Box("01"), 5), new LogicalPageReadingEntry(Box("02"), 6)])],
            [], []);

        PageMapValidator.Validate(map).Should().Contain(error => error.Code == "mixed_rule_kinds");
    }

    [Fact]
    public void Validator_accepts_adjacent_non_overlapping_rule_kinds()
    {
        DocumentPageMap map = new(DocId,
            [new PhysicalPaginationRule(1, 4, 1)],
            [new LogicalPaginationRule(5, 6, 50,
                [new LogicalPageReadingEntry(Box("01"), 5), new LogicalPageReadingEntry(Box("02"), 6)])],
            [], []);

        PageMapValidator.Validate(map).Should().BeEmpty();
    }

    [Fact]
    public void Validator_reports_each_physical_page_missing_logical_pages()
    {
        // Pages 6 and 8 have no logical pages inside the [5, 8] logical range: the range must
        // be re-split or given a complete logical division instead of silently counting them.
        DocumentPageMap map = new(DocId, [],
            [new LogicalPaginationRule(5, 8, 1,
                [
                    new LogicalPageReadingEntry(Box("01"), 5),
                    new LogicalPageReadingEntry(Box("02"), 7)
                ])],
            [], []);

        IReadOnlyList<PageMapValidationError> errors = PageMapValidator.Validate(map);
        errors.Where(error => error.Code == "logical_range_gap").Should().HaveCount(2);
    }

    [Fact]
    public void Validator_rejects_empty_reading_order()
    {
        DocumentPageMap map = new(DocId, [],
            [new LogicalPaginationRule(5, 6, 1, [])],
            [], []);

        PageMapValidator.Validate(map).Should().Contain(error => error.Code == "reading_order_empty");
    }

    [Fact]
    public void Validator_rejects_illegal_range_shapes()
    {
        DocumentPageMap map = new(DocId,
            [
                new PhysicalPaginationRule(0, 4, 1),
                new PhysicalPaginationRule(6, 5, 1),
                new PhysicalPaginationRule(7, 8, 0)
            ],
            [
                new LogicalPaginationRule(0, 6, 1, [new LogicalPageReadingEntry(Box("01"), 1)]),
                new LogicalPaginationRule(7, 6, 1, [new LogicalPageReadingEntry(Box("02"), 7)]),
                new LogicalPaginationRule(8, 9, 0, [new LogicalPageReadingEntry(Box("03"), 8)])
            ],
            [], []);

        IReadOnlyList<PageMapValidationError> errors = PageMapValidator.Validate(map);
        errors.Where(error => error.Code == "range_start").Should().HaveCount(2);
        errors.Where(error => error.Code == "range_order").Should().HaveCount(2);
        errors.Where(error => error.Code == "printed_start").Should().HaveCount(2);
    }

    [Fact]
    public void Validator_rejects_label_targets_with_zero_or_two_sides_set()
    {
        DocumentPageMap map = new(DocId, [new PhysicalPaginationRule(1, 2, 1)], [],
            [
                new PageLabelOverride(new PageLabelTarget(null, null), "x"),
                new PageLabelOverride(new PageLabelTarget(1, Box("01")), "y"),
                new PageLabelOverride(new PageLabelTarget(0, null), "z")
            ],
            []);

        IReadOnlyList<PageMapValidationError> errors = PageMapValidator.Validate(map);
        errors.Where(error => error.Code == "label_target").Should().HaveCount(3);
    }

    [Fact]
    public void Validator_rejects_chapter_target_on_page_zero()
    {
        DocumentPageMap map = new(DocId, [new PhysicalPaginationRule(1, 2, 1)], [], [],
            [ChapterNode.Leaf("ch1", "Title", new NavigationPosition(0))]);

        PageMapValidator.Validate(map).Should().Contain(error => error.Code == "chapter_target");
    }

    [Fact]
    public void Chapter_with_logical_target_resolves_through_the_logical_rule()
    {
        DocumentBoxId a = Box("01");
        DocumentPageMap map = new(DocId, [],
            [new LogicalPaginationRule(5, 6, 10,
                [new LogicalPageReadingEntry(a, 5), new LogicalPageReadingEntry(Box("02"), 6)])],
            [],
            [ChapterNode.Leaf("ch1", "第一章", new NavigationPosition(5, a))]);

        ResolvedChapterNode chapter = PageMapResolver.ResolveChapters(map, Healthy(map)).Single();

        chapter.PrintedNumber.Should().Be(10);
        chapter.DisplayLabel.Should().Be("10");
        chapter.Target.Should().Be(new NavigationPosition(5, a));
        chapter.Diagnostics.Should().NotContain(diagnostic => diagnostic.Code == "chapter_target_degraded");
    }

    [Fact]
    public void Reverse_lookup_skips_paused_logical_rules_but_keeps_physical_ones()
    {
        DocumentBoxId a = Box("01");
        DocumentPageMap map = new(DocId,
            [new PhysicalPaginationRule(1, 2, 1)],
            [new LogicalPaginationRule(3, 3, 5, [new LogicalPageReadingEntry(a, 3)])],
            [], []);
        IReadOnlyList<LogicalRuleHealth> health =
            [new LogicalRuleHealth(0, true, [new MapDiagnostic("logical_page_deleted", "gone")])];

        // The paused logical rule contributes nothing…
        PageMapResolver.ResolveReverse(map, health, 5).Should().BeEmpty();
        // …while the healthy physical rule keeps resolving.
        PageMapResolver.ResolveReverse(map, health, 2).Should()
            .BeEquivalentTo<NavigationPosition>([new NavigationPosition(2)]);
    }

    [Fact]
    public void Duplicate_printed_numbers_across_rule_kinds_return_all_positions()
    {
        DocumentBoxId a = Box("01");
        DocumentPageMap map = new(DocId,
            [new PhysicalPaginationRule(1, 2, 5)],
            [new LogicalPaginationRule(3, 4, 5,
                [new LogicalPageReadingEntry(a, 3), new LogicalPageReadingEntry(Box("02"), 4)])],
            [], []);

        IReadOnlyList<NavigationPosition> positions =
            PageMapResolver.ResolveReverse(map, Healthy(map), 5);
        positions.Should().BeEquivalentTo<NavigationPosition>(
        [
            new NavigationPosition(1),
            new NavigationPosition(3, a)
        ]);

        PageMapResolver.ResolveReverse(map, Healthy(map), 6).Should()
            .BeEquivalentTo<NavigationPosition>(
            [
                new NavigationPosition(2),
                new NavigationPosition(4, Box("02"))
            ]);
    }
}
