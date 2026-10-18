using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Pagination;

namespace Patchouli.Tests;

/// <summary>
/// Pure arithmetic tests for <see cref="PageMapResolver"/> over physical pagination rules:
/// the printed = printed_start + pdf_page - start formula, inclusive bounds, no
/// extrapolation, split ranges for inserts/gaps/renumbering, duplicate printed numbers
/// resolving to every matching position, and explicit non-integer label overrides.
/// </summary>
public sealed class PageMapResolverMathTests
{
    private static DocumentInstanceId DocId => new(new Guid("11111111-1111-1111-1111-111111111111"));

    private static DocumentBoxId Box(string suffix)
    {
        return new DocumentBoxId(Guid.Parse($"22222222-2222-2222-2222-2222222222{suffix}"));
    }

    private static IReadOnlyList<LogicalRuleHealth> NoLogicalRules()
    {
        return [];
    }

    [Fact]
    public void Physical_100_page_single_rule_middle_pages_are_correct()
    {
        DocumentPageMap map = new(DocId, [new PhysicalPaginationRule(1, 100, 1)], [], [], []);

        PageMapResolver.ResolveForward(map, NoLogicalRules(), new NavigationPosition(1)).PrintedNumber.Should().Be(1);
        PageMapResolver.ResolveForward(map, NoLogicalRules(), new NavigationPosition(50)).PrintedNumber.Should().Be(50);
        PageMapResolver.ResolveForward(map, NoLogicalRules(), new NavigationPosition(100)).PrintedNumber.Should().Be(100);
    }

    [Fact]
    public void Physical_100_page_rule_with_offset_middle_pages_are_correct()
    {
        // Front matter i..xii occupy printed 1..12; body starts at printed 13 on PDF page 13.
        DocumentPageMap map = new(DocId, [new PhysicalPaginationRule(13, 112, 13)], [], [], []);

        PageMapResolver.ResolveForward(map, NoLogicalRules(), new NavigationPosition(13)).PrintedNumber.Should().Be(13);
        PageMapResolver.ResolveForward(map, NoLogicalRules(), new NavigationPosition(62)).PrintedNumber.Should().Be(62);
        PageMapResolver.ResolveForward(map, NoLogicalRules(), new NavigationPosition(112)).PrintedNumber.Should().Be(112);
    }

    [Fact]
    public void Physical_pages_outside_the_range_never_extrapolate()
    {
        DocumentPageMap map = new(DocId, [new PhysicalPaginationRule(1, 100, 1)], [], [], []);

        ForwardResolution before = PageMapResolver.ResolveForward(map, NoLogicalRules(), new NavigationPosition(0));
        before.PrintedNumber.Should().BeNull();
        before.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "position_invalid");

