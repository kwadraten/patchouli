using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Pagination;

namespace Patchouli.Tests;

public sealed class PageMapResolverTests
{
    private static DocumentInstanceId DocId => new(new Guid("11111111-1111-1111-1111-111111111111"));

    private static DocumentBoxId Box(string suffix)
    {
        return new DocumentBoxId(Guid.Parse($"22222222-2222-2222-2222-2222222222{suffix}"));
    }

    private static DocumentPageMap PhysicalMap()
    {
        return new DocumentPageMap(DocId,
            [new PhysicalPaginationRule(1, 4, 1), new PhysicalPaginationRule(5, 6, 101)],
            [], [], []);
    }

    private static DocumentPageMap LogicalMap()
    {
        DocumentBoxId a = Box("01");
        DocumentBoxId b = Box("02");
        DocumentBoxId c = Box("03");
        return new DocumentPageMap(DocId, [],
            [new LogicalPaginationRule(5, 6, 1,
                [new LogicalPageReadingEntry(a, 5), new LogicalPageReadingEntry(b, 5), new LogicalPageReadingEntry(c, 6)])],
            [], []);
    }

    private static IReadOnlyList<LogicalRuleHealth> Healthy(DocumentPageMap map)
    {
        return map.LogicalRules.Select((_, i) => LogicalRuleHealth.Healthy(i)).ToArray();
    }

    [Fact]
    public void Physical_forward_uses_printed_start_plus_offset_with_inclusive_bounds()
    {
        DocumentPageMap map = PhysicalMap();

        PageMapResolver.ResolveForward(map, Healthy(map), new NavigationPosition(1)).PrintedNumber.Should().Be(1);
        PageMapResolver.ResolveForward(map, Healthy(map), new NavigationPosition(4)).PrintedNumber.Should().Be(4);
        PageMapResolver.ResolveForward(map, Healthy(map), new NavigationPosition(5)).PrintedNumber.Should().Be(101);
        PageMapResolver.ResolveForward(map, Healthy(map), new NavigationPosition(6)).PrintedNumber.Should().Be(102);
    }

