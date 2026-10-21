using FluentAssertions;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Hashing;
using System.Xml.Linq;

namespace Patchouli.Tests;

public sealed class TranslationRealignerTests
{
    [Fact]
    public async Task Legacy_gfm_translation_survives_equivalent_html_revision_and_expires_after_text_change()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId tableId = DocumentBoxId.New();
        const string legacyGfm = "| Metric | Value |\n|---|---|\n| Revenue | 10 |";
        const string translatedGfm = "| Kennzahl | Wert |\n|---|---|\n| Umsatz | 10 |";
        const string equivalentHtml = "<table><thead><tr><th>Metric</th><th>Value</th></tr></thead>" +
                                      "<tbody><tr><td>Revenue</td><td>10</td></tr></tbody></table>";

        await context.CommitAsync(TableBox(tableId, legacyGfm));
        await PutAsync(context, translatedGfm);
        await SeedLegacyTableHashAsync(context, tableId, legacyGfm);
        TranslationTestContext.TranslationBoxRowSnapshot oldRow =
            (await context.ListTranslationRowsAsync()).Should().ContainSingle().Which;
        await context.CommitAsync(TableBox(tableId, equivalentHtml, true));

        TranslatedPageMarkdown? projected = await context.Translations.GetPageTranslationAsync(
            context.DocumentId, context.PageId);
        projected.Should().NotBeNull();
        projected!.Status.TranslatedBoxCount.Should().Be(1);
        projected.Status.StaleBoxIds.Should().BeEmpty();
        projected.Markdown.Should().Contain("Umsatz").And.NotContain("Revenue");

        Result<PageXmlTranslation> pageXml = await context.Translations.GetPageXmlAsync(
            context.DocumentId, context.PageId, 1);
        pageXml.IsSuccess.Should().BeTrue(pageXml.ErrorMessage);
        XElement xmlBox = XDocument.Parse(pageXml.Value.Xml).Descendants("box").Single();
        xmlBox.Attribute("translated")!.Value.Should().Be("true");
        XElement translatedPayload = xmlBox.Element("payload")!;
        translatedPayload.Attribute("format")!.Value.Should().Be("html-table-v1");
        translatedPayload.Value.Should().Contain("Umsatz").And.NotContain("Revenue");

        TranslationTestContext.TranslationBoxRowSnapshot migratedRow =
            (await context.ListTranslationRowsAsync()).Should().ContainSingle().Which;
        migratedRow.TranslatedMd.Should().Be(translatedGfm);
        migratedRow.SourceHash.Should().NotBe(oldRow.SourceHash);

        const string changedHtml = "<table><thead><tr><th>Metric</th><th>Value</th></tr></thead>" +
                                   "<tbody><tr><td>Sales</td><td>10</td></tr></tbody></table>";
        await context.CommitAsync(TableBox(tableId, changedHtml, true));

