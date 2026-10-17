using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Pagination;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Mcp;

namespace Patchouli.Tests;

/// <summary>
/// End-to-end logical-page invalidation through <see cref="DocumentPageMapService"/>: the
/// write-time structural signature (identity + order) is compared against the current
/// committed box trees, so text-only edits keep rules healthy while structural edits
/// (delete/add/move/reorder/disappearing endpoint) pause only the affected rule.
/// </summary>
public sealed class PageMapInvalidationServiceTests
{
    private static string MapYaml(PageMapTestDocument fixture, params ChapterNode[] chapters)
    {
        DocumentPageMap map = new(fixture.DocumentId,
            [new PhysicalPaginationRule(3, 3, 100)],
            [new LogicalPaginationRule(1, 2, 1,
                [
                    new LogicalPageReadingEntry(fixture.BoxA, 1),
                    new LogicalPageReadingEntry(fixture.BoxB, 1),
                    new LogicalPageReadingEntry(fixture.BoxC, 2)
                ])],
            [],
            chapters);
        return DocumentPageMapYamlCodec.Encode(map, true);
    }

    private static async Task<ResolvedDocumentPageMap> PutAndResolveAsync(
        PageMapTestDocument fixture, params ChapterNode[] chapters)
    {
        DocumentPageMapService maps = fixture.CreateMapService();
        Result<DocumentPageMap> replaced =
            await maps.ReplaceFromYamlAsync(fixture.DocumentId, MapYaml(fixture, chapters));
        replaced.IsSuccess.Should().BeTrue(replaced.ErrorMessage);

        Result<ResolvedDocumentPageMap?> resolved = await maps.GetResolvedAsync(fixture.DocumentId);
        resolved.IsSuccess.Should().BeTrue(resolved.ErrorMessage);
        resolved.Value.Should().NotBeNull();
        return resolved.Value!;
    }

    [Fact]
    public async Task Text_only_change_keeps_logical_rules_healthy()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        await PutAndResolveAsync(fixture);

        // Same box identities, same pages, same sibling order — only the text payload changed.
        await fixture.CommitPageAsync(0,
            PageMapTestDocument.LogicalSeed(fixture.BoxA, 0),
            PageMapTestDocument.TextSeed(fixture.TextBox, 0, "edited paragraph text", fixture.BoxA),
            PageMapTestDocument.LogicalSeed(fixture.BoxB, 1));

        ResolvedDocumentPageMap resolved = (await fixture.CreateMapService()
            .GetResolvedAsync(fixture.DocumentId)).Value!;

