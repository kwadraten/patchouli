using FluentAssertions;
using Patchouli.Core.Pagination;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Mcp;

namespace Patchouli.Tests;

public sealed class ChapterEditingIndependentTests
{
    [Fact]
    public async Task Concurrent_writers_with_the_same_token_have_exactly_one_winner()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        DocumentPageMapService first = fixture.CreateMapService();
        DocumentPageMapService second = fixture.CreateMapService();
        ChapterEditingSnapshot snapshot = (await first.GetChapterEditingSnapshotAsync(fixture.DocumentId)).Value;

        Result<ChapterEditingSnapshot>[] results = await Task.WhenAll(
            first.SaveChaptersAsync(fixture.DocumentId, new ChapterSaveRequest(snapshot.VersionToken,
                [ChapterNode.Leaf("first", "First", new NavigationPosition(1))])),
            second.SaveChaptersAsync(fixture.DocumentId, new ChapterSaveRequest(snapshot.VersionToken,
                [ChapterNode.Leaf("second", "Second", new NavigationPosition(2))])));

        results.Count(result => result.IsSuccess).Should().Be(1);
        results.Single(result => result.IsFailure).ErrorCode.Should().Be(AppErrorCodes.Conflict);
        ChapterEditingSnapshot final = (await first.GetChapterEditingSnapshotAsync(fixture.DocumentId)).Value;
        final.Definition.Chapters.Should().ContainSingle();
        final.Definition.Chapters[0].Should()
            .BeEquivalentTo(results.Single(result => result.IsSuccess).Value.Definition.Chapters[0],
                options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task Clearing_all_chapters_preserves_non_chapter_map_data()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        DocumentPageMapService service = fixture.CreateMapService();
        DocumentPageMap original = DocumentPageMap.Empty(fixture.DocumentId) with
        {
            PhysicalRules = [new PhysicalPaginationRule(1, 3, 9)],
            PageLabels = [new PageLabelOverride(new PageLabelTarget(1, null), "封面")],
            Chapters = [ChapterNode.Leaf("root", "Root", new NavigationPosition(1))]
        };
        (await service.ReplaceFromYamlAsync(fixture.DocumentId, DocumentPageMapYamlCodec.Encode(original, true)))
            .IsSuccess.Should().BeTrue();
        ChapterEditingSnapshot snapshot = (await service.GetChapterEditingSnapshotAsync(fixture.DocumentId)).Value;

        Result<ChapterEditingSnapshot> result = await service.SaveChaptersAsync(fixture.DocumentId,
            new ChapterSaveRequest(snapshot.VersionToken, []));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.Definition.Should().BeEquivalentTo(original with { Chapters = [] });
        (await service.GetResolvedAsync(fixture.DocumentId)).Value.Should().NotBeNull();
    }

    [Fact]
    public async Task Clearing_chapters_from_a_chapters_only_map_restores_the_absent_snapshot()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        DocumentPageMapService service = fixture.CreateMapService();
        ChapterEditingSnapshot empty = (await service.GetChapterEditingSnapshotAsync(fixture.DocumentId)).Value;
        ChapterEditingSnapshot populated = (await service.SaveChaptersAsync(fixture.DocumentId,
            new ChapterSaveRequest(empty.VersionToken,
                [ChapterNode.Leaf("root", "Root", new NavigationPosition(1))]))).Value;

        Result<ChapterEditingSnapshot> cleared = await service.SaveChaptersAsync(fixture.DocumentId,
            new ChapterSaveRequest(populated.VersionToken, []));

        cleared.IsSuccess.Should().BeTrue(cleared.ErrorMessage);
        cleared.Value.Definition.IsEmpty.Should().BeTrue();
        cleared.Value.VersionToken.Should().Be(empty.VersionToken);
        (await service.GetResolvedAsync(fixture.DocumentId)).Value.Should().BeNull();
    }
}
