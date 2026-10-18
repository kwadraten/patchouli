using System.Xml.Linq;
using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Bibliography.Biblatex;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Search;
using Patchouli.Mcp;

namespace Patchouli.Tests;

public sealed class McpNativeResourceTests
{
    [Fact]
    public async Task Xml_byte_budget_returns_whole_boxes_and_reports_an_oversized_single_box()
    {
        await using TranslationTestContext fixture = await TranslationTestContext.CreateAsync();
        await fixture.CommitAsync(Seed(0, new string('a', 5000)), Seed(1, new string('b', 5000)));
        McpCommandService commands = Commands(fixture);
        string uri = McpResourceUris.PageUri(fixture.DocumentId, 1);
        McpCommandResult<McpFetchMeta, McpFetchResult> fetched =
            await commands.FetchAsync(new McpFetchRequest([uri], null, 7000));
        fetched.IsSuccess.Should().BeTrue(fetched.Error?.Detail);
        McpFetchResult entry = fetched.Envelope!.Entries.Single();
        XElement xml = XElement.Parse(entry.Content!);
        xml.Descendants("payload").Select(value => value.Value).Should().Equal(new string('a', 5000));
        entry.Complete.Should().BeFalse();
        entry.Truncated.Should().BeFalse();
        entry.NextRange.Should().Be("boxes:2-2");
        entry.ReturnedBytes.Should().Be(System.Text.Encoding.UTF8.GetByteCount(entry.Content!));
        entry.ReturnedBytes.Should().BeLessThanOrEqualTo(7000);

        McpCommandResult<McpFetchMeta, McpFetchResult> oversized =
            await commands.FetchAsync(new McpFetchRequest([uri], "boxes:1-1", 2000));
        oversized.Error!.Code.Should().Be((int)McpErrorCode.ResponseTruncated);
        McpFetchResult refused = oversized.Envelope!.Entries.Single();
        refused.Content.Should().BeNull();
        refused.ReturnedBytes.Should().Be(0);
        refused.Error.Should().Contain("increase limit_bytes");
    }

    [Fact]
    public async Task Empty_xml_page_is_complete_and_explicit_box_ranges_are_rejected()
    {
        await using TranslationTestContext fixture = await TranslationTestContext.CreateAsync();
        await fixture.CommitAsync();
        McpCommandService commands = Commands(fixture);
        string uri = McpResourceUris.PageUri(fixture.DocumentId, 1);
        McpCommandResult<McpFetchMeta, McpFetchResult> full =
            await commands.FetchAsync(new McpFetchRequest([uri], null, null));
        full.IsSuccess.Should().BeTrue(full.Error?.Detail);
        XElement page = XElement.Parse(full.Envelope!.Entries.Single().Content!);
        page.Attribute("complete")!.Value.Should().Be("true");
        page.Descendants("box").Should().BeEmpty();
        foreach (string range in new[] { "boxes:1-1", "boxes:2-2" })
        {
            McpCommandResult<McpFetchMeta, McpFetchResult> invalid =
                await commands.FetchAsync(new McpFetchRequest([uri], range, null));
            invalid.Error!.Code.Should().Be((int)McpErrorCode.InvalidArgument);
        }
    }

