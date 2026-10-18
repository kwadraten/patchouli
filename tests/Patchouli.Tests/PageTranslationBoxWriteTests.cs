using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Documents.Translations;
using Patchouli.Infrastructure.LibraryIdentity;
using System.Xml.Linq;

namespace Patchouli.Tests;

public sealed class PageTranslationBoxWriteTests
{
    [Fact]
    public async Task Box_write_upserts_one_leaf_preserves_valid_siblings_and_returns_committed_version()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId firstId = DocumentBoxId.New();
        DocumentBoxId secondId = DocumentBoxId.New();
        DocumentTreeRevision revision = await context.CommitAsync(
            Leaf(firstId, 0, "first source"),
            Leaf(secondId, 1, "second source"));
        LibraryRevisionService revisions = new(context.ConnectionFactory);
        long libraryRevisionBefore = (await revisions.GetCurrentRevisionAsync()).Value;
        PageTranslationService translations = new(
            context.ConnectionFactory,
            context.Trees,
            context.MarkdownCompiler,
            context.Markdown,
            context.TranslationCompiler,
            context.Cache,
            context.Clock,
            revisions);
        int publishedChanges = 0;
        revisions.ChangeCommitted += (_, _) => publishedChanges++;

        Result<PageTranslationStatus> firstWrite = await translations.PutBoxPayloadsAsync(
            context.DocumentId, context.PageId, revision.TreeRevisionId, 0,
            new Dictionary<DocumentBoxId, DocumentBoxPayload>
            {
                [firstId] = new TextBoxPayload("primera traducción")
            });

        Assert.True(firstWrite.IsSuccess, firstWrite.ErrorMessage);
        Assert.Equal(1, firstWrite.Value.Version);
        Assert.Equal(1, firstWrite.Value.TranslatedBoxCount);
        Assert.Equal(1, firstWrite.Value.StaleBoxIds.Count);
        Assert.Equal(1, await context.GetVersionAsync());

        Result<PageTranslationStatus> secondWrite = await translations.PutBoxPayloadsAsync(
            context.DocumentId, context.PageId, revision.TreeRevisionId, firstWrite.Value.Version,
            new Dictionary<DocumentBoxId, DocumentBoxPayload>
            {
                [secondId] = new TextBoxPayload("segunda traducción")
            });

        Assert.True(secondWrite.IsSuccess, secondWrite.ErrorMessage);
        Assert.Equal(2, secondWrite.Value.Version);
        Assert.Equal(2, secondWrite.Value.TranslatedBoxCount);
        Assert.Empty(secondWrite.Value.StaleBoxIds);
        Assert.Equal(2, await context.GetVersionAsync());
        Assert.Equal(libraryRevisionBefore + 2, (await revisions.GetCurrentRevisionAsync()).Value);
        Assert.Equal(2, publishedChanges);