        ForwardResolution after = PageMapResolver.ResolveForward(map, NoLogicalRules(), new NavigationPosition(101));
        after.PrintedNumber.Should().BeNull();
        after.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "no_covering_rule");
        after.Label.Should().BeNull();
    }

    [Fact]
    public void Split_ranges_express_insert_gap_and_renumbering()
    {
        // Pages 1-10: body printed 1-10.
        // Pages 11-12: inserted supplement, renumbered to printed 101-102.
        // Pages 13-20: body continues at printed 11-18.
        // Page 21: missing entirely (no rule covers it).
        // Pages 22-30: body continues at printed 19-27.
        DocumentPageMap map = new(DocId,
            [
                new PhysicalPaginationRule(1, 10, 1),
                new PhysicalPaginationRule(11, 12, 101),
                new PhysicalPaginationRule(13, 20, 11),
                new PhysicalPaginationRule(22, 30, 19)
            ],
            [], [], []);

        PageMapResolver.ResolveForward(map, NoLogicalRules(), new NavigationPosition(10)).PrintedNumber.Should().Be(10);
        PageMapResolver.ResolveForward(map, NoLogicalRules(), new NavigationPosition(11)).PrintedNumber.Should().Be(101);
        PageMapResolver.ResolveForward(map, NoLogicalRules(), new NavigationPosition(12)).PrintedNumber.Should().Be(102);
        PageMapResolver.ResolveForward(map, NoLogicalRules(), new NavigationPosition(13)).PrintedNumber.Should().Be(11);
        PageMapResolver.ResolveForward(map, NoLogicalRules(), new NavigationPosition(20)).PrintedNumber.Should().Be(18);

        ForwardResolution missing = PageMapResolver.ResolveForward(map, NoLogicalRules(), new NavigationPosition(21));
        missing.PrintedNumber.Should().BeNull();
        missing.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "no_covering_rule");

        PageMapResolver.ResolveForward(map, NoLogicalRules(), new NavigationPosition(22)).PrintedNumber.Should().Be(19);
        PageMapResolver.ResolveForward(map, NoLogicalRules(), new NavigationPosition(30)).PrintedNumber.Should().Be(27);
    }

    [Fact]
    public void Split_ranges_reverse_lookup_hits_the_right_split()
    {
        DocumentPageMap map = new(DocId,
            [
                new PhysicalPaginationRule(1, 10, 1),
                new PhysicalPaginationRule(11, 12, 101),
                new PhysicalPaginationRule(13, 20, 11),
                new PhysicalPaginationRule(22, 30, 19)
            ],
            [], [], []);

        PageMapResolver.ResolveReverse(map, NoLogicalRules(), 101).Should()
            .BeEquivalentTo<NavigationPosition>([new NavigationPosition(11)]);
        PageMapResolver.ResolveReverse(map, NoLogicalRules(), 11).Should()
            .BeEquivalentTo<NavigationPosition>([new NavigationPosition(13)]);
        PageMapResolver.ResolveReverse(map, NoLogicalRules(), 19).Should()
            .BeEquivalentTo<NavigationPosition>([new NavigationPosition(22)]);
        PageMapResolver.ResolveReverse(map, NoLogicalRules(), 12).Should()
            .BeEquivalentTo<NavigationPosition>([new NavigationPosition(14)]);
    }

    [Fact]
    public void Reverse_returns_every_position_sharing_a_printed_number()
    {
        // The same printed number legitimately appears at several positions; there is no
        // global uniqueness, so reverse lookup must return ALL of them.
        DocumentPageMap map = new(DocId,
            [
                new PhysicalPaginationRule(1, 4, 1),
                new PhysicalPaginationRule(10, 11, 3),
                new PhysicalPaginationRule(20, 21, 3)
            ],
            [], [], []);

        PageMapResolver.ResolveReverse(map, NoLogicalRules(), 1).Should()
            .BeEquivalentTo<NavigationPosition>([new NavigationPosition(1)]);
        PageMapResolver.ResolveReverse(map, NoLogicalRules(), 3).Should()
            .BeEquivalentTo<NavigationPosition>(
            [
                new NavigationPosition(3),
                new NavigationPosition(10),
                new NavigationPosition(20)
            ]);
        PageMapResolver.ResolveReverse(map, NoLogicalRules(), 4).Should()
            .BeEquivalentTo<NavigationPosition>(
            [
                new NavigationPosition(4),
                new NavigationPosition(11),
                new NavigationPosition(21)
            ]);
        PageMapResolver.ResolveReverse(map, NoLogicalRules(), 99).Should().BeEmpty();
    }

    [Fact]
    public void Reverse_rejects_non_positive_printed_numbers()
    {
        DocumentPageMap map = new(DocId, [new PhysicalPaginationRule(1, 100, 1)], [], [], []);

        PageMapResolver.ResolveReverse(map, NoLogicalRules(), 0).Should().BeEmpty();
        PageMapResolver.ResolveReverse(map, NoLogicalRules(), -7).Should().BeEmpty();
    }

    [Fact]
    public void Explicit_labels_override_computed_values_with_non_integer_strings()
    {
        DocumentPageMap map = new(DocId, [new PhysicalPaginationRule(1, 5, 1)], [],
            [
                new PageLabelOverride(new PageLabelTarget(1, null), "i"),
                new PageLabelOverride(new PageLabelTarget(2, null), "扉页")
            ],
            []);

        ForwardResolution first = PageMapResolver.ResolveForward(map, NoLogicalRules(), new NavigationPosition(1));
        first.PrintedNumber.Should().Be(1);
        first.Label.Should().Be("i");

        ForwardResolution second = PageMapResolver.ResolveForward(map, NoLogicalRules(), new NavigationPosition(2));
        second.PrintedNumber.Should().Be(2);
        second.Label.Should().Be("扉页");

        // Pages without an override keep the computed decimal label.
        ForwardResolution third = PageMapResolver.ResolveForward(map, NoLogicalRules(), new NavigationPosition(3));
        third.PrintedNumber.Should().Be(3);
        third.Label.Should().Be("3");
    }

    [Fact]
    public void Logical_page_label_overrides_computed_value_for_that_logical_position_only()
    {
        DocumentBoxId box = Box("01");
        DocumentPageMap map = new(DocId, [],
            [new LogicalPaginationRule(5, 5, 7, [new LogicalPageReadingEntry(box, 5)])],
            [new PageLabelOverride(new PageLabelTarget(null, box), "A-1")],
            []);
        IReadOnlyList<LogicalRuleHealth> health = [LogicalRuleHealth.Healthy(0)];

        ForwardResolution logical = PageMapResolver.ResolveForward(map, health, new NavigationPosition(5, box));
        logical.PrintedNumber.Should().Be(7);
        logical.Label.Should().Be("A-1");

        // The same physical page addressed without the logical page keeps no label: the
        // logical override must not leak onto the bare physical position.
        ForwardResolution bare = PageMapResolver.ResolveForward(map, health, new NavigationPosition(5));
        bare.PrintedNumber.Should().BeNull();
        bare.Label.Should().BeNull();
    }

    [Fact]
    public void Resolver_reports_mixed_rule_kinds_defensively_instead_of_picking_one()
    {
        // The validator rejects this shape, but the resolver must still degrade gracefully
        // if it ever sees one: no printed number, a mixed_rule_kinds diagnostic, no throw.
        DocumentBoxId box = Box("01");
        DocumentPageMap map = new(DocId,
            [new PhysicalPaginationRule(1, 10, 1)],
            [new LogicalPaginationRule(5, 6, 50,
                [new LogicalPageReadingEntry(box, 5), new LogicalPageReadingEntry(Box("02"), 6)])],
            [], []);

        ForwardResolution resolution =
            PageMapResolver.ResolveForward(map, NoLogicalRules(), new NavigationPosition(5, box));
        resolution.PrintedNumber.Should().BeNull();
        resolution.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "mixed_rule_kinds");
    }
}