    [Fact]
    public async Task Canonical_resources_use_yaml_xml_and_legacy_fetches_warn_without_changing_the_body()
    {
        await using TranslationTestContext fixture = await TranslationTestContext.CreateAsync();
        await fixture.CommitAsync(Seed(0, "Body & text"), Seed(1, "Footnote", true));
        McpCommandService commands = Commands(fixture);
        string page = McpResourceUris.PageUri(fixture.DocumentId, 1);
        McpCommandResult<McpFetchMeta, McpFetchResult> fetched =
            await commands.FetchAsync(new McpFetchRequest([page, "library.yml"], null, null));
        fetched.IsSuccess.Should().BeTrue(fetched.Error?.Detail);
        XElement xml = XElement.Parse(fetched.Envelope!.Entries[0].Content!);
        xml.Attribute("complete")!.Value.Should().Be("true");
        xml.Descendants("box").Should().HaveCount(2);
        xml.Descendants("payload").Select(value => value.Value).Should().Contain("Footnote");
        fetched.Envelope.Entries[0].ContentType.Should().Be("application/xml");
        McpYamlCodec.Decode(fetched.Envelope.Entries[1].Content!).Should().NotBeNull();
        McpCommandResult<McpFetchMeta, McpFetchResult> legacy =
            await commands.FetchAsync(new McpFetchRequest([page.Replace(".xml", ".md"), "./library.toon"], null, null));
        legacy.IsSuccess.Should().BeTrue(legacy.Error?.Detail);
        legacy.Envelope!.Message!.Warnings.Should().HaveCount(2).And
            .OnlyContain(value => value.Contains("[obsoleted]") && value.Contains("0.4.0"));
        legacy.Envelope.Entries[0].Uri.Should().Be(page);
        legacy.Envelope.Entries[0].Content.Should().Contain("Body & text").And.NotContain("<page").And
            .NotContain("[obsoleted]");
        legacy.Envelope.Entries[1].Uri.Should().Be("patchouli://library.yml");
    }

