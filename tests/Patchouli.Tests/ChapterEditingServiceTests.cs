using System.Data.Common;
using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Pagination;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;

namespace Patchouli.Tests;

public sealed class ChapterEditingServiceTests
{
    [Fact]
    public async Task Chapter_edit_preserves_rules_labels_and_publishes_only_after_commit()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        LibraryRevisionService revisions = new(fixture.ConnectionFactory);
        DocumentPageMapService service = new(fixture.ConnectionFactory, fixture.Clock, fixture.Documents,
            fixture.Pages, fixture.Trees, revisions);
        DocumentPageMap initial = DocumentPageMap.Empty(fixture.DocumentId) with
        {
            PhysicalRules = [new PhysicalPaginationRule(1, 3, 10)],
            PageLabels = [new PageLabelOverride(new PageLabelTarget(1, null), "封面")]
        };
        (await service.ReplaceFromYamlAsync(fixture.DocumentId, DocumentPageMapYamlCodec.Encode(initial, true)))
            .IsSuccess.Should().BeTrue();
        ChapterEditingSnapshot before = (await service.GetChapterEditingSnapshotAsync(fixture.DocumentId)).Value;
        int published = 0;
        revisions.ChangeCommitted += (_, _) => published++;

        Result<ChapterEditingSnapshot> saved = await service.SaveChaptersAsync(fixture.DocumentId,
            new ChapterSaveRequest(before.VersionToken,
                [ChapterNode.Leaf("manual", "Introduction", new NavigationPosition(1, fixture.BoxA))]));

