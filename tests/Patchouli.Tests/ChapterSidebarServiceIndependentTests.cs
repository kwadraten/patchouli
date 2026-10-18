using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Pagination;
using Patchouli.Infrastructure.Documents;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

public sealed class ChapterSidebarServiceIndependentTests
{
    [Fact]
    public async Task Moving_to_missing_parent_never_deletes_source_subtree()
    {
        await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync();
        DocumentPageMapService maps = document.CreateMapService();
        ChapterEditingSnapshot snapshot = (await maps.GetChapterEditingSnapshotAsync(document.DocumentId)).Value;
        ChapterNode parent = new("parent", "Parent", new NavigationPosition(1),
            [ChapterNode.Leaf("child", "Child", new NavigationPosition(2))]);
        (await maps.SaveChaptersAsync(document.DocumentId, new ChapterSaveRequest(snapshot.VersionToken, [parent])))
            .IsSuccess.Should().BeTrue();
        using ChapterSidebarViewModel sidebar = Sidebar(document);
        await sidebar.RefreshAsync();

        await sidebar.MoveToAsync("parent", "deleted-destination", null);

        ChapterEditingSnapshot after = (await maps.GetChapterEditingSnapshotAsync(document.DocumentId)).Value;
        after.Definition.Chapters.Should().ContainSingle().Which.ChapterId.Should().Be("parent");
        after.Definition.Chapters[0].Children.Should().ContainSingle().Which.ChapterId.Should().Be("child");
    }

    [Fact]
    public async Task Editing_title_during_target_loading_cannot_silently_drop_logical_target()
    {
        await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync();
        DocumentPageMapService maps = document.CreateMapService();
        ChapterEditingSnapshot initial = (await maps.GetChapterEditingSnapshotAsync(document.DocumentId)).Value;
        (await maps.SaveChaptersAsync(document.DocumentId, new ChapterSaveRequest(initial.VersionToken,
                [ChapterNode.Leaf("logical", "Original", new NavigationPosition(1, document.BoxA))])))
            .IsSuccess.Should().BeTrue();
        ChapterSidebarServices services = Services(document);
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;

        async Task<ChapterSidebarServices> Provider()
        {
            if (++calls == 2)
            {
                entered.TrySetResult();
                await gate.Task;
            }

            return services;
        }

        DocumentInstanceId documentId = document.DocumentId;
        using ChapterSidebarViewModel sidebar = new(Provider, () => documentId,
            () => 1, _ => Task.CompletedTask, () => Task.CompletedTask);
        Task editing = sidebar.BeginEditAsync("logical");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        sidebar.Title = "Renamed";
        await sidebar.SaveCommand.ExecuteAsync();
        gate.TrySetResult();
        await editing;
        if (sidebar.IsFormOpen)
        {
            await sidebar.SaveCommand.ExecuteAsync();
        }

        ChapterEditingSnapshot after = (await maps.GetChapterEditingSnapshotAsync(document.DocumentId)).Value;
        after.Definition.Chapters.Should().ContainSingle().Which.Title.Should().Be("Renamed");
        after.Definition.Chapters[0].Target.Should().Be(new NavigationPosition(1, document.BoxA));
    }

    [Fact]
    public async Task Moving_preview_parent_down_preserves_its_children_and_moves_across_sibling_subtree()
    {
        await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync();
        using ChapterSidebarViewModel sidebar = Sidebar(document);

        ChapterScanRowViewModel Row(string id, int depth)
        {
            return new ChapterScanRowViewModel(sidebar,
                ChapterNode.Leaf(id, id, new NavigationPosition(1)), depth, depth + 1, true, string.Empty);
        }

        ChapterScanRowViewModel parent = Row("parent", 0);
        sidebar.PreviewRows.Add(parent);
        sidebar.PreviewRows.Add(Row("child", 1));
        sidebar.PreviewRows.Add(Row("next", 0));

        sidebar.DropPreview(parent, sidebar.PreviewRows[2], true, false);

        IReadOnlyList<ChapterNode> result = ChapterSidebarViewModel.BuildSelectedPreview(sidebar.PreviewRows);
        result.Select(node => node.ChapterId).Should().Equal("next", "parent");
        result[1].Children.Should().ContainSingle().Which.ChapterId.Should().Be("child");
    }

    [Fact]
    public async Task Changing_reading_page_does_not_change_selected_edit_object_or_target()
    {
        await using PageMapTestDocument document = await PageMapTestDocument.CreateAsync();
        DocumentPageMapService maps = document.CreateMapService();
        ChapterEditingSnapshot initial = (await maps.GetChapterEditingSnapshotAsync(document.DocumentId)).Value;
        (await maps.SaveChaptersAsync(document.DocumentId, new ChapterSaveRequest(initial.VersionToken,
            [
                ChapterNode.Leaf("one", "One", new NavigationPosition(1)),
                ChapterNode.Leaf("two", "Two", new NavigationPosition(1))
            ])))
            .IsSuccess.Should().BeTrue();
        System.Runtime.CompilerServices.StrongBox<int> readingPage = new(1);
        ChapterSidebarServices services = Services(document);
        DocumentInstanceId documentId = document.DocumentId;
        using ChapterSidebarViewModel sidebar = new(() => Task.FromResult(services),
            () => documentId,
            () => readingPage.Value, _ => Task.CompletedTask, () => Task.CompletedTask);
        await sidebar.BeginEditAsync("two");
        readingPage.Value = 3;
        sidebar.Title = "Edited second chapter";
        await sidebar.SaveCommand.ExecuteAsync();

        ChapterEditingSnapshot after = (await maps.GetChapterEditingSnapshotAsync(document.DocumentId)).Value;
        after.Definition.Chapters[0].Title.Should().Be("One");
        after.Definition.Chapters[1].Title.Should().Be("Edited second chapter");
        after.Definition.Chapters[1].Target.PdfPage.Should().Be(1);
        sidebar.SelectedChapterId.Should().Be("two");
    }

    private static ChapterSidebarViewModel Sidebar(PageMapTestDocument document)
    {
        return new ChapterSidebarViewModel(
            () => Task.FromResult(Services(document)), () => document.DocumentId, () => 1,
            _ => Task.CompletedTask, () => Task.CompletedTask);
    }

    private static ChapterSidebarServices Services(PageMapTestDocument document)
    {
        MarkdigMarkdownEngine markdown = new();
        return new ChapterSidebarServices(document.CreateMapService(),
            new ChapterScanService(document.Pages, document.Trees,
                new DocumentMarkdownCompiler(document.Trees, markdown), markdown),
            document.Pages, document.Trees);
    }
}
