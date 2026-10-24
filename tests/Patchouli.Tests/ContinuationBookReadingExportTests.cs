using FluentAssertions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Documents;
using Patchouli.Host.Translation;
using Patchouli.Reading;
using Patchouli.UI.Reading;
using Page = Patchouli.Core.Layout.Page;

namespace Patchouli.Tests;

[Collection("RetainedTabUI")]
public sealed class ContinuationBookReadingExportTests(AvaloniaSharedSessionFixture headless)
{
    [Fact]
    public void Document_projection_preserves_unrelated_roots_on_every_page_while_combining_chain()
    {
        DocumentInstanceId document = DocumentInstanceId.New();
        DocumentTreeRevisionId revision = DocumentTreeRevisionId.New();
        PageId[] pages = [PageId.New(), PageId.New(), PageId.New()];
        DocumentBoxId[] chain = [DocumentBoxId.New(), DocumentBoxId.New(), DocumentBoxId.New()];
        DocumentBoxId[] unrelated = [DocumentBoxId.New(), DocumentBoxId.New(), DocumentBoxId.New()];
        List<DocumentBox> boxes = [];
        for (int page = 0; page < pages.Length; page++)
        {
            boxes.Add(Box(chain[page], pages[page], document, revision, $"chain-{page}",
                unrelated[page], page == 0 ? null : chain[page - 1]));
            boxes.Add(Box(unrelated[page], pages[page], document, revision, $"other-{page}"));
        }

        CompiledMarkdown projection = new ContinuationDocumentMarkdownProjection(new MarkdigMarkdownEngine())
            .Render(boxes);

        foreach (int page in Enumerable.Range(0, 3))
        {
            Count(projection.Markdown, $"chain-{page}").Should().Be(1);
            Count(projection.Markdown, $"other-{page}").Should().Be(1);
        }
    }

    [Fact]
    public async Task Export_modes_preserve_every_page_content_once_for_cross_page_chains()
    {
        (DocumentInstanceId document, TranslationDocumentExporter exporter, string[] sources, string[] translations) =
            CreateExporter(true);

        string source = (await exporter.ExportMarkdownAsync(document, TranslationExportMode.Source)).Value;
        string translated = (await exporter.ExportMarkdownAsync(document, TranslationExportMode.Translation)).Value;
        string bilingual = (await exporter.ExportMarkdownAsync(document, TranslationExportMode.Bilingual)).Value;

        foreach (string content in sources)
        {
            Count(source, content).Should().Be(1);
            Count(bilingual, content).Should().Be(1);
        }

        foreach (string content in Enumerable.Range(0, 3).Select(index => $"independent-{index}"))
        {
            Count(source, content).Should().Be(1);
            Count(bilingual, content).Should().Be(1);
        }

        translated.Should().Contain("跨页接续");
        foreach (string content in translations)
        {
            Count(translated, content).Should().Be(1);
            Count(bilingual, content).Should().Be(1);
        }
    }

