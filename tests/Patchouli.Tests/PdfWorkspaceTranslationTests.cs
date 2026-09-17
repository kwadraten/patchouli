using Avalonia.Headless;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Ocr;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

/// <summary>
/// Exercises the view-mode sidebar translation tab through the real host services: a committed
/// page is translated, the page is loaded into the workspace with a stub preview renderer, and
/// the translation scene plus clipboard copy are asserted against the compiled translation.
/// </summary>
[Collection("Avalonia")]
public sealed class PdfWorkspaceTranslationTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    public void Dispose()
    {
        _settings.Dispose();
    }

    [Fact]
    public async Task Loading_a_page_populates_the_translation_scene_and_copy_command()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            MainWindowViewModel main = new(new FakeClipboard(), settingsPath: _settings.Path)
            {
                RuntimeDatabasePath = _settings.CreateDatabasePath("ui-translation")
            };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();

            ItemMetadata item = (await services.Items.CreateItemAsync("book", "Translation workspace")).Value;
            DocumentInstance document = (await services.Documents.AttachDocumentInstanceAsync(
                item.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
            Page page = (await services.Pages.CreatePageAsync(
                document.DocumentInstanceId, 0, "1", null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "test", null)).Value;
            DocumentTreeRevision revision = await BoxTreeTestData.CommitTextAsync(
                services.ConnectionFactory, services.Clock, document.DocumentInstanceId, page.PageId,
                "Source paragraph");
            CompiledMarkdown source = (await services.DocumentMarkdown.CompilePageMarkdownAsync(
                revision.TreeRevisionId)).Value;
            Result<PageTranslationStatus> put = await services.PageTranslations.PutPageTranslationAsync(
                document.DocumentInstanceId, page.PageId, source.Markdown.Replace("Source paragraph", "译文段落"));
            put.IsSuccess.Should().BeTrue(put.ErrorMessage);

            LibraryItemViewModel libraryItem = CreateLibraryItem(document.DocumentInstanceId);
            FakeClipboard clipboard = (FakeClipboard)main.Clipboard;
            PdfWorkspaceViewModel workspace = new(main, libraryItem);
            workspace.PageRenderPreviewHandler = (_, _) => Task.FromResult(
                Result<PdfPagePixelBufferLease>.Success(
                    new PdfPagePixelBufferLease(new byte[400], 10, 10, 40, 0, "test", 10, 10, "v1")));

            await workspace.LoadAsync();

            workspace.HasNoTranslation.Should().BeFalse();
            workspace.TranslationScene.Should().NotBeNull();
            workspace.TranslationScene!.Blocks.Should().NotBeEmpty();

            await workspace.ShowTranslationTabCommand.ExecuteAsync();
            workspace.ActiveSidebarTab.Should().Be(SidebarTab.Translation);
            workspace.SidebarTabTitle.Should().Be("翻译");

            await workspace.CopyMarkdownCommand.ExecuteAsync();
            clipboard.Text.Should().Contain("译文段落");

            await services.PageRenders.ReleaseDocumentSessionAsync(document.DocumentInstanceId);
            workspace.Dispose();
            await main.ShutdownAsync();
        }, CancellationToken.None);
    }

    private static LibraryItemViewModel CreateLibraryItem(DocumentInstanceId documentInstanceId)
    {
        return new LibraryItemViewModel(
            ItemId.New().ToString(), "Translation workspace", "book", "", "", "", null,
            documentInstanceId.ToString(),
            FileAssetId.New().ToString(),
            "source.pdf", "", 1, 0, "", _ => Task.CompletedTask, _ => Task.CompletedTask);
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public string? Text { get; private set; }

        public Task SetTextAsync(string text)
        {
            Text = text;
            return Task.CompletedTask;
        }

        public Task<string?> GetTextAsync()
        {
            return Task.FromResult(Text);
        }

        public Task ClearAsync()
        {
            Text = null;
            return Task.CompletedTask;
        }
    }
}