        saved.IsSuccess.Should().BeTrue(saved.ErrorMessage);
        saved.Value.Definition.PhysicalRules.Should().Equal(initial.PhysicalRules);
        saved.Value.Definition.PageLabels.Should().Equal(initial.PageLabels);
        saved.Value.Definition.Chapters.Should().ContainSingle().Which.Title.Should().Be("Introduction");
        published.Should().Be(1);
        (await service.GetMapYamlAsync(fixture.DocumentId)).Value.Should().Contain("Introduction");
    }

    [Fact]
    public async Task Full_map_content_token_detects_external_changes_even_with_fixed_clock()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        DocumentPageMapService service = fixture.CreateMapService();
        ChapterEditingSnapshot before = (await service.GetChapterEditingSnapshotAsync(fixture.DocumentId)).Value;
        DocumentPageMap incoming = DocumentPageMap.Empty(fixture.DocumentId) with
        {
            PhysicalRules = [new PhysicalPaginationRule(1, 3, 20)]
        };
        (await service.ReplaceFromYamlAsync(fixture.DocumentId, DocumentPageMapYamlCodec.Encode(incoming, true)))
            .IsSuccess.Should().BeTrue();

        Result<ChapterEditingSnapshot> saved = await service.SaveChaptersAsync(fixture.DocumentId,
            new ChapterSaveRequest(before.VersionToken, [ChapterNode.Leaf("one", "One", new NavigationPosition(1))]));

        saved.ErrorCode.Should().Be(AppErrorCodes.Conflict);
        ChapterEditingSnapshot current = (await service.GetChapterEditingSnapshotAsync(fixture.DocumentId)).Value;
        current.Definition.Chapters.Should().BeEmpty();
        current.Definition.PhysicalRules.Should().Equal(incoming.PhysicalRules);
    }

    [Fact]
    public async Task Rename_preserves_stale_logical_target_and_unrelated_stale_rule()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        DocumentPageMapService service = fixture.CreateMapService();
        DocumentPageMap initial = DocumentPageMap.Empty(fixture.DocumentId) with
        {
            LogicalRules =
            [
                new LogicalPaginationRule(1, 1, 1,
                    [new LogicalPageReadingEntry(fixture.BoxA, 1), new LogicalPageReadingEntry(fixture.BoxB, 1)])
            ],
            Chapters = [ChapterNode.Leaf("old", "Old", new NavigationPosition(1, fixture.BoxA))]
        };
        (await service.ReplaceFromYamlAsync(fixture.DocumentId, DocumentPageMapYamlCodec.Encode(initial, true)))
            .IsSuccess.Should().BeTrue();
        await fixture.CommitPageAsync(0, PageMapTestDocument.LogicalSeed(fixture.BoxB, 0));
        ChapterEditingSnapshot before = (await service.GetChapterEditingSnapshotAsync(fixture.DocumentId)).Value;

        Result<ChapterEditingSnapshot> saved = await service.SaveChaptersAsync(fixture.DocumentId,
            new ChapterSaveRequest(before.VersionToken, [before.Definition.Chapters[0] with { Title = "Renamed" }]));

        saved.IsSuccess.Should().BeTrue(saved.ErrorMessage);
        saved.Value.Definition.Chapters[0].Target.Should().Be(initial.Chapters[0].Target);
        saved.Value.Definition.LogicalRules[0].ReadingOrder.Should().Equal(initial.LogicalRules[0].ReadingOrder);
    }

    [Fact]
    public async Task New_chapter_cannot_reuse_an_existing_stale_target_under_another_id()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        DocumentPageMapService service = fixture.CreateMapService();
        ChapterEditingSnapshot before = (await service.GetChapterEditingSnapshotAsync(fixture.DocumentId)).Value;
        Result<ChapterEditingSnapshot> first = await service.SaveChaptersAsync(fixture.DocumentId,
            new ChapterSaveRequest(before.VersionToken,
                [ChapterNode.Leaf("old", "Old", new NavigationPosition(1, fixture.BoxA))]));
        first.IsSuccess.Should().BeTrue(first.ErrorMessage);
        await fixture.CommitPageAsync(0, PageMapTestDocument.LogicalSeed(fixture.BoxB, 0));

        Result<ChapterEditingSnapshot> saved = await service.SaveChaptersAsync(fixture.DocumentId,
            new ChapterSaveRequest(first.Value.VersionToken,
                [ChapterNode.Leaf("new", "New", new NavigationPosition(1, fixture.BoxA))]));

        saved.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        (await service.GetChapterEditingSnapshotAsync(fixture.DocumentId)).Value.Definition.Chapters[0].ChapterId
            .Should().Be("old");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task Out_of_bounds_target_is_rejected_without_partial_save(int page)
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        DocumentPageMapService service = fixture.CreateMapService();
        ChapterEditingSnapshot before = (await service.GetChapterEditingSnapshotAsync(fixture.DocumentId)).Value;

        Result<ChapterEditingSnapshot> saved = await service.SaveChaptersAsync(fixture.DocumentId,
            new ChapterSaveRequest(before.VersionToken,
                [ChapterNode.Leaf("bad", "Bad", new NavigationPosition(page))]));

        saved.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        (await service.GetChapterEditingSnapshotAsync(fixture.DocumentId)).Value.VersionToken.Should()
            .Be(before.VersionToken);
    }

    [Fact]
    public async Task Logical_target_must_be_on_the_correct_page_and_have_logical_type()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        DocumentPageMapService service = fixture.CreateMapService();
        ChapterEditingSnapshot before = (await service.GetChapterEditingSnapshotAsync(fixture.DocumentId)).Value;
        foreach (NavigationPosition target in new[]
                     { new NavigationPosition(2, fixture.BoxA), new NavigationPosition(1, fixture.TextBox) })
        {
            Result<ChapterEditingSnapshot> saved = await service.SaveChaptersAsync(fixture.DocumentId,
                new ChapterSaveRequest(before.VersionToken, [ChapterNode.Leaf("bad", "Bad", target)]));
            saved.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        }
    }

    [Fact]
    public async Task Cyclic_chapter_graph_is_rejected_before_recursive_validation()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        DocumentPageMapService service = fixture.CreateMapService();
        ChapterEditingSnapshot before = (await service.GetChapterEditingSnapshotAsync(fixture.DocumentId)).Value;
        List<ChapterNode> children = [];
        ChapterNode cycle = new("cycle", "Cycle", new NavigationPosition(1), children);
        children.Add(cycle);

        Result<ChapterEditingSnapshot> saved = await service.SaveChaptersAsync(fixture.DocumentId,
            new ChapterSaveRequest(before.VersionToken, [cycle]));

        saved.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Scan_apply_rejects_changed_page_revision_and_reordered_page_expectations()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        DocumentPageMapService service = fixture.CreateMapService();
        ChapterEditingSnapshot before = (await service.GetChapterEditingSnapshotAsync(fixture.DocumentId)).Value;
        List<ChapterPageRevisionExpectation> versions = [];
        for (int index = 0; index < fixture.PageIds.Count; index++)
        {
            Result<DocumentTreeRevision> revision =
                await fixture.Trees.GetCurrentRevisionAsync(fixture.DocumentId, fixture.PageIds[index]);
            versions.Add(new ChapterPageRevisionExpectation(fixture.PageIds[index], index + 1,
                revision.Value.TreeRevisionId));
        }

        Result<ChapterEditingSnapshot> reordered = await service.SaveChaptersAsync(fixture.DocumentId,
            new ChapterSaveRequest(before.VersionToken, [], versions.AsEnumerable().Reverse().ToArray()));
        reordered.ErrorCode.Should().Be(AppErrorCodes.Conflict);
        await fixture.CommitPageAsync(0, PageMapTestDocument.LogicalSeed(fixture.BoxA, 0));
        Result<ChapterEditingSnapshot> changed = await service.SaveChaptersAsync(fixture.DocumentId,
            new ChapterSaveRequest(before.VersionToken, [], versions));
        changed.ErrorCode.Should().Be(AppErrorCodes.Conflict);
    }

    [Fact]
    public async Task Physical_chapter_can_be_created_without_any_ocr_and_scan_tracks_unrecognized_pages()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync(4);
        DocumentPageMapService service = fixture.CreateMapService();
        ChapterEditingSnapshot before = (await service.GetChapterEditingSnapshotAsync(fixture.DocumentId)).Value;
        List<ChapterPageRevisionExpectation> versions = [];
        for (int index = 0; index < fixture.PageIds.Count; index++)
        {
            Result<DocumentTreeRevision> revision =
                await fixture.Trees.GetCurrentRevisionAsync(fixture.DocumentId, fixture.PageIds[index]);
            versions.Add(new ChapterPageRevisionExpectation(fixture.PageIds[index], index + 1,
                revision.IsSuccess ? revision.Value.TreeRevisionId : null));
        }

        Result<ChapterEditingSnapshot> saved = await service.SaveChaptersAsync(fixture.DocumentId,
            new ChapterSaveRequest(before.VersionToken,
                [ChapterNode.Leaf("manual", "Unrecognized page", new NavigationPosition(4))], versions));
        saved.IsSuccess.Should().BeTrue(saved.ErrorMessage);
        await fixture.CommitPageAsync(3, PageMapTestDocument.LogicalSeed(DocumentBoxId.New(), 0));

        Result<ChapterEditingSnapshot> stale = await service.SaveChaptersAsync(fixture.DocumentId,
            new ChapterSaveRequest(saved.Value.VersionToken, saved.Value.Definition.Chapters, versions));
        stale.ErrorCode.Should().Be(AppErrorCodes.Conflict);
    }

    [Fact]
    public async Task Revision_failure_rolls_back_the_entire_chapter_save()
    {
        await using PageMapTestDocument fixture = await PageMapTestDocument.CreateAsync();
        FailingRevisionService revisions = new();
        DocumentPageMapService service = new(fixture.ConnectionFactory, fixture.Clock, fixture.Documents,
            fixture.Pages, fixture.Trees, revisions);
        ChapterEditingSnapshot before = (await service.GetChapterEditingSnapshotAsync(fixture.DocumentId)).Value;

        Result<ChapterEditingSnapshot> saved = await service.SaveChaptersAsync(fixture.DocumentId,
            new ChapterSaveRequest(before.VersionToken, [ChapterNode.Leaf("one", "One", new NavigationPosition(1))]));

        saved.IsFailure.Should().BeTrue();
        (await service.GetChapterEditingSnapshotAsync(fixture.DocumentId)).Value.VersionToken.Should()
            .Be(before.VersionToken);
        revisions.Published.Should().BeFalse();
    }

    private sealed class FailingRevisionService : ILibraryRevisionService
    {
        public bool Published { get; private set; }

        public event EventHandler<LibraryRevisionCommittedEventArgs>? ChangeCommitted
        {
            add { }
            remove { }
        }

        public Task<Result<long>> GetCurrentRevisionAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<long>.Success(0));
        }

        public Task<Result<long>> CommitAsync(LibraryChangeSet changeSet, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<long>.Failure(AppErrorCodes.DatabaseError, "Injected revision failure"));
        }

        public Task<Result<LibraryChangeSet>> IncrementInTransactionAsync(DbConnection connection,
            DbTransaction transaction,
            LibraryChangeSet changeSet, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<LibraryChangeSet>.Failure(AppErrorCodes.DatabaseError,
                "Injected revision failure"));
        }

        public void PublishCommitted(LibraryChangeSet changeSet)
        {
            Published = true;
        }
    }
}