        TranslatedPageMarkdown? stale = await context.Translations.GetPageTranslationAsync(
            context.DocumentId, context.PageId);
        stale.Should().NotBeNull();
        stale!.Status.TranslatedBoxCount.Should().Be(0);
        stale.Status.StaleBoxIds.Should().Equal(tableId);
        stale.Markdown.Should().Contain("Sales").And.NotContain("Umsatz");
        (await context.ListTranslationRowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Legacy_gfm_translation_expires_when_authoritative_html_changes_cell_spans()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId tableId = DocumentBoxId.New();
        const string legacyGfm = "| Metric | Value |\n|---|---|\n| Revenue | 10 |";
        const string translatedGfm = "| Kennzahl | Wert |\n|---|---|\n| Umsatz | 10 |";
        const string equivalentHtml = "<table><thead><tr><th>Metric</th><th>Value</th></tr></thead>" +
                                      "<tbody><tr><td>Revenue</td><td>10</td></tr></tbody></table>";
        const string changedSpanHtml = "<table><thead><tr><th colspan=\"2\">Metric Value</th></tr></thead>" +
                                       "<tbody><tr><td colspan=\"2\">Revenue 10</td></tr></tbody></table>";

        await context.CommitAsync(TableBox(tableId, legacyGfm));
        await PutAsync(context, translatedGfm);
        await context.CommitAsync(TableBox(tableId, equivalentHtml, true));
        (await context.Translations.GetPageTranslationAsync(context.DocumentId, context.PageId))
            .Should().NotBeNull();
        await context.CommitAsync(TableBox(tableId, changedSpanHtml, true));

        TranslatedPageMarkdown? stale = await context.Translations.GetPageTranslationAsync(
            context.DocumentId, context.PageId);

        stale.Should().NotBeNull();
        stale!.Status.TranslatedBoxCount.Should().Be(0);
        stale.Status.StaleBoxIds.Should().Contain(tableId);
        stale.Markdown.Should().Contain("Metric").And.NotContain("Umsatz");
        (await context.ListTranslationRowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Unchanged_boxes_keep_their_translation_across_a_tree_revision()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId first = DocumentBoxId.New();
        DocumentBoxId second = DocumentBoxId.New();
        DocumentTreeRevision firstRevision = await context.CommitAsync(
            TextBox(first, 0, "Alpha"),
            TextBox(second, 1, "Beta"));
        await PutAsync(context, "翻译 Alpha\n\n翻译 Beta");

        DocumentTreeRevision secondRevision = await context.CommitAsync(
            TextBox(first, 0, "Alpha"),
            TextBox(second, 1, "Beta"));

        TranslatedPageMarkdown? translated = await context.Translations.GetPageTranslationAsync(
            context.DocumentId, context.PageId);

        translated.Should().NotBeNull();
        translated!.Markdown.Should().Contain("翻译 Alpha").And.Contain("翻译 Beta");
        translated.Status.TranslatedBoxCount.Should().Be(2);
        translated.Status.StaleBoxIds.Should().BeEmpty();
        IReadOnlyList<TranslationTestContext.TranslationBoxRowSnapshot> rows =
            await context.ListTranslationRowsAsync();
        rows.Should().HaveCount(2);
        (int Version, string SourceTreeRevisionId)? header = await context.GetHeaderAsync();
        header.Should().NotBeNull();
        header.Value.Version.Should().Be(2, "realignment bumps the translation version");
        header.Value.SourceTreeRevisionId.Should().Be(secondRevision.TreeRevisionId.ToString());
        firstRevision.TreeRevisionId.Should().NotBe(secondRevision.TreeRevisionId);
    }

    [Fact]
    public async Task Changed_payload_expires_only_that_box_translation()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId first = DocumentBoxId.New();
        DocumentBoxId second = DocumentBoxId.New();
        await context.CommitAsync(
            TextBox(first, 0, "Alpha"),
            TextBox(second, 1, "Beta"));
        await PutAsync(context, "翻译 Alpha\n\n翻译 Beta");
        await context.CommitAsync(
            TextBox(first, 0, "Alpha"),
            TextBox(second, 1, "Beta revised"));

        TranslatedPageMarkdown? translated = await context.Translations.GetPageTranslationAsync(
            context.DocumentId, context.PageId);

        translated.Should().NotBeNull();
        translated!.Markdown.Should().Contain("翻译 Alpha").And.Contain("Beta revised")
            .And.NotContain("翻译 Beta");
        translated.Status.TranslatedBoxCount.Should().Be(1);
        translated.Status.StaleBoxIds.Should().Equal(second);
        IReadOnlyList<TranslationTestContext.TranslationBoxRowSnapshot> rows =
            await context.ListTranslationRowsAsync();
        rows.Should().ContainSingle().Which.BoxId.Should().Be(first.ToString());
    }

    [Fact]
    public async Task Removed_box_translation_is_garbage_collected()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId first = DocumentBoxId.New();
        DocumentBoxId second = DocumentBoxId.New();
        DocumentBoxId removed = DocumentBoxId.New();
        await context.CommitAsync(
            TextBox(first, 0, "Alpha"),
            TextBox(second, 1, "Beta"),
            TextBox(removed, 2, "Gamma"));
        await PutAsync(context, "翻译 Alpha\n\n翻译 Beta\n\n翻译 Gamma");

        await context.CommitAsync(
            TextBox(first, 0, "Alpha"),
            TextBox(second, 1, "Beta"));
        TranslatedPageMarkdown? translated = await context.Translations.GetPageTranslationAsync(
            context.DocumentId, context.PageId);