    [Fact]
    public async Task Full_xml_translation_is_keyed_by_box_and_preserves_suppressed_footnotes()
    {
        await using TranslationTestContext fixture = await TranslationTestContext.CreateAsync();
        await fixture.CommitAsync(Seed(0, "Body"), Seed(1, "Note", true));
        McpCommandService commands = Commands(fixture);
        string uri = McpResourceUris.TranslationPageUri(fixture.DocumentId, 1);
        McpCommandResult<McpFetchMeta, McpFetchResult> fetched =
            await commands.FetchAsync(new McpFetchRequest([uri], null, null));
        fetched.IsSuccess.Should().BeTrue(fetched.Error?.Detail);
        XElement page = XElement.Parse(fetched.Envelope!.Entries.Single().Content!);
        foreach (XElement box in page.Descendants("box"))
        {
            box.SetAttributeValue("translated", "true");
            box.Element("payload")!.Value = "Translated " + box.Element("payload")!.Value;
        }

        McpCommandResult<McpPutMeta, McpPutResult> written =
            await commands.PutAsync(new McpPutRequest(uri, page.ToString()));
        written.IsSuccess.Should().BeTrue(written.Error?.Detail);
        written.Envelope!.Entries.Single().TranslationVersion.Should().Be(1);
        (await fixture.ListTranslationRowsAsync()).Should().HaveCount(2);
        McpCommandResult<McpPutMeta, McpPutResult> conflict =
            await commands.PutAsync(new McpPutRequest(uri, page.ToString()));
        conflict.Error!.Code.Should().Be((int)McpErrorCode.RevisionConflict);
        (await fixture.GetVersionAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Xml_box_range_is_parseable_and_cannot_replace_a_complete_translation()
    {
        await using TranslationTestContext fixture = await TranslationTestContext.CreateAsync();
        await fixture.CommitAsync(Seed(0, "First"), Seed(1, "Second"));
        McpCommandService commands = Commands(fixture);
        string uri = McpResourceUris.TranslationPageUri(fixture.DocumentId, 1);
        McpCommandResult<McpFetchMeta, McpFetchResult> fetched =
            await commands.FetchAsync(new McpFetchRequest([uri], "boxes:1-1", null));
        fetched.IsSuccess.Should().BeTrue(fetched.Error?.Detail);
        McpFetchResult entry = fetched.Envelope!.Entries.Single();
        XElement.Parse(entry.Content!).Descendants("box").Should().ContainSingle();
        entry.Complete.Should().BeFalse();
        entry.NextRange.Should().Be("boxes:2-2");
        McpCommandResult<McpPutMeta, McpPutResult> refused =
            await commands.PutAsync(new McpPutRequest(uri, entry.Content));
        refused.IsSuccess.Should().BeFalse();
        (await fixture.GetVersionAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Translation_box_batches_preserve_other_rows_and_report_exact_versions()
    {
        await using TranslationTestContext fixture = await TranslationTestContext.CreateAsync();
        DocumentTreeRevision original = await fixture.CommitAsync(Seed(0, "First"), Seed(1, "Second", true));
        McpCommandService commands = Commands(fixture);
        string uri = McpResourceUris.TranslationPageUri(fixture.DocumentId, 1);
        XElement source = XElement.Parse((await commands.FetchAsync(new McpFetchRequest([uri], null, null))).Envelope!
            .Entries.Single().Content!);
        XElement[] boxes = source.Descendants("box").ToArray();
        string firstId = (string)boxes[0].Attribute("id")!;
        string secondId = (string)boxes[1].Attribute("id")!;
        boxes[0].Element("payload")!.Value = "Translated first";
        McpCommandResult<McpPutMeta, McpPutResult> first = await commands.PutAsync(new McpPutRequest(uri,
            SourceRevision: original.TreeRevisionId.ToString(), TranslationVersion: 0,
            Boxes: [new McpBoxReplacement(firstId, boxes[0].ToString())]));
        first.IsSuccess.Should().BeTrue(first.Error?.Detail);
        first.Envelope!.Entries.Single().ModifiedBoxIds.Should().Equal(firstId);
        first.Envelope.Entries.Single().TranslationVersion.Should().Be(1);
        boxes[1].Element("payload")!.Value = "Translated note";
        McpCommandResult<McpPutMeta, McpPutResult> second = await commands.PutAsync(new McpPutRequest(uri,
            SourceRevision: original.TreeRevisionId.ToString(), TranslationVersion: 1,
            Boxes: [new McpBoxReplacement(secondId, boxes[1].ToString())]));
        second.IsSuccess.Should().BeTrue(second.Error?.Detail);
        second.Envelope!.Entries.Single().TranslationVersion.Should().Be(2);
        (await fixture.ListTranslationRowsAsync()).Select(row => row.TranslatedMd).Should()
            .BeEquivalentTo("Translated first", "Translated note");
        McpCommandResult<McpPutMeta, McpPutResult> conflict = await commands.PutAsync(new McpPutRequest(uri,
            SourceRevision: original.TreeRevisionId.ToString(), TranslationVersion: 1,
            Boxes: [new McpBoxReplacement(firstId, boxes[0].ToString())]));
        conflict.Error!.Code.Should().Be((int)McpErrorCode.RevisionConflict);
        (await fixture.GetVersionAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Invalid_batch_member_rejects_all_changes_and_names_the_bad_box()
    {
        await using TranslationTestContext fixture = await TranslationTestContext.CreateAsync();
        DocumentTreeRevision original = await fixture.CommitAsync(Seed(0, "First"));
        McpCommandService commands = Commands(fixture);
        string uri = McpResourceUris.TranslationPageUri(fixture.DocumentId, 1);
        XElement box = XElement
            .Parse((await commands.FetchAsync(new McpFetchRequest([uri], null, null))).Envelope!.Entries.Single()
                .Content!).Element("box")!;
        string id = (string)box.Attribute("id")!;
        box.Element("payload")!.Value = "Changed";
        string missing = DocumentBoxId.New().ToString();
        McpCommandResult<McpPutMeta, McpPutResult> rejected = await commands.PutAsync(new McpPutRequest(uri,
            SourceRevision: original.TreeRevisionId.ToString(), TranslationVersion: 0,
            Boxes: [new McpBoxReplacement(id, box.ToString()), new McpBoxReplacement(missing, box.ToString())]));
        rejected.Error!.Code.Should().Be((int)McpErrorCode.InvalidContent);
        rejected.Envelope!.Entries.Single().BoxErrors.Should().Contain(error => error.BoxId == missing);
        (await fixture.ListTranslationRowsAsync()).Should().BeEmpty();
        (await fixture.GetVersionAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Source_box_batch_creates_history_and_keeps_untouched_box_identity()
    {
        await using TranslationTestContext fixture = await TranslationTestContext.CreateAsync();
        DocumentTreeRevision original = await fixture.CommitAsync(Seed(0, "First"), Seed(1, "Second"));
        McpCommandService commands = Commands(fixture);
        string uri = McpResourceUris.PageUri(fixture.DocumentId, 1);
        XElement page = XElement.Parse((await commands.FetchAsync(new McpFetchRequest([uri], null, null))).Envelope!
            .Entries.Single().Content!);
        XElement[] boxes = page.Descendants("box").ToArray();
        string id = (string)boxes[0].Attribute("id")!;
        boxes[0].Element("payload")!.Value = "Corrected first";
        McpCommandResult<McpPutMeta, McpPutResult> written = await commands.PutAsync(new McpPutRequest(uri,
            SourceRevision: original.TreeRevisionId.ToString(),
            Boxes: [new McpBoxReplacement(id, boxes[0].ToString())]));
        written.IsSuccess.Should().BeTrue(written.Error?.Detail);
        string revision = written.Envelope!.Entries.Single().SourceRevision!;
        revision.Should().NotBe(original.TreeRevisionId.ToString());
        XElement current = XElement.Parse((await commands.FetchAsync(new McpFetchRequest([uri], null, null))).Envelope!
            .Entries.Single().Content!);
        current.Descendants("box").Select(box => (string)box.Attribute("id")!).Should()
            .Equal(boxes.Select(box => (string)box.Attribute("id")!));
        current.Descendants("payload").Select(payload => payload.Value).Should().Equal("Corrected first", "Second");
        string historical = uri + "?rev=" + original.TreeRevisionId;
        XElement history =
            XElement.Parse((await commands.FetchAsync(new McpFetchRequest([historical], null, null))).Envelope!.Entries
                .Single().Content!);
        history.Descendants("payload").First().Value.Should().Be("First");
        McpCommandResult<McpPutMeta, McpPutResult> stale = await commands.PutAsync(new McpPutRequest(uri,
            SourceRevision: original.TreeRevisionId.ToString(),
            Boxes: [new McpBoxReplacement(id, boxes[0].ToString())]));
        stale.Error!.Code.Should().Be((int)McpErrorCode.RevisionConflict);
        McpCommandResult<McpPutMeta, McpPutResult> forbidden = await commands.PutAsync(new McpPutRequest(historical,
            SourceRevision: original.TreeRevisionId.ToString(),
            Boxes: [new McpBoxReplacement(id, boxes[0].ToString())]));
        forbidden.Error!.Code.Should().Be((int)McpErrorCode.PermissionDenied);
    }

    private static DocumentBoxSeed Seed(int index, string text, bool suppressed = false)
    {
        return new DocumentBoxSeed(DocumentBoxId.New(), null, index,
            suppressed ? DocumentBoxType.PageFootnote : DocumentBoxType.Text,
            null, new NormalizedBBox(0.1, 0.1 + index * 0.3, 0.7, 0.2), new TextBoxPayload(text),
            Suppressed: suppressed);
    }

    private static McpCommandService Commands(TranslationTestContext fixture)
    {
        LibraryIdentityService library = new(fixture.ConnectionFactory, fixture.Clock);
        ItemService items = new(fixture.ConnectionFactory, library, fixture.Clock);
        DocumentInstanceService documents = new(fixture.ConnectionFactory, fixture.Clock);
        BiblatexHelperClient helper = new();
        McpReadApi read = new(fixture.ConnectionFactory, new SqliteSearchService(fixture.ConnectionFactory),
            markdown: fixture.Markdown, markdownCompiler: fixture.MarkdownCompiler,
            pageTranslations: fixture.Translations);
        McpWriteApi write = new(items, helper, new CslStyleStore(fixture.ConnectionFactory, fixture.Clock),
            fixture.Translations, fixture.Trees);
        BiblatexImportService bibliography = new(helper, items,
            new FileAssetService(fixture.ConnectionFactory, library, fixture.Clock), documents);
        return new McpCommandService(read, write, bibliography, items,
            new VersionedEvidenceReader(fixture.ConnectionFactory, library, fixture.Trees, fixture.MarkdownCompiler));
    }
}