        IReadOnlyList<TranslationTestContext.TranslationBoxRowSnapshot> rows = await context.ListTranslationRowsAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.BoxId == firstId.ToString() && row.TranslatedMd == "primera traducción");
        Assert.Contains(rows, row => row.BoxId == secondId.ToString() && row.TranslatedMd == "segunda traducción");
    }

    [Fact]
    public async Task Stale_source_revision_and_translation_version_are_conflicts_without_writes()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId boxId = DocumentBoxId.New();
        DocumentTreeRevision firstRevision = await context.CommitAsync(Leaf(boxId, 0, "source one"));
        DocumentTreeRevision secondRevision = await context.CommitAsync(Leaf(boxId, 0, "source two"));

        Result<PageTranslationStatus> staleSource = await context.Translations.PutBoxPayloadsAsync(
            context.DocumentId, context.PageId, firstRevision.TreeRevisionId, 0,
            new Dictionary<DocumentBoxId, DocumentBoxPayload> { [boxId] = new TextBoxPayload("stale") });

        Assert.True(staleSource.IsFailure, staleSource.ErrorMessage);
        Assert.Equal("conflict", staleSource.ErrorCode);
        Assert.Empty(await context.ListTranslationRowsAsync());
        Assert.Equal(0, await context.GetVersionAsync());

        Result<PageTranslationStatus> firstWrite = await context.Translations.PutBoxPayloadsAsync(
            context.DocumentId, context.PageId, secondRevision.TreeRevisionId, 0,
            new Dictionary<DocumentBoxId, DocumentBoxPayload> { [boxId] = new TextBoxPayload("current") });
        Assert.True(firstWrite.IsSuccess, firstWrite.ErrorMessage);

        Result<PageTranslationStatus> staleVersion = await context.Translations.PutBoxPayloadsAsync(
            context.DocumentId, context.PageId, secondRevision.TreeRevisionId, 0,
            new Dictionary<DocumentBoxId, DocumentBoxPayload> { [boxId] = new TextBoxPayload("overwritten") });

        Assert.True(staleVersion.IsFailure, staleVersion.ErrorMessage);
        Assert.Equal("conflict", staleVersion.ErrorCode);
        Assert.Equal(1, await context.GetVersionAsync());
        Assert.Equal("current", Assert.Single(await context.ListTranslationRowsAsync()).TranslatedMd);
    }

    [Fact]
    public async Task Legacy_markdown_write_conflicts_if_source_changes_during_validation()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId boxId = DocumentBoxId.New();
        DocumentTreeRevision firstRevision = await context.CommitAsync(Leaf(boxId, 0, "source one"));
        CallbackMarkdownCompiler compiler = new(context.MarkdownCompiler,
            // CompilePageMarkdownAsync awaits this callback before the write returns and context is disposed.
            // ReSharper disable once AccessToDisposedClosure
            async () => { await context.CommitAsync(Leaf(boxId, 0, "source two")); });
        PageTranslationService translations = new(
            context.ConnectionFactory,
            context.Trees,
            compiler,
            context.Markdown,
            context.TranslationCompiler,
            context.Cache,
            context.Clock);

        Result<PageTranslationStatus> result = await translations.PutPageTranslationAsync(
            context.DocumentId, context.PageId, "validated against source one");

        Assert.True(result.IsFailure, result.ErrorMessage);
        Assert.Equal("conflict", result.ErrorCode);
        Assert.Empty(await context.ListTranslationRowsAsync());
        Assert.Null(await context.GetHeaderAsync());
    }

    [Fact]
    public async Task Invalid_table_structure_rolls_back_other_replacements_in_the_same_batch()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId textId = DocumentBoxId.New();
        DocumentBoxId tableId = DocumentBoxId.New();
        DocumentTreeRevision revision = await context.CommitAsync(
            Leaf(textId, 0, "valid source"),
            Leaf(tableId, 1, "| A | B |\n|---|---|\n| x | y |", DocumentBoxType.Table,
                new TableBoxPayload("| A | B |\n|---|---|\n| x | y |")));

        Result<PageTranslationStatus> result = await context.Translations.PutBoxPayloadsAsync(
            context.DocumentId, context.PageId, revision.TreeRevisionId, 0,
            new Dictionary<DocumentBoxId, DocumentBoxPayload>
            {
                [textId] = new TextBoxPayload("valid translation"),
                [tableId] = new TableBoxPayload("| A |\n|---|\n| x |")
            });

        Assert.True(result.IsFailure, result.ErrorMessage);
        Assert.True(result.Details is BoxContentFailureDetails, result.ErrorMessage);
        Assert.Empty(await context.ListTranslationRowsAsync());
        Assert.Equal(0, await context.GetVersionAsync());
    }

    [Fact]
    public async Task Full_xml_write_rejects_a_markdown_structure_change_without_writes()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId boxId = DocumentBoxId.New();
        await context.CommitAsync(Leaf(boxId, 0, "source paragraph"));
        PageTranslationService translations = new(
            context.ConnectionFactory,
            context.Trees,
            context.MarkdownCompiler,
            context.Markdown,
            context.TranslationCompiler,
            context.Cache,
            context.Clock);
        Result<PageXmlTranslation> pageXml = await translations.GetPageXmlAsync(context.DocumentId, context.PageId, 1);
        Assert.True(pageXml.IsSuccess, pageXml.ErrorMessage);

        XDocument edited = XDocument.Parse(pageXml.Value.Xml);
        XElement box = Assert.Single(edited.Descendants("box"));
        box.SetAttributeValue("translated", "true");
        box.Element("payload")!.Value = "- translated list item";

        Result<PageTranslationStatus> result = await translations.PutPageXmlAsync(
            context.DocumentId, context.PageId, 1, edited.ToString(SaveOptions.DisableFormatting));

        Assert.True(result.IsFailure, result.ErrorMessage);
        Assert.Equal("validation_failed", result.ErrorCode);
        Assert.IsType<BoxContentFailureDetails>(result.Details);
        Assert.Empty(await context.ListTranslationRowsAsync());
        Assert.Null(await context.GetHeaderAsync());
    }

    [Fact]
    public async Task Typed_translation_payloads_round_trip_through_full_page_xml()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId equationId = DocumentBoxId.New();
        DocumentBoxId codeId = DocumentBoxId.New();
        DocumentBoxId tableId = DocumentBoxId.New();
        DocumentBoxId imageId = DocumentBoxId.New();
        DocumentBoxId footnoteId = DocumentBoxId.New();
        DocumentBoxId siblingImageId = DocumentBoxId.New();
        DocumentTreeRevision revision = await context.CommitAsync(
            Leaf(equationId, 0, "x^2", DocumentBoxType.Equation, new EquationBoxPayload("x^2")),
            Leaf(codeId, 1, "Console.WriteLine(1);", DocumentBoxType.Code,
                    new CodeBoxPayload("Console.WriteLine(1);")) with
                {
                    CodeLanguage = "csharp"
                },
            Leaf(tableId, 2, "| A |\n|---|\n| x |", DocumentBoxType.Table,
                new TableBoxPayload("| A |\n|---|\n| x |")),
            Leaf(imageId, 3, "source image", DocumentBoxType.Image,
                new MediaBoxPayload("asset-1", "source image")),
            new DocumentBoxSeed(
                footnoteId, imageId, 4, DocumentBoxType.ImageFootnote, null, new NormalizedBBox(0.1, 0.1, 0.2, 0.1),
                new TextBoxPayload("source footnote"),
                Suppressed: true),
            Leaf(siblingImageId, 5, "source chart", DocumentBoxType.Chart,
                new MediaBoxPayload("asset-2", "source chart")));

        Result<PageTranslationStatus> written = await context.Translations.PutBoxPayloadsAsync(
            context.DocumentId, context.PageId, revision.TreeRevisionId, 0,
            new Dictionary<DocumentBoxId, DocumentBoxPayload>
            {
                [equationId] = new EquationBoxPayload("y^2"),
                [codeId] = new CodeBoxPayload("Console.WriteLine(2);"),
                [tableId] = new TableBoxPayload("| A |\n|---|\n| y |"),
                [siblingImageId] = new MediaBoxPayload("asset-2", "translated chart"),
                [footnoteId] = new TextBoxPayload("translated footnote")
            });
        Assert.True(written.IsSuccess, written.ErrorMessage);

        Result<PageXmlTranslation> pageXml =
            await context.Translations.GetPageXmlAsync(context.DocumentId, context.PageId, 1);
        Assert.True(pageXml.IsSuccess, pageXml.ErrorMessage);
        XDocument decoded = XDocument.Parse(pageXml.Value.Xml);
        Dictionary<string, (string Format, string Text)> translatedPayloads = decoded
            .Descendants("box")
            .Where(box => (string?)box.Attribute("translated") == "true")
            .ToDictionary(
                box => (string)box.Attribute("id")!,
                box => ((string)box.Element("payload")!.Attribute("format")!, box.Element("payload")!.Value));
        Assert.Equal(("latex", "y^2"), translatedPayloads[equationId.ToString()]);
        Assert.Equal(("code", "Console.WriteLine(2);"), translatedPayloads[codeId.ToString()]);
        Assert.Equal(("markdown", "| A |\n|---|\n| y |"), translatedPayloads[tableId.ToString()]);
        Assert.Equal(("description", "translated chart"), translatedPayloads[siblingImageId.ToString()]);
        Assert.Equal(("markdown", "translated footnote"), translatedPayloads[footnoteId.ToString()]);

        XDocument invalidContainer = XDocument.Parse(pageXml.Value.Xml);
        XElement imageBox = invalidContainer.Descendants("box")
            .Single(box => (string?)box.Attribute("id") == imageId.ToString());
        imageBox.SetAttributeValue("translated", "true");
        Result<PageTranslationStatus> rejectedContainer = await context.Translations.PutPageXmlAsync(
            context.DocumentId, context.PageId, 1,
            invalidContainer.ToString(SaveOptions.DisableFormatting));
        Assert.True(rejectedContainer.IsFailure, rejectedContainer.ErrorMessage);
        Assert.Equal("validation_failed", rejectedContainer.ErrorCode);
        Assert.Equal(written.Value.Version, await context.GetVersionAsync());

        Result<PageTranslationStatus> unchangedPut = await context.Translations.PutPageXmlAsync(
            context.DocumentId, context.PageId, 1, pageXml.Value.Xml);

        Assert.True(unchangedPut.IsSuccess, unchangedPut.ErrorMessage);
        Assert.Equal(written.Value.Version + 1, unchangedPut.Value.Version);
        Assert.Equal(5, unchangedPut.Value.TranslatedBoxCount);
    }

    [Fact]
    public async Task Legacy_markdown_translation_exports_raw_latex_to_xml_and_round_trips()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId equationId = DocumentBoxId.New();
        await context.CommitAsync(
            Leaf(equationId, 0, "x^2", DocumentBoxType.Equation, new EquationBoxPayload("x^2")));

        Result<PageTranslationStatus> legacyWrite = await context.Translations.PutPageTranslationAsync(
            context.DocumentId, context.PageId, "$$\ny^2\n$$");
        Assert.True(legacyWrite.IsSuccess, legacyWrite.ErrorMessage);

        Result<PageXmlTranslation> pageXml =
            await context.Translations.GetPageXmlAsync(context.DocumentId, context.PageId, 1);
        Assert.True(pageXml.IsSuccess, pageXml.ErrorMessage);
        XElement equation = Assert.Single(XDocument.Parse(pageXml.Value.Xml).Descendants("box"));
        Assert.Equal("true", (string?)equation.Attribute("translated"));
        Assert.Equal("latex", (string?)equation.Element("payload")?.Attribute("format"));
        Assert.Equal("y^2", equation.Element("payload")?.Value);

        Result<PageTranslationStatus> unchangedPut = await context.Translations.PutPageXmlAsync(
            context.DocumentId, context.PageId, 1, pageXml.Value.Xml);

        Assert.True(unchangedPut.IsSuccess, unchangedPut.ErrorMessage);
        Assert.Equal(legacyWrite.Value.Version + 1, unchangedPut.Value.Version);
    }

    [Fact]
    public async Task Suppressed_footnote_leaf_can_be_translated_and_cancellation_writes_nothing()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId imageId = DocumentBoxId.New();
        DocumentBoxId footnoteId = DocumentBoxId.New();
        DocumentTreeRevision revision = await context.CommitAsync(
            Leaf(imageId, 0, "image description", DocumentBoxType.Image,
                new MediaBoxPayload("asset-1", "image description")),
            new DocumentBoxSeed(
                footnoteId, imageId, 1, DocumentBoxType.ImageFootnote, null, new NormalizedBBox(0.1, 0.1, 0.2, 0.1),
                new TextBoxPayload("source footnote"),
                Suppressed: true));

        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Translations.PutBoxPayloadsAsync(
            context.DocumentId, context.PageId, revision.TreeRevisionId, 0,
            new Dictionary<DocumentBoxId, DocumentBoxPayload>
            {
                [footnoteId] = new TextBoxPayload("cancelled")
            }, cancelled.Token));
        Assert.Empty(await context.ListTranslationRowsAsync());

        Result<PageTranslationStatus> result = await context.Translations.PutBoxPayloadsAsync(
            context.DocumentId, context.PageId, revision.TreeRevisionId, 0,
            new Dictionary<DocumentBoxId, DocumentBoxPayload>
            {
                [footnoteId] = new TextBoxPayload("translated footnote")
            });

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(1, result.Value.Version);
        Assert.Equal(1, result.Value.TranslatedBoxCount);
        Assert.Equal("translated footnote", Assert.Single(await context.ListTranslationRowsAsync()).TranslatedMd);
    }

    private static DocumentBoxSeed Leaf(
        DocumentBoxId id,
        int order,
        string markdown,
        string type = DocumentBoxType.Text,
        DocumentBoxPayload? payload = null)
    {
        return new DocumentBoxSeed(
            id,
            null,
            order,
            type,
            null,
            new NormalizedBBox(0.02 + order * 0.13, 0.02, 0.15, 0.08),
            payload ?? new TextBoxPayload(markdown));
    }

    private sealed class CallbackMarkdownCompiler : IDocumentMarkdownCompiler
    {
        private readonly IDocumentMarkdownCompiler _inner;
        private Func<Task>? _afterCompile;

        public CallbackMarkdownCompiler(IDocumentMarkdownCompiler inner, Func<Task> afterCompile)
        {
            _inner = inner;
            _afterCompile = afterCompile;
        }

        public async Task<Result<CompiledMarkdown>> CompilePageMarkdownAsync(
            DocumentTreeRevisionId treeRevisionId,
            bool includeSuppressed = false,
            CancellationToken cancellationToken = default,
            bool includeComplexTableHtml = false)
        {
            Result<CompiledMarkdown> compiled = await _inner.CompilePageMarkdownAsync(
                treeRevisionId, includeSuppressed, cancellationToken, includeComplexTableHtml);
            Func<Task>? callback = _afterCompile;
            _afterCompile = null;
            if (compiled.IsSuccess && callback is not null)
            {
                await callback();
            }

            return compiled;
        }
    }
}