        translated.Should().NotBeNull();
        translated!.Status.TranslatedBoxCount.Should().Be(2);
        translated.Status.StaleBoxIds.Should().BeEmpty();
        IReadOnlyList<TranslationTestContext.TranslationBoxRowSnapshot> rows =
            await context.ListTranslationRowsAsync();
        rows.Select(row => row.BoxId).Should().BeEquivalentTo([first.ToString(), second.ToString()]);
        rows.Select(row => row.BoxId).Should().NotContain(removed.ToString());
    }

    [Fact]
    public async Task Reimport_with_fresh_box_ids_expires_every_translation()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        await context.CommitAsync(
            TextBox(DocumentBoxId.New(), 0, "Alpha"),
            TextBox(DocumentBoxId.New(), 1, "Beta"));
        await PutAsync(context, "翻译 Alpha\n\n翻译 Beta");

        DocumentBoxId reimportedFirst = DocumentBoxId.New();
        DocumentBoxId reimportedSecond = DocumentBoxId.New();
        DocumentTreeRevision reimported = await context.CommitAsync(
            TextBox(reimportedFirst, 0, "Alpha"),
            TextBox(reimportedSecond, 1, "Beta"));

        TranslatedPageMarkdown? translated = await context.Translations.GetPageTranslationAsync(
            context.DocumentId, context.PageId);

        translated.Should().NotBeNull("the page still has a translation header after realignment");
        translated!.Status.TranslatedBoxCount.Should().Be(0);
        translated.Status.StaleBoxIds.Should().BeEquivalentTo([reimportedFirst, reimportedSecond]);
        translated.Markdown.Should().Be("Alpha\n\nBeta", "all boxes fall back to their source text");
        (await context.ListTranslationRowsAsync()).Should().BeEmpty();
        (int Version, string SourceTreeRevisionId)? header = await context.GetHeaderAsync();
        header.Should().NotBeNull();
        header.GetValueOrDefault().SourceTreeRevisionId.Should().Be(reimported.TreeRevisionId.ToString());
    }

    [Fact]
    public async Task Partially_translated_page_compiles_missing_boxes_from_source()
    {
        await using TranslationTestContext context = await TranslationTestContext.CreateAsync();
        DocumentBoxId first = DocumentBoxId.New();
        DocumentBoxId second = DocumentBoxId.New();
        await context.CommitAsync(
            TextBox(first, 0, "Alpha"),
            TextBox(second, 1, "Beta"));
        await PutAsync(context, "翻译 Alpha\n\n翻译 Beta");
        await context.CommitAsync(
            TextBox(first, 0, "Alpha"),
            TextBox(second, 1, "Beta rewritten"));

        TranslatedPageMarkdown? translated = await context.Translations.GetPageTranslationAsync(
            context.DocumentId, context.PageId);

        translated.Should().NotBeNull();
        translated!.Status.TranslatedBoxCount.Should().Be(1);
        translated.Status.TotalBoxCount.Should().Be(2);
        translated.Status.StaleBoxIds.Should().Equal(second);
        translated.Markdown.Should().Contain("翻译 Alpha").And.Contain("Beta rewritten");
        translated.SourceMap.Should().HaveCount(2);
        translated.SourceMap.Select(entry => entry.BoxId).Should().Equal(first, second);
    }

    private static DocumentBoxSeed TextBox(DocumentBoxId boxId, int order, string text)
    {
        return new DocumentBoxSeed(boxId, null, order, DocumentBoxType.Text, null,
            new NormalizedBBox(.1, .1 + order * .1, .8, .05), new TextBoxPayload(text));
    }

    private static DocumentBoxSeed TableBox(DocumentBoxId boxId, string content, bool isHtml = false)
    {
        DocumentBoxPayload payload = isHtml
            ? TableContentAdapter.CreateHtml(content).Value
            : new TableBoxPayload(content);
        return new DocumentBoxSeed(boxId, null, 0, DocumentBoxType.Table, null,
            new NormalizedBBox(.1, .1, .8, .2), payload);
    }

    private static async Task SeedLegacyTableHashAsync(
        TranslationTestContext context,
        DocumentBoxId boxId,
        string legacyMarkdown)
    {
        await using SqliteConnection connection = context.ConnectionFactory.CreateWriteConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "update translation_boxes set source_hash = @SourceHash where box_id = @BoxId;",
            new
            {
                BoxId = boxId.ToString(),
                SourceHash = Blake3Hash.ComputeUtf8($"table\u001f{legacyMarkdown}")
            });
    }

    private static async Task PutAsync(TranslationTestContext context, string markdown)
    {
        Result<PageTranslationStatus> put = await context.Translations.PutPageTranslationAsync(
            context.DocumentId, context.PageId, markdown);
        put.IsSuccess.Should().BeTrue(put.ErrorMessage);
    }
}