    [Fact]
    public async Task Bilingual_export_without_cross_page_chain_keeps_page_local_source_then_translation()
    {
        (DocumentInstanceId document, TranslationDocumentExporter exporter, string[] sources, string[] translations) =
            CreateExporter(false);

        string output = (await exporter.ExportMarkdownAsync(document,
            TranslationExportMode.Bilingual)).Value;

        for (int index = 0; index < sources.Length; index++)
        {
            string pageHeading = $"## 第 {index + 1} 页";
            int heading = output.IndexOf(pageHeading, StringComparison.Ordinal);
            int source = output.IndexOf(sources[index], heading, StringComparison.Ordinal);
            int translation = output.IndexOf(translations[index], source, StringComparison.Ordinal);
            heading.Should().BeGreaterThanOrEqualTo(0);
            source.Should().BeGreaterThan(heading);
            translation.Should().BeGreaterThan(source);
            Count(output, pageHeading).Should().Be(1);
            if (index + 1 < sources.Length)
            {
                translation.Should().BeLessThan(output.IndexOf($"## 第 {index + 2} 页", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public async Task Export_propagates_cancellation_before_loading_page_revisions()
    {
        (DocumentInstanceId document, TranslationDocumentExporter exporter, _, _) = CreateExporter(false);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        CancellationToken token = cancellation.Token;

        Func<Task> export = () => exporter.ExportMarkdownAsync(document,
            TranslationExportMode.Source, token);

        await export.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    [Trait("Category", "ui")]
    public async Task Reading_measurement_places_each_continuation_page_at_its_text_line()
    {
        await headless.Session.Dispatch<bool>(() =>
        {
            const string first = "First fragment has enough words to wrap onto more than one line.";
            const string second = "Second fragment begins well below the first fragment.";
            string combined = first + "\n" + second;
            DocumentBoxId firstBox = DocumentBoxId.New();
            DocumentBoxId secondBox = DocumentBoxId.New();
            ReadingView view = new()
            {
                SourceScene = new ReadingScene([
                    new ReadingBlock(firstBox, "paragraph", 0, combined,
                        PageIndex: 0)
                    {
                        Segments =
                        [
                            new ReadingBlockSegment(0, first.Length, 0, firstBox),
                            new ReadingBlockSegment(first.Length + 1, second.Length, 1, secondBox)
                        ]
                    }
                ])
            };
            ReadingEditSelectionRequest? editRequest = null;
            view.EditSelectionRequested += (_, request) => editRequest = request;
            Window window = new() { Content = view, Width = 180, Height = 500 };
            window.Show();
            try
            {
                view.Measure(new Size(180, 500));
                view.Arrange(new Rect(0, 0, 180, 500));
                IReadOnlyList<ReadingMeasuredBlock> measured = view.GetMeasuredBlocks();
                ReadingMeasuredBlock firstPage = measured.Single(item => item.PageIndex == 0);
                ReadingMeasuredBlock secondPage = measured.Single(item => item.PageIndex == 1);
                secondPage.Bounds.Y.Should().BeGreaterThan(firstPage.Bounds.Y);
                view.BeginSelection(ReadingColumnRole.Source, 0, 0);
                view.ExtendSelection(ReadingColumnRole.Source, 0, combined.Length);
                view.GetSelectedText().Should().Be(combined);
                view.GetSelectedBoxIds().Should().Equal(firstBox, secondBox);

                view.BeginSelection(ReadingColumnRole.Source, 0, first.Length + 1);
                view.ExtendSelection(ReadingColumnRole.Source, 0, combined.Length);
                view.GetSelectedText().Should().Be(second);
                view.GetSelectedBoxIds().Should().Equal(secondBox);
                view.RaiseEditSelection();
                ReadingEditSelectionRequest request = editRequest ??
                                                      throw new InvalidOperationException(
                                                          "The source edit request was not raised.");
                request.PageIndex.Should().Be(1);
                request.BoxIds.Should().Equal(secondBox);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public void Loaded_explicit_nonadjacent_chain_becomes_one_block_with_physical_segments()
    {
        DocumentBoxId a = DocumentBoxId.New();
        DocumentBoxId b = DocumentBoxId.New();
        DocumentBoxId c = DocumentBoxId.New();
        SortedDictionary<int, ReadingScene> pages = new()
        {
            [0] = new ReadingScene([
                new ReadingBlock(a, "paragraph", 0, "A",
                    [new MarkdownInlineModel("text", "A")])
            ]),
            [1] = new ReadingScene([
                new ReadingBlock(DocumentBoxId.New(), "paragraph", 0, "unrelated"),
                new ReadingBlock(b, "paragraph", 0, "B",
                        [new MarkdownInlineModel("strong", string.Empty, [new MarkdownInlineModel("text", "B")])])
                    { ContinuesFromBoxId = a }
            ]),
            [2] = new ReadingScene([
                new ReadingBlock(c, "paragraph", 0, "C",
                    [new MarkdownInlineModel("text", "C")]) { ContinuesFromBoxId = b }
            ])
        };

        (ReadingScene source, _) = BookReadingDocument.Assemble(pages,
            new Dictionary<int, ReadingScene?>(), false);

        source.Blocks.Should().ContainSingle(block => block.BoxId == a);
        ReadingBlock logical = source.Blocks.Single(block => block.BoxId == a);
        logical.Text.Should().Be("A\nB\nC");
        IReadOnlyList<MarkdownInlineModel> inlines = logical.Inlines ??
                                                     throw new InvalidOperationException(
                                                         "Combined rich text must retain its inline runs.");
        inlines.Select(inline => inline.Kind).Should().Equal("text", "text", "strong", "text", "text");
        inlines[1].Text.Should().Be("\n");
        logical.SourceBoxIds.Should().Equal(a, b, c);
        logical.Segments!.Select(segment => segment.PageIndex).Should().Equal(0, 1, 2);
        source.Blocks.Should().ContainSingle(block => block.Text == "unrelated");
    }

    [Fact]
    public void Missing_source_does_not_invent_text_and_compare_keeps_physical_rows_aligned()
    {
        DocumentBoxId a = DocumentBoxId.New();
        DocumentBoxId b = DocumentBoxId.New();
        SortedDictionary<int, ReadingScene> pages = new()
        {
            [1] = new ReadingScene([new ReadingBlock(b, "paragraph", 0, "B") { ContinuesFromBoxId = a }]),
            [2] = new ReadingScene([new ReadingBlock(DocumentBoxId.New(), "paragraph", 0, "later")])
        };
        Dictionary<int, ReadingScene?> translations = new()
        {
            [1] = new ReadingScene([new ReadingBlock(b, "paragraph", 0, "译B")]),
            [2] = new ReadingScene([new ReadingBlock(DocumentBoxId.New(), "paragraph", 0, "译later")])
        };

        (ReadingScene source, ReadingScene? translated) = BookReadingDocument.Assemble(pages, translations, true);

        source.Blocks.Select(block => block.Text).Should().Equal("B", "later");
        translated!.Blocks.Select(block => block.Text).Should().Equal("译B", "译later");
    }

    [Fact]
    public void Empty_legacy_follower_does_not_repeat_the_head_text()
    {
        DocumentBoxId head = DocumentBoxId.New();
        DocumentBoxId follower = DocumentBoxId.New();
        SortedDictionary<int, ReadingScene> pages = new()
        {
            [0] = new ReadingScene([new ReadingBlock(head, "paragraph", 0, "canonical text")]),
            [1] = new ReadingScene([
                new ReadingBlock(follower, "paragraph", 0, string.Empty)
                    { ContinuesFromBoxId = head }
            ])
        };

        (ReadingScene assembled, _) = BookReadingDocument.Assemble(pages,
            new Dictionary<int, ReadingScene?>(), false);

        assembled.Blocks.Should().ContainSingle();
        Count(assembled.Blocks[0].Text, "canonical text").Should().Be(1);
    }

    [Fact]
    public async Task Compiled_page_scenes_keep_all_same_node_box_ids_for_a_later_page_link()
    {
        DocumentInstanceId document = DocumentInstanceId.New();
        PageId[] pageIds = [PageId.New(), PageId.New()];
        DocumentTreeRevisionId[] revisions = [DocumentTreeRevisionId.New(), DocumentTreeRevisionId.New()];
        DocumentBoxId a = DocumentBoxId.New();
        DocumentBoxId b = DocumentBoxId.New();
        DocumentBoxId c = DocumentBoxId.New();
        Page[] pages = pageIds.Select((id, index) => new Page(id, document, index, null, null, null, 0,
            "page", null, null, "test", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)).ToArray();
        DocumentBox[] pageOneBoxes =
        [
            Box(a, pageIds[0], document, revisions[0], "A", b),
            Box(b, pageIds[0], document, revisions[0], "B", from: a)
        ];
        DocumentBox[] pageTwoBoxes = [Box(c, pageIds[1], document, revisions[1], "C", from: b)];
        MarkdownSourceMapEntry[] sameNodeMap = [new(a, 0, 1, 0, 1), new(b, 2, 1, 0, 1)];
        CompiledMarkdown[] compiled =
        [
            new("A B", sameNodeMap, [], new MarkdownDocumentModel([new MarkdownBlock("paragraph", "A B", 0, 3)]))
            {
                Continuations = [new MarkdownContinuation(b, a)]
            },
            new("C", [new MarkdownSourceMapEntry(c, 0, 1, 0, 1)], [],
                new MarkdownDocumentModel([new MarkdownBlock("paragraph", "C", 0, 1)]))
            {
                Continuations = [new MarkdownContinuation(c, b)]
            }
        ];
        List<DocumentTreeRevisionId> compiledRevisions = [];
        BookReadingStream stream = new(
            (_, _) => Task.FromResult(Result<IReadOnlyList<Page>>.Success(pages)),
            (_, pageId, _) => Task.FromResult(Result<DocumentTreeRevision>.Success(new DocumentTreeRevision(
                revisions[Array.IndexOf(pageIds, pageId)], document, pageId, null, "test", "committed", true,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))),
            (revisionId, _, _, _) =>
            {
                compiledRevisions.Add(revisionId);
                return Task.FromResult(Result<CompiledMarkdown>.Success(
                    compiled[Array.IndexOf(revisions, revisionId)]));
            },
            (revisionId, _) => Task.FromResult(Result<IReadOnlyList<DocumentBox>>.Success(
                revisionId == revisions[0] ? pageOneBoxes : pageTwoBoxes))
        );

        BookReadingPage pageOne = await stream.LoadPageAsync(document, 0, 2);
        compiledRevisions.Should().ContainSingle().Which.Should().Be(revisions[0]);
        pageOne.Source.Blocks.Single().Segments!.Select(segment => (segment.Start, segment.Length))
            .Should().Equal((0, 1), (2, 1));
        BookReadingPage pageTwo = await stream.LoadPageAsync(document, 1, 2);
        SortedDictionary<int, ReadingScene> loaded = new() { [0] = pageOne.Source, [1] = pageTwo.Source };
        (ReadingScene assembled, _) = BookReadingDocument.Assemble(loaded,
            new Dictionary<int, ReadingScene?>(), false);

        assembled.Blocks.Should().ContainSingle();
        assembled.Blocks[0].Text.Should().Be("A B\nC");
        assembled.Blocks[0].SourceBoxIds.Should().Equal(a, b, c);
        assembled.Blocks[0].Segments!.Select(segment => segment.PageIndex).Should().Contain(0).And.Contain(1);
    }

    private static int Count(string value, string fragment)
    {
        return (value.Length - value.Replace(fragment, string.Empty, StringComparison.Ordinal).Length) /
               fragment.Length;
    }

    private static DocumentBox Box(DocumentBoxId id, PageId page, DocumentInstanceId document,
        DocumentTreeRevisionId revision, string text, DocumentBoxId? next = null, DocumentBoxId? from = null)
    {
        return new DocumentBox(revision, id, document, page, null, next, "text", null, default,
            new TextBoxPayload(text), null, null, null, false, from);
    }

    private static (DocumentInstanceId Document, TranslationDocumentExporter Exporter,
        string[] Sources, string[] Translations)
        CreateExporter(bool crossPage)
    {
        DocumentInstanceId document = DocumentInstanceId.New();
        PageId[] pageIds = [PageId.New(), PageId.New(), PageId.New()];
        DocumentTreeRevisionId[] revisions =
            [DocumentTreeRevisionId.New(), DocumentTreeRevisionId.New(), DocumentTreeRevisionId.New()];
        string[] sources = ["source-alpha", "source-beta", "source-gamma"];
        string[] translations = ["translated-alpha", "translated-beta", "translated-gamma"];
        DocumentBoxId[] chain = [DocumentBoxId.New(), DocumentBoxId.New(), DocumentBoxId.New()];
        DocumentBoxId[] independent = [DocumentBoxId.New(), DocumentBoxId.New(), DocumentBoxId.New()];
        Page[] pages = pageIds.Select((pageId, index) => new Page(pageId, document, index, null, null, null, 0,
            "page", null, null, "test", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)).ToArray();
        List<DocumentBox[]> boxesPerPage = [];
        CompiledMarkdown[] compiledByPage = new CompiledMarkdown[pages.Length];
        for (int index = 0; index < pages.Length; index++)
        {
            DocumentBoxId rootId = crossPage ? chain[index] : independent[index];
            DocumentBoxId? predecessor = crossPage && index > 0 ? chain[index - 1] : null;
            DocumentBox[] pageBoxes =
            [
                Box(rootId, pageIds[index], document, revisions[index], sources[index],
                    from: predecessor),
                Box(DocumentBoxId.New(), pageIds[index], document, revisions[index], $"independent-{index}")
            ];
            // Ensure two ordinary roots remain in the per-page tree.
            pageBoxes[0] = pageBoxes[0] with { NextSiblingBoxId = pageBoxes[1].BoxId };
            boxesPerPage.Add(pageBoxes);
            compiledByPage[index] = new ContinuationDocumentMarkdownProjection(new MarkdigMarkdownEngine())
                .Render(pageBoxes);
        }

        IPageService pageService = Stub<IPageService>((method, args) =>
            method.Name == nameof(IPageService.ListPagesAsync)
                ? Task.FromResult(Result<IReadOnlyList<Page>>.Success(pages))
                : throw new NotSupportedException(method.Name));
        IDocumentTreeService treeService = Stub<IDocumentTreeService>((method, args) => method.Name switch
        {
            nameof(IDocumentTreeService.GetCurrentRevisionAsync) => Task.FromResult(
                Result<DocumentTreeRevision>.Success(
                    new DocumentTreeRevision(revisions[Array.IndexOf(pageIds, (PageId)args[1]!)], document,
                        (PageId)args[1]!, null, "test", "committed", true, DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow))),
            nameof(IDocumentTreeService.ListBoxesAsync) => Task.FromResult(Result<IReadOnlyList<DocumentBox>>.Success(
                boxesPerPage[Array.IndexOf(revisions, (DocumentTreeRevisionId)args[0]!)])),
            _ => throw new NotSupportedException(method.Name)
        });
        IDocumentMarkdownCompiler compiler = Stub<IDocumentMarkdownCompiler>((method, args) =>
            method.Name == nameof(IDocumentMarkdownCompiler.CompilePageMarkdownAsync)
                ? Task.FromResult(Result<CompiledMarkdown>.Success(
                    compiledByPage[Array.IndexOf(revisions, (DocumentTreeRevisionId)args[0]!)]))
                : throw new NotSupportedException(method.Name));
        IPageTranslationService translationService = Stub<IPageTranslationService>((method, args) =>
            method.Name == nameof(IPageTranslationService.GetPageTranslationAsync)
                ? Task.FromResult<TranslatedPageMarkdown?>(new TranslatedPageMarkdown(
                    translations[Array.IndexOf(pageIds, (PageId)args[1]!)], [],
                    new PageTranslationStatus(1, 1, [], revisions[Array.IndexOf(pageIds, (PageId)args[1]!)], true)))
                : throw new NotSupportedException(method.Name));

        return (document, new TranslationDocumentExporter(pageService, treeService, compiler, translationService,
            new MarkdigMarkdownEngine()), sources, translations);
    }

    private static T Stub<T>(Func<System.Reflection.MethodInfo, object?[], object?> handler) where T : class
    {
        T proxy = System.Reflection.DispatchProxy.Create<T, RecordingProxy<T>>();
        ((RecordingProxy<T>)(object)proxy).Handler = handler;
        return proxy;
    }

    public class RecordingProxy<T> : System.Reflection.DispatchProxy where T : class
    {
        public Func<System.Reflection.MethodInfo, object?[], object?> Handler { get; set; } = null!;

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            return Handler(targetMethod ?? throw new InvalidOperationException("Missing service method."), args ?? []);
        }
    }
}