        resolved.RuleHealth.Should().ContainSingle().Which.NeedsRepair.Should().BeFalse();
        PageMapResolver.ResolveForward(resolved.Definition, resolved.RuleHealth,
            new NavigationPosition(1, fixture.BoxA)).PrintedNumber.Should().Be(1);
        PageMapResolver.ResolveForward(resolved.Definition, resolved.RuleHealth,
            new NavigationPosition(2, fixture.BoxC)).PrintedNumber.Should().Be(3);
    }

    [Fact]
    public async Task Deleting_a_middle_logical_page_marks_the_rule_for_repair()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        await PutAndResolveAsync(fixture);

        await fixture.CommitPageAsync(0,
            PageMapTestDocument.LogicalSeed(fixture.BoxA, 0),
            PageMapTestDocument.TextSeed(fixture.TextBox, 0, "paragraph", fixture.BoxA));

        ResolvedDocumentPageMap resolved = (await fixture.CreateMapService()
            .GetResolvedAsync(fixture.DocumentId)).Value!;

        LogicalRuleHealth health = resolved.RuleHealth.Should().ContainSingle().Subject;
        health.NeedsRepair.Should().BeTrue();
        health.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "logical_page_deleted");
        health.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "rule_paused");

        // The affected rule pauses, but the rest of the map keeps resolving and the original
        // definition is kept: the map stays readable.
        ForwardResolution paused = PageMapResolver.ResolveForward(
            resolved.Definition, resolved.RuleHealth, new NavigationPosition(1, fixture.BoxA));
        paused.PrintedNumber.Should().BeNull();
        paused.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "rule_paused");

        PageMapResolver.ResolveForward(resolved.Definition, resolved.RuleHealth, new NavigationPosition(3))
            .PrintedNumber.Should().Be(100);

        Result<string> yaml = await fixture.CreateMapService().GetMapYamlAsync(fixture.DocumentId);
        yaml.IsSuccess.Should().BeTrue(yaml.ErrorMessage);
        DocumentPageMapYamlCodec.Decode(yaml.Value, fixture.DocumentId).Value
            .LogicalRules.Should().HaveCount(1);
    }

    [Fact]
    public async Task Deleting_a_range_endpoint_reports_endpoint_missing()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        await PutAndResolveAsync(fixture);

        // BoxA is the first entry of the reading order: a range endpoint disappears.
        await fixture.CommitPageAsync(0,
            PageMapTestDocument.LogicalSeed(fixture.BoxB, 0),
            PageMapTestDocument.TextSeed(fixture.TextBox, 0, "paragraph", fixture.BoxB));

        ResolvedDocumentPageMap resolved = (await fixture.CreateMapService()
            .GetResolvedAsync(fixture.DocumentId)).Value!;

        LogicalRuleHealth health = resolved.RuleHealth.Should().ContainSingle().Subject;
        health.NeedsRepair.Should().BeTrue();
        health.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "logical_endpoint_missing");
    }

    [Fact]
    public async Task Adding_a_logical_page_inside_the_range_marks_the_rule_for_repair()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        await PutAndResolveAsync(fixture);

        DocumentBoxId added = DocumentBoxId.New();
        await fixture.CommitPageAsync(1,
            PageMapTestDocument.LogicalSeed(fixture.BoxC, 0),
            PageMapTestDocument.LogicalSeed(added, 1));

        ResolvedDocumentPageMap resolved = (await fixture.CreateMapService()
            .GetResolvedAsync(fixture.DocumentId)).Value!;

        LogicalRuleHealth health = resolved.RuleHealth.Should().ContainSingle().Subject;
        health.NeedsRepair.Should().BeTrue();
        health.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "logical_page_added");
    }

    [Fact]
    public async Task Moving_a_logical_page_to_another_page_marks_the_rule_for_repair()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        await PutAndResolveAsync(fixture);

        await fixture.CommitPageAsync(0,
            PageMapTestDocument.LogicalSeed(fixture.BoxB, 0),
            PageMapTestDocument.TextSeed(fixture.TextBox, 0, "paragraph", fixture.BoxB));
        await fixture.CommitPageAsync(1,
            PageMapTestDocument.LogicalSeed(fixture.BoxC, 0),
            PageMapTestDocument.LogicalSeed(fixture.BoxA, 1));

        ResolvedDocumentPageMap resolved = (await fixture.CreateMapService()
            .GetResolvedAsync(fixture.DocumentId)).Value!;

        LogicalRuleHealth health = resolved.RuleHealth.Should().ContainSingle().Subject;
        health.NeedsRepair.Should().BeTrue();
        health.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "logical_page_moved");
        // The moved box is still a known identity, so it must not also count as an addition.
        health.Diagnostics.Should().NotContain(diagnostic => diagnostic.Code == "logical_page_added");
    }

    [Fact]
    public async Task Reordering_logical_pages_on_one_page_marks_the_rule_for_repair()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        await PutAndResolveAsync(fixture);

        // Flip the sibling order of the two adjacent logical boxes on page 1: the current
        // sibling chain (B -> A) disagrees with the stored reading order (A, B).
        await fixture.CommitPageAsync(0,
            PageMapTestDocument.LogicalSeed(fixture.BoxB, 0),
            PageMapTestDocument.TextSeed(fixture.TextBox, 0, "paragraph", fixture.BoxB),
            PageMapTestDocument.LogicalSeed(fixture.BoxA, 1));

        ResolvedDocumentPageMap resolved = (await fixture.CreateMapService()
            .GetResolvedAsync(fixture.DocumentId)).Value!;

        LogicalRuleHealth health = resolved.RuleHealth.Should().ContainSingle().Subject;
        health.NeedsRepair.Should().BeTrue();
        health.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "logical_page_reordered");
    }

    [Fact]
    public async Task One_bad_rule_does_not_break_the_whole_map_read()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        DocumentPageMapService maps = fixture.CreateMapService();

        DocumentPageMap map = new(fixture.DocumentId,
            [new PhysicalPaginationRule(2, 3, 50)],
            [new LogicalPaginationRule(1, 1, 1, [new LogicalPageReadingEntry(fixture.BoxA, 1)])],
            [], []);
        (await maps.ReplaceFromYamlAsync(fixture.DocumentId,
            DocumentPageMapYamlCodec.Encode(map, true))).IsSuccess.Should().BeTrue();

        // Delete the only logical page of the logical rule.
        await fixture.CommitPageAsync(0,
            PageMapTestDocument.LogicalSeed(fixture.BoxB, 0),
            PageMapTestDocument.TextSeed(fixture.TextBox, 0, "paragraph", fixture.BoxB));

        Result<ResolvedDocumentPageMap?> resolved = await maps.GetResolvedAsync(fixture.DocumentId);
        resolved.IsSuccess.Should().BeTrue(resolved.ErrorMessage);
        resolved.Value.Should().NotBeNull();
        resolved.Value!.HasMap.Should().BeTrue();
        resolved.Value.RuleHealth.Should().ContainSingle().Which.NeedsRepair.Should().BeTrue();

        // The healthy physical rule keeps resolving in both directions.
        PageMapResolver.ResolveForward(resolved.Value.Definition, resolved.Value.RuleHealth,
            new NavigationPosition(2)).PrintedNumber.Should().Be(50);
        PageMapResolver.ResolveReverse(resolved.Value.Definition, resolved.Value.RuleHealth, 51).Should()
            .BeEquivalentTo<NavigationPosition>([new NavigationPosition(3)]);
    }

    [Fact]
    public async Task Chapter_with_dead_logical_target_degrades_to_physical_page()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        ResolvedDocumentPageMap before = await PutAndResolveAsync(fixture,
            ChapterNode.Leaf("chX", "消逝的章节", new NavigationPosition(1, fixture.BoxA)));

        before.Chapters.Should().ContainSingle().Which.PrintedNumber.Should().Be(1);

        // Delete the chapter's logical target box.
        await fixture.CommitPageAsync(0,
            PageMapTestDocument.LogicalSeed(fixture.BoxB, 0),
            PageMapTestDocument.TextSeed(fixture.TextBox, 0, "paragraph", fixture.BoxB));

        ResolvedDocumentPageMap after = (await fixture.CreateMapService()
            .GetResolvedAsync(fixture.DocumentId)).Value!;

        // The chapter is kept and degrades to its physical page with a diagnostic.
        ResolvedChapterNode chapter = after.Chapters.Should().ContainSingle().Subject;
        chapter.ChapterId.Should().Be("chX");
        chapter.Title.Should().Be("消逝的章节");
        chapter.PrintedNumber.Should().BeNull();
        chapter.DisplayLabel.Should().Be("PDF p.1");
        chapter.Target.Should().Be(new NavigationPosition(1, fixture.BoxA));
        chapter.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "chapter_target_degraded");
    }
}