    [Fact]
    public void Physical_forward_never_extrapolates_outside_ranges()
    {
        DocumentPageMap map = PhysicalMap();

        ForwardResolution resolution = PageMapResolver.ResolveForward(map, Healthy(map), new NavigationPosition(7));
        resolution.PrintedNumber.Should().BeNull();
        resolution.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "no_covering_rule");
    }

    [Fact]
    public void Logical_forward_enumerates_reading_order_plus_one_each()
    {
        DocumentPageMap map = LogicalMap();
        DocumentBoxId a = Box("01");
        DocumentBoxId b = Box("02");
        DocumentBoxId c = Box("03");

        PageMapResolver.ResolveForward(map, Healthy(map), new NavigationPosition(5, a)).PrintedNumber.Should().Be(1);
        PageMapResolver.ResolveForward(map, Healthy(map), new NavigationPosition(5, b)).PrintedNumber.Should().Be(2);
        PageMapResolver.ResolveForward(map, Healthy(map), new NavigationPosition(6, c)).PrintedNumber.Should().Be(3);
    }

    [Fact]
    public void Logical_forward_requires_a_logical_page_position()
    {
        DocumentPageMap map = LogicalMap();

        ForwardResolution resolution =
            PageMapResolver.ResolveForward(map, Healthy(map), new NavigationPosition(5));
        resolution.PrintedNumber.Should().BeNull();
        resolution.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "logical_position_required");
    }

    [Fact]
    public void Logical_forward_paused_rule_reports_diagnostic_and_stays_unresolved()
    {
        DocumentPageMap map = LogicalMap();
        var health = new[] { new LogicalRuleHealth(0, true, [new MapDiagnostic("logical_page_deleted", "x")]) };

        ForwardResolution resolution =
            PageMapResolver.ResolveForward(map, health, new NavigationPosition(5, Box("01")));
        resolution.PrintedNumber.Should().BeNull();
        resolution.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "rule_paused");
    }

    [Fact]
    public void Label_overrides_win_over_computed_values_with_logical_beating_physical()
    {
        DocumentBoxId a = Box("01");
        DocumentPageMap map = PhysicalMap() with
        {
            PageLabels =
            [
                new PageLabelOverride(new PageLabelTarget(2, null), "ii"),
                new PageLabelOverride(new PageLabelTarget(null, a), "扉页")
            ]
        };

        PageMapResolver.ResolveForward(map, Healthy(map), new NavigationPosition(1)).Label.Should().Be("1");
        PageMapResolver.ResolveForward(map, Healthy(map), new NavigationPosition(2)).Label.Should().Be("ii");
        PageMapResolver.ResolveForward(map, Healthy(map), new NavigationPosition(2)).PrintedNumber.Should().Be(2);
        PageMapResolver.ResolveForward(map, Healthy(map), new NavigationPosition(2, a)).Label.Should().Be("扉页");
    }

    [Fact]
    public void Reverse_returns_all_matching_positions_without_global_uniqueness()
    {
        DocumentBoxId a = Box("01");
        DocumentPageMap map = new(DocId,
            [new PhysicalPaginationRule(1, 2, 5)],
            [new LogicalPaginationRule(3, 3, 5, [new LogicalPageReadingEntry(a, 3)])],
            [], []);

        IReadOnlyList<NavigationPosition> positions =
            PageMapResolver.ResolveReverse(map, Healthy(map), 5);
        positions.Should().BeEquivalentTo<NavigationPosition>(
        [
            new NavigationPosition(1),
            new NavigationPosition(3, a)
        ]);
    }

    [Fact]
    public void Reverse_skips_paused_rules()
    {
        DocumentPageMap map = LogicalMap();
        var health = new[] { new LogicalRuleHealth(0, true, [new MapDiagnostic("x", "y")]) };

        PageMapResolver.ResolveReverse(map, health, 1).Should().BeEmpty();
    }

    [Fact]
    public void Validator_rejects_mixed_physical_and_logical_rules_on_the_same_page()
    {
        DocumentPageMap map = new(DocId,
            [new PhysicalPaginationRule(1, 10, 1)],
            [new LogicalPaginationRule(5, 6, 1,
                [new LogicalPageReadingEntry(Box("01"), 5), new LogicalPageReadingEntry(Box("02"), 6)])],
            [], []);

        PageMapValidator.Validate(map).Should()
            .Contain(error => error.Code == "mixed_rule_kinds");
    }

    [Fact]
    public void Validator_rejects_logical_range_with_a_physical_page_missing_logical_pages()
    {
        DocumentPageMap map = new(DocId, [],
            [new LogicalPaginationRule(5, 7, 1,
                [new LogicalPageReadingEntry(Box("01"), 5), new LogicalPageReadingEntry(Box("02"), 7)])],
            [], []);

        PageMapValidator.Validate(map).Should()
            .Contain(error => error.Code == "logical_range_gap");
    }

    [Fact]
    public void Validator_rejects_conflicting_duplicate_label_targets()
    {
        DocumentPageMap map = PhysicalMap() with
        {
            PageLabels =
            [
                new PageLabelOverride(new PageLabelTarget(2, null), "ii"),
                new PageLabelOverride(new PageLabelTarget(2, null), "II")
            ]
        };

        PageMapValidator.Validate(map).Should()
            .Contain(error => error.Code == "label_duplicate");
    }

    [Fact]
    public void Validator_rejects_duplicate_chapter_ids_and_excess_depth()
    {
        ChapterNode deep = ChapterNode.Leaf("leaf", "Leaf", new NavigationPosition(1));
        for (int i = 0; i < 20; i++)
        {
            deep = new ChapterNode($"n{i}", $"N{i}", new NavigationPosition(1), [deep]);
        }

        DocumentPageMap map = PhysicalMap() with
        {
            Chapters =
            [
                new ChapterNode("dup", "A", new NavigationPosition(1), []),
                new ChapterNode("dup", "B", new NavigationPosition(2), []),
                deep
            ]
        };

        IReadOnlyList<PageMapValidationError> errors = PageMapValidator.Validate(map);
        errors.Should().Contain(error => error.Code == "chapter_id_duplicate");
        errors.Should().Contain(error => error.Code == "chapter_depth");
    }

    [Fact]
    public void Validator_accepts_a_well_formed_map()
    {
        DocumentPageMap map = LogicalMap() with
        {
            PageLabels = [new PageLabelOverride(new PageLabelTarget(1, null), "cover")],
            Chapters =
            [
                new ChapterNode("ch1", "第一章", new NavigationPosition(5, Box("01")),
                    [ChapterNode.Leaf("ch1-1", "第一节", new NavigationPosition(6, Box("03")))])
            ]
        };

        PageMapValidator.Validate(map).Should().BeEmpty();
    }

    [Fact]
    public void Invalidation_keeps_text_only_changes_healthy()
    {
        DocumentPageMap map = LogicalMap();
        var current = new[]
        {
            new CurrentLogicalPageState(Box("01"), 5, Box("02")),
            new CurrentLogicalPageState(Box("02"), 5, Box("03")),
            new CurrentLogicalPageState(Box("03"), 6, null)
        };

        IReadOnlyList<LogicalRuleHealth> health = PageMapInvalidation.CheckHealth(map, current);
        health.Should().ContainSingle().Which.NeedsRepair.Should().BeFalse();
    }

    [Fact]
    public void Invalidation_marks_deleted_logical_pages_and_missing_endpoints()
    {
        DocumentPageMap map = LogicalMap();
        var current = new[]
        {
            new CurrentLogicalPageState(Box("02"), 5, null),
            new CurrentLogicalPageState(Box("03"), 6, null)
        };

        LogicalRuleHealth health = PageMapInvalidation.CheckHealth(map, current).Single();
        health.NeedsRepair.Should().BeTrue();
        health.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "logical_endpoint_missing");
    }

    [Fact]
    public void Invalidation_marks_added_logical_pages_and_page_moves()
    {
        DocumentPageMap map = LogicalMap();
        var current = new[]
        {
            new CurrentLogicalPageState(Box("01"), 5, null),
            new CurrentLogicalPageState(Box("02"), 6, null),
            new CurrentLogicalPageState(Box("03"), 6, null),
            new CurrentLogicalPageState(Box("04"), 5, null)
        };

        LogicalRuleHealth health = PageMapInvalidation.CheckHealth(map, current).Single();
        health.NeedsRepair.Should().BeTrue();
        health.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "logical_page_moved");
        health.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "logical_page_added");
        health.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "rule_paused");
    }

    [Fact]
    public void Invalidation_detects_sibling_chain_reorder()
    {
        DocumentPageMap map = LogicalMap();
        // Stored order on page 5: Box01 -> Box02. The current sibling chain says Box02 -> Box01.
        var current = new[]
        {
            new CurrentLogicalPageState(Box("02"), 5, Box("01")),
            new CurrentLogicalPageState(Box("01"), 5, null),
            new CurrentLogicalPageState(Box("03"), 6, null)
        };

        LogicalRuleHealth health = PageMapInvalidation.CheckHealth(map, current).Single();
        health.NeedsRepair.Should().BeTrue();
        health.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "logical_page_reordered");
    }

    [Fact]
    public void Invalidation_is_per_rule_so_one_bad_rule_does_not_break_the_map()
    {
        DocumentBoxId d = Box("04");
        DocumentPageMap map = new(DocId, [new PhysicalPaginationRule(1, 2, 1)],
            [
                new LogicalPaginationRule(3, 3, 10, [new LogicalPageReadingEntry(Box("01"), 3)]),
                new LogicalPaginationRule(4, 4, 20, [new LogicalPageReadingEntry(d, 4)])
            ],
            [], []);
        var current = new[] { new CurrentLogicalPageState(d, 4, null) };

        IReadOnlyList<LogicalRuleHealth> health = PageMapInvalidation.CheckHealth(map, current);
        health[0].NeedsRepair.Should().BeTrue();
        health[1].NeedsRepair.Should().BeFalse();

        // The healthy parts of the map keep resolving.
        PageMapResolver.ResolveForward(map, health, new NavigationPosition(1)).PrintedNumber.Should().Be(1);
        PageMapResolver.ResolveForward(map, health, new NavigationPosition(4, d)).PrintedNumber.Should().Be(20);
        PageMapResolver.ResolveReverse(map, health, 20).Should()
            .BeEquivalentTo<NavigationPosition>([new NavigationPosition(4, d)]);
    }

    [Fact]
    public void Chapters_resolve_printed_numbers_and_degrade_unknown_targets_to_physical_page()
    {
        DocumentPageMap map = PhysicalMap() with
        {
            Chapters =
            [
                new ChapterNode("ch1", "第一章", new NavigationPosition(2),
                    [ChapterNode.Leaf("ch1-1", "第一节", new NavigationPosition(99))])
            ]
        };

        IReadOnlyList<ResolvedChapterNode> resolved =
            PageMapResolver.ResolveChapters(map, Healthy(map));
        ResolvedChapterNode chapter = resolved.Single();
        chapter.PrintedNumber.Should().Be(2);
        chapter.DisplayLabel.Should().Be("2");

        ResolvedChapterNode child = chapter.Children.Single();
        child.PrintedNumber.Should().BeNull();
        child.DisplayLabel.Should().Be("PDF p.99");
        child.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "chapter_target_degraded");
    }

    [Fact]
    public void Chapters_keep_logical_targets_with_diagnostics_when_the_rule_is_paused()
    {
        DocumentPageMap map = LogicalMap() with
        {
            Chapters = [ChapterNode.Leaf("ch1", "第一章", new NavigationPosition(5, Box("01")))]
        };
        var health = new[] { new LogicalRuleHealth(0, true, [new MapDiagnostic("logical_page_deleted", "x")]) };

        ResolvedChapterNode chapter = PageMapResolver.ResolveChapters(map, health).Single();
        chapter.PrintedNumber.Should().BeNull();
        chapter.Target.Should().Be(new NavigationPosition(5, Box("01")));
        chapter.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "chapter_target_degraded");
    }
}
