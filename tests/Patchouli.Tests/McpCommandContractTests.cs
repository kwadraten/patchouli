using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Csl;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Bibliography.Biblatex;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Operations;
using Patchouli.Infrastructure.Search;
using Patchouli.Mcp;
using Xunit;

namespace Patchouli.Tests;

public sealed class McpCommandContractTests : IAsyncLifetime
{
    private const string ApaStyleXml =
        """
        <style xmlns="http://purl.org/net/xbiblio/csl" class="in-text" version="1.0">
          <info><title>APA 7th</title><id>apa</id></info>
          <citation><layout><text variable="title"/></layout></citation>
          <bibliography><layout><text variable="title"/></layout></bibliography>
        </style>
        """;

    private string _databasePath = null!;
    private TestLibrary _library = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"patchouli-contract-{Guid.NewGuid():N}.sqlite");
        _library = await TestLibrary.SeedAsync(_databasePath);
    }

    public Task DisposeAsync()
    {
        try
        {
            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Find_root_returns_only_the_three_vfs_directories_without_message()
    {
        McpCommandResult<McpFindMeta, object> result =
            await _library.Commands.FindAsync(new McpFindRequest(null, null, null));

        result.IsSuccess.Should().BeTrue();
        McpEnvelope<McpFindMeta, object> envelope = result.Envelope!;
        envelope.Continuation.Should().BeNull();
        envelope.Message.Should().BeNull("a clean success omits message");
        envelope.Meta.ShownTotal.Should().Be(3);
        envelope.Meta.DomainTotal.Should().Be(3);
        envelope.Meta.FilteredTotal.Should().Be(3);
        envelope.Meta.LibraryRevision.Should().MatchRegex("^lib:[0-9]+$");
        envelope.Entries.Select(Entry).Select(entry => entry.Uri).Should().Equal(
            "patchouli://items/",
            "patchouli://texts/",
            "patchouli://csl-styles/");
        envelope.Entries.Select(Entry).Should().OnlyContain(entry => entry.Type == "directory");
    }

    [Fact]
    public async Task Find_root_rejects_query_where_and_legacy_scopes()
    {
        McpCommandResult<McpFindMeta, object> query = await _library.Commands.FindAsync(
            new McpFindRequest("something", null, null));
        query.IsSuccess.Should().BeFalse();
        query.Error!.Code.Should().Be((int)McpErrorCode.InvalidArgument);

        McpCommandResult<McpFindMeta, object> where = await _library.Commands.FindAsync(
            new McpFindRequest(null, null, [new McpWhereClause("item_type", "book")]));
        where.Error!.Code.Should().Be((int)McpErrorCode.InvalidArgument);

        McpCommandResult<McpFindMeta, object> legacyDocuments = await _library.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://documents/", null));
        legacyDocuments.Error!.Code.Should().Be((int)McpErrorCode.InvalidArgument);

        McpCommandResult<McpFindMeta, object> legacyStyles = await _library.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://styles/", null));
        legacyStyles.Error!.Code.Should().Be((int)McpErrorCode.InvalidArgument);

        McpCommandResult<McpFindMeta, object> legacyEvidence = await _library.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://evidence/", null));
        legacyEvidence.Error!.Code.Should().Be((int)McpErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task Find_browses_items_texts_and_styles_scopes()
    {
        McpCommandResult<McpFindMeta, object> items = await _library.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://items/", null));
        items.IsSuccess.Should().BeTrue();
        items.Envelope!.Meta.ShownTotal.Should().Be(3);
        items.Envelope.Entries.Select(Entry).Should().OnlyContain(entry => entry.Type == "file");
        items.Envelope.Entries.Select(Entry).Should()
            .Contain(entry => entry.Uri == McpResourceUris.ItemUri(_library.BookA));

        McpCommandResult<McpFindMeta, object> texts = await _library.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://texts/", null));
        texts.IsSuccess.Should().BeTrue();
        texts.Envelope!.Meta.ShownTotal.Should().Be(2);
        texts.Envelope.Entries.Select(Entry).Should().OnlyContain(entry => entry.Type == "directory");
        texts.Envelope.Entries.Select(Entry).Should()
            .Contain(entry => entry.Uri == McpResourceUris.DocumentUri(_library.DocumentA));

        McpCommandResult<McpFindMeta, object> styles = await _library.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://csl-styles/", null));
        styles.IsSuccess.Should().BeTrue();
        styles.Envelope!.Meta.ShownTotal.Should().Be(1);
        styles.Envelope.Entries.Select(Entry).Should().Contain(entry => entry.Uri == McpResourceUris.StyleUri("apa"));
    }

    [Fact]
    public async Task Find_default_entries_are_strictly_uri_title_type()
    {
        McpCommandResult<McpFindMeta, object> result = await _library.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://items/", null));
        JsonNode root = JsonSerializer.SerializeToNode(result.Envelope!)!;
        JsonNode first = root["entries"]![0]!;
        first.AsObject().Select(pair => pair.Key).Should().Equal("uri", "title", "type");
        first["uri"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        first["title"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        first["type"]!.GetValue<string>().Should().Be("file");
        root.AsObject().Select(pair => pair.Key).Should().Equal("meta", "continuation", "entries");
    }

    [Fact]
    public async Task Find_long_item_projection_exposes_only_item_fields()
    {
        McpCommandResult<McpFindMeta, object> result = await _library.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://items/", null, Long: true));
        result.IsSuccess.Should().BeTrue();

        McpItemLongEntry item = (McpItemLongEntry)result.Envelope!.Entries.Single(entry =>
            ((McpItemLongEntry)entry).Uri == McpResourceUris.ItemUri(_library.BookA));
        item.ItemStatus.Should().NotBeNull();
        item.Citable.Should().BeTrue();
        item.PrimaryDocumentOcrIndexStatus.Should().Be("ocr_not_indexed");

        JsonNode root = JsonSerializer.SerializeToNode(result.Envelope!)!;
        root["entries"]![0]!.AsObject().Select(pair => pair.Key).Should().Equal(
            "uri", "title", "type", "item_status", "primary_document_ocr_index_status", "citable");
    }

    [Fact]
    public async Task Primary_document_ocr_index_state_is_shared_by_library_rows_and_mcp()
    {
        LibraryItemQueryService libraryRows = new(_library.ConnectionFactory);
        Result<IReadOnlyList<LibraryItemRow>> rows = await libraryRows.ListRowsAsync();
        LibraryItemRow uiRow = rows.Value.Single(row => row.ItemId == _library.BookA);

        McpCommandResult<McpFindMeta, object> result = await _library.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://items/", null, Long: true));
        McpItemLongEntry mcpEntry = (McpItemLongEntry)result.Envelope!.Entries.Single(entry =>
            ((McpItemLongEntry)entry).Uri == McpResourceUris.ItemUri(_library.BookA));

        mcpEntry.PrimaryDocumentOcrIndexStatus.Should().Be(uiRow.PrimaryDocumentOcrIndexState.Value);
        uiRow.PrimaryDocumentOcrIndexState.ChineseLabel.Should().NotBeNullOrWhiteSpace();
        uiRow.PrimaryDocumentOcrIndexState.Detail.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Find_search_texts_returns_versioned_evidence_page_uris()
    {
        McpCommandResult<McpFindMeta, object> result = await _library.Commands.FindAsync(
            new McpFindRequest("quorum", "patchouli://texts/", null));
        result.IsSuccess.Should().BeTrue($"error: {result.Error?.Code} {result.Error?.Detail}");
        result.Envelope!.Entries.Should().NotBeEmpty();
        McpFindEntry first = Entry(result.Envelope.Entries.First());
        first.Uri.Should().Match($"patchouli://texts/{_library.DocumentA}/page-*");
        first.Uri.Should().Contain("?rev=");
        first.Uri.Should().Contain("&box=");
        first.Type.Should().Be("file");
    }

    [Fact]
    public async Task Find_long_text_projection_uses_raw_entity_statuses_and_shared_fsm()
    {
        McpCommandResult<McpFindMeta, object> result = await _library.Commands.FindAsync(
            new McpFindRequest("quorum", "patchouli://texts/", null, Long: true));
        McpTextLongEntry entry = result.Envelope!.Entries.OfType<McpTextLongEntry>().First();

        entry.ItemStatus.Should().Be("active");
        entry.DocumentStatus.Should().Be("active");
        entry.SourceStatus.Should().Be("unavailable");
        entry.OcrIndexStatus.Should().Be("ocr_not_indexed");

        JsonNode node = JsonSerializer.SerializeToNode(entry)!;
        node.AsObject().Select(pair => pair.Key).Should().Equal(
            "uri", "title", "type", "item_uri", "item_status", "document_status", "source_status",
            "ocr_index_status", "citable");
    }

    [Fact]
    public async Task Singleton_text_filters_use_the_same_ocr_index_state_as_browse()
    {
        McpWhereClause filter = new("ocr_index_status", "ocr_not_indexed");
        McpCommandResult<McpFindMeta, object> browse = await _library.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://texts/", [filter]));
        browse.IsSuccess.Should().BeTrue($"error: {browse.Error?.Code} {browse.Error?.Detail}");
        browse.Envelope!.Entries.Should().HaveCount(2);

        string documentUri = McpResourceUris.DocumentUri(_library.DocumentA);
        McpCommandResult<McpFindMeta, object> document = await _library.Commands.FindAsync(
            new McpFindRequest(null, documentUri, [filter]));
        document.IsSuccess.Should().BeTrue();
        document.Envelope!.Entries.Should().ContainSingle();

        string pageUri = McpResourceUris.PageUri(_library.DocumentA, 1);
        McpCommandResult<McpFindMeta, object> page = await _library.Commands.FindAsync(
            new McpFindRequest(null, pageUri, [filter]));
        page.IsSuccess.Should().BeTrue();
        page.Envelope!.Entries.Should().ContainSingle();

        McpCommandResult<McpFindMeta, object> search = await _library.Commands.FindAsync(
            new McpFindRequest("quorum", "patchouli://texts/", null));
        string evidenceUri = Entry(search.Envelope!.Entries.First()).Uri;
        McpCommandResult<McpFindMeta, object> evidence = await _library.Commands.FindAsync(
            new McpFindRequest(null, evidenceUri, [filter]));
        evidence.IsSuccess.Should().BeTrue();
        evidence.Envelope!.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task Find_literal_requires_query_and_matches_without_rewriting()
    {
        McpCommandResult<McpFindMeta, object> literal = await _library.Commands.FindAsync(
            new McpFindRequest("Quorum", "patchouli://texts/", null, true));
        literal.IsSuccess.Should().BeTrue();
        literal.Envelope!.Entries.Should().NotBeEmpty();

        McpCommandResult<McpFindMeta, object> noMatch = await _library.Commands.FindAsync(
            new McpFindRequest("zzzz-no-match", "patchouli://texts/", null, true));
        noMatch.IsSuccess.Should().BeTrue();
        noMatch.Envelope!.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Find_whitespace_query_browses_with_warning()
    {
        McpCommandResult<McpFindMeta, object> result = await _library.Commands.FindAsync(
            new McpFindRequest("   ", "patchouli://items/", null));
        result.IsSuccess.Should().BeTrue();
        result.Envelope!.Message.Should().NotBeNull();
        result.Envelope.Message!.Warnings.Should().Contain(
            McpWarningCodes.ToTerminalLine(McpWarningCodes.WhitespaceQueryTreatedAsBrowse));
        result.Envelope.Message.Error.Should().BeNull();
        result.Envelope.Entries.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Find_where_filters_and_duplicate_key_last_wins()
    {
        McpCommandResult<McpFindMeta, object> books = await _library.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://items/", [new McpWhereClause("item_type", "book")]));
        books.IsSuccess.Should().BeTrue();
        books.Envelope!.Meta.FilteredTotal.Should().Be(2);
        books.Envelope.Entries.Select(Entry).Should()
            .OnlyContain(entry => entry.Uri != McpResourceUris.ItemUri(_library.GeneralItem));

        McpCommandResult<McpFindMeta, object> ocrNotIndexed = await _library.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://items/",
                [new McpWhereClause("primary_document_ocr_index_status", "ocr_not_indexed")]));
        ocrNotIndexed.IsSuccess.Should().BeTrue();
        ocrNotIndexed.Envelope!.Entries.Should().HaveCount(2);

        McpCommandResult<McpFindMeta, object> noPrimaryDocument = await _library.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://items/",
                [new McpWhereClause("primary_document_ocr_index_status", "no_primary_document")]));
        noPrimaryDocument.Envelope!.Entries.Select(Entry).Should()
            .ContainSingle(entry => entry.Uri == McpResourceUris.ItemUri(_library.GeneralItem));

        McpCommandResult<McpFindMeta, object> duplicated = await _library.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://items/",
                [new McpWhereClause("item_type", "book"), new McpWhereClause("item_type", "general")]));
        duplicated.IsSuccess.Should().BeTrue();
        duplicated.Envelope!.Message.Should().NotBeNull();
        duplicated.Envelope.Message!.Warnings.Should()
            .Contain(McpWarningCodes.ToTerminalLine(McpWarningCodes.DuplicateWhereKeyLastWins));
        duplicated.Envelope.Entries.Select(Entry).Should()
            .OnlyContain(entry => entry.Uri == McpResourceUris.ItemUri(_library.GeneralItem));

        McpCommandResult<McpFindMeta, object> unsupported = await _library.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://items/", [new McpWhereClause("status", "active")]));
        unsupported.Error!.Code.Should().Be((int)McpErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task Find_cursor_paginates_and_warns_result_set_may_have_changed()
    {
        McpCommandResult<McpFindMeta, object> first = await _library.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://items/", null, false, 2));
        first.IsSuccess.Should().BeTrue();
        first.Envelope!.Entries.Should().HaveCount(2);
        first.Envelope.Continuation.Should().NotBeNull();
        first.Envelope.Message!.Warnings.Should()
            .Contain(McpWarningCodes.ToTerminalLine(McpWarningCodes.ResultSetMayHaveChanged));

        McpCommandResult<McpFindMeta, object> second = await _library.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://items/", null, false, 2, first.Envelope.Continuation));
        second.IsSuccess.Should().BeTrue();
        second.Envelope!.Entries.Should().HaveCount(1);
        second.Envelope.Message!.Warnings.Should()
            .Contain(McpWarningCodes.ToTerminalLine(McpWarningCodes.ResultSetMayHaveChanged));

        McpCommandResult<McpFindMeta, object> invalid = await _library.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://items/", null, false, 2, "not-a-cursor"));
        invalid.Error!.Code.Should().Be((int)McpErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task Find_cursor_conflicting_context_is_restored_with_warning()
    {
        McpCommandResult<McpFindMeta, object> first = await _library.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://items/", null, false, 2));
        first.Envelope!.Continuation.Should().NotBeNull();
        string[] firstPageUris = first.Envelope.Entries.Select(Entry).Select(entry => entry.Uri).ToArray();

        McpCommandResult<McpFindMeta, object> second = await _library.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://items/", [new McpWhereClause("item_type", "book")], false, 2,
                first.Envelope.Continuation));
        second.IsSuccess.Should().BeTrue();
        second.Envelope!.Message!.Warnings.Should()
            .Contain(McpWarningCodes.ToTerminalLine(McpWarningCodes.CursorContextRestored));
        second.Envelope.Entries.Select(Entry).Should().NotContain(entry => firstPageUris.Contains(entry.Uri));
    }

    [Fact]
    public async Task Find_file_uri_scope_returns_singleton_with_warning()
    {
        McpCommandResult<McpFindMeta, object> result = await _library.Commands.FindAsync(
            new McpFindRequest(null, McpResourceUris.ItemUri(_library.BookA), null));
        result.IsSuccess.Should().BeTrue();
        result.Envelope!.Message!.Warnings.Should()
            .Contain(McpWarningCodes.ToTerminalLine(McpWarningCodes.FileUriSingletonScope));
        McpFindEntry entry = Entry(result.Envelope.Entries.Should().ContainSingle().Subject);
        entry.Uri.Should().Be(McpResourceUris.ItemUri(_library.BookA));
        entry.Title.Should().Be("Distributed Systems Notes");
    }

    [Fact]
    public async Task Fetch_item_returns_biblatex_complete_projection()
    {
        McpCommandResult<McpFetchMeta, McpFetchResult> result = await _library.Commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.ItemUri(_library.BookA)], null, null));
        result.IsSuccess.Should().BeTrue();
        McpFetchResult entry = result.Envelope!.Entries.Should().ContainSingle().Subject;
        entry.ResourceType.Should().Be("item_bib");
        entry.ItemUri.Should().Be(McpResourceUris.ItemUri(_library.BookA));
        entry.Complete.Should().BeTrue();
        entry.Truncated.Should().BeFalse();
        entry.Error.Should().BeNull();
        entry.ReturnedBytes.Should().Be(Encoding.UTF8.GetByteCount(entry.Content!));
        entry.Content.Should().Contain("@book{");
        entry.Content.Should().Contain("Distributed Systems Notes");
        result.Envelope!.Continuation.Should().BeNull();
        result.Envelope.Message.Should().BeNull();
    }

    [Fact]
    public async Task Fetch_document_returns_outline_with_page_links()
    {
        McpCommandResult<McpFetchMeta, McpFetchResult> result = await _library.Commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.DocumentUri(_library.DocumentA)], null, null));
        McpFetchResult entry = result.Envelope!.Entries.Should().ContainSingle().Subject;
        entry.ResourceType.Should().Be("text_document");
        entry.ItemUri.Should().Be(McpResourceUris.ItemUri(_library.BookA));
        entry.Content.Should().Contain("Distributed Systems Notes");
        entry.Content.Should().Contain($"/page-1.md");
        entry.Content.Should().Contain($"/page-2.md");
    }

    [Fact]
    public async Task Fetch_page_uses_one_based_page_index()
    {
        McpCommandResult<McpFetchMeta, McpFetchResult> first = await _library.Commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.PageUri(_library.DocumentA, 1)], null, null));
        first.Envelope!.Entries.Single().Content.Should().Contain("Consensus requires a quorum.");

        McpCommandResult<McpFetchMeta, McpFetchResult> second = await _library.Commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.PageUri(_library.DocumentA, 2)], null, null));
        second.Envelope!.Entries.Single().Content.Should().Contain("A quorum of replicas must agree");

        McpCommandResult<McpFetchMeta, McpFetchResult> missing = await _library.Commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.PageUri(_library.DocumentA, 99)], null, null));
        ErrorCode(missing.Envelope!.Entries.Single().Error).Should().Be((int)McpErrorCode.NotFound);
    }

    [Fact]
    public async Task Fetch_document_pages_range_returns_selected_pages()
    {
        McpCommandResult<McpFetchMeta, McpFetchResult> result = await _library.Commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.DocumentUri(_library.DocumentA)], "pages:2-2", null));
        McpFetchResult entry = result.Envelope!.Entries.Single();
        entry.Content.Should().Contain($"/page-2.md");
        entry.Content.Should().NotContain($"/page-1.md");
    }

    [Fact]
    public async Task Fetch_style_returns_csl_xml()
    {
        McpCommandResult<McpFetchMeta, McpFetchResult> result = await _library.Commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.StyleUri("apa")], null, null));
        McpFetchResult entry = result.Envelope!.Entries.Single();
        entry.ResourceType.Should().Be("csl_style");
        entry.Complete.Should().BeTrue();
        entry.Content.Should().Contain("<style");
    }

    [Fact]
    public async Task Fetch_evidence_validates_page_ownership_and_library()
    {
        McpCommandResult<McpFindMeta, object> search = await _library.Commands.FindAsync(
            new McpFindRequest("quorum", "patchouli://texts/", null));
        string evidenceUri = Entry(search.Envelope!.Entries.First()).Uri;

        McpCommandResult<McpFetchMeta, McpFetchResult> ok = await _library.Commands.FetchAsync(
            new McpFetchRequest([evidenceUri], null, null));
        ok.Envelope!.Entries.Single().ResourceType.Should().Be("evidence");
        ok.Envelope.Entries.Single().Content.Should().Contain("quorum");

        Result<McpUriParseResult> parsed = McpResourceUris.Parse(evidenceUri);
        parsed.IsSuccess.Should().BeTrue();
        DocumentTreeRevisionId rev = parsed.Value.TreeRevisionId!.Value;
        DocumentBoxId box = parsed.Value.BoxId!.Value;

        McpCommandResult<McpFetchMeta, McpFetchResult> wrongPage = await _library.Commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.EvidencePageUri(_library.DocumentA, 99, rev, box)], null, null));
        ErrorCode(wrongPage.Envelope!.Entries.Single().Error).Should().Be((int)McpErrorCode.NotFound);

        McpCommandResult<McpFetchMeta, McpFetchResult> wrongDocument = await _library.Commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.EvidencePageUri(_library.DocumentB, 1, rev, box)], null, null));
        ErrorCode(wrongDocument.Envelope!.Entries.Single().Error).Should().Be((int)McpErrorCode.NotFound);
    }

    [Fact]
    public async Task Fetch_missing_resource_and_legacy_uri_return_failed_entries()
    {
        McpCommandResult<McpFetchMeta, McpFetchResult> missing = await _library.Commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.ItemUri(new ItemId(Guid.NewGuid()))], null, null));
        ErrorCode(missing.Envelope!.Entries.Single().Error).Should().Be((int)McpErrorCode.NotFound);
        missing.IsSuccess.Should().BeFalse();

        McpCommandResult<McpFetchMeta, McpFetchResult> legacy = await _library.Commands.FetchAsync(
            new McpFetchRequest(["patchouli://documents/"], null, null));
        ErrorCode(legacy.Envelope!.Entries.Single().Error).Should().Be((int)McpErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task Fetch_multi_uri_keeps_independent_results()
    {
        McpCommandResult<McpFetchMeta, McpFetchResult> result = await _library.Commands.FetchAsync(
            new McpFetchRequest(
            [
                McpResourceUris.ItemUri(_library.BookA),
                McpResourceUris.ItemUri(new ItemId(Guid.NewGuid()))
            ], null, null));
        result.Envelope!.Entries.Should().HaveCount(2);
        result.Envelope.Entries[0].Complete.Should().BeTrue();
        ErrorCode(result.Envelope.Entries[1].Error).Should().Be((int)McpErrorCode.NotFound);
        result.IsSuccess.Should().BeTrue("a single failed URI must not fail the request");
        result.Envelope.Message.Should().BeNull();
    }

    [Fact]
    public async Task Fetch_oversized_response_is_truncated_not_complete()
    {
        McpCommandResult<McpFetchMeta, McpFetchResult> result = await _library.Commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.ItemUri(_library.BookA)], null, 64));
        McpFetchResult entry = result.Envelope!.Entries.Single();
        entry.Complete.Should().BeFalse();
        entry.Truncated.Should().BeTrue();
        ErrorCode(entry.Error).Should().Be((int)McpErrorCode.ResponseTruncated);
        (entry.Continuation ?? entry.NextRange).Should().NotBeNull();
        entry.ReturnedBytes.Should().BeLessThanOrEqualTo(64);
        result.IsSuccess.Should().BeFalse();
        ErrorCode(result.Envelope.Message!.Error).Should().Be((int)McpErrorCode.ResponseTruncated);
    }

    [Fact]
    public async Task Fetch_rejects_wrong_range_kinds()
    {
        McpCommandResult<McpFetchMeta, McpFetchResult> pagesOnItem = await _library.Commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.ItemUri(_library.BookA)], "pages:1-1", null));
        ErrorCode(pagesOnItem.Envelope!.Entries.Single().Error).Should().Be((int)McpErrorCode.InvalidArgument);

        McpCommandResult<McpFetchMeta, McpFetchResult> linesOnDocument = await _library.Commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.DocumentUri(_library.DocumentA)], "lines:1-2", null));
        ErrorCode(linesOnDocument.Envelope!.Entries.Single().Error).Should().Be((int)McpErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task Fetch_never_exposes_revision_or_resource_revision_fields()
    {
        McpCommandResult<McpFetchMeta, McpFetchResult> result = await _library.Commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.ItemUri(_library.BookA)], null, null));
        JsonNode root = JsonSerializer.SerializeToNode(result.Envelope!)!;
        root.AsObject().Select(pair => pair.Key).Should().NotContain(new[] { "revision", "data", "warnings", "error" });
        root["entries"]![0]!.AsObject().Select(pair => pair.Key).Should().NotContain("revision");
        root["entries"]![0]!.AsObject().Select(pair => pair.Key).Should().NotContain("resource_revision");
    }

    [Fact]
    public async Task Put_replaces_an_item_bib_without_base_revision()
    {
        McpCommandResult<McpFetchMeta, McpFetchResult> fetched = await _library.Commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.ItemUri(_library.BookA)], null, null));
        string key = RegexKey(fetched.Envelope!.Entries.Single().Content!);

        string updated =
            "@book{" + key + ",\n" +
            "  author = {Doe, Jane},\n" +
            "  title = {Distributed Systems Notes, Second Edition},\n" +
            "  publisher = {Example Press},\n" +
            "  year = {2024}\n" +
            "}\n";

        McpCommandResult<McpPutMeta, McpPutResult> put = await _library.Commands.PutAsync(
            new McpPutRequest(McpResourceUris.ItemUri(_library.BookA), updated));
        put.IsSuccess.Should().BeTrue($"error: {put.Error?.Code} {put.Error?.Detail}");
        McpPutResult putEntry = put.Envelope!.Entries.Should().ContainSingle().Subject;
        putEntry.Uri.Should().Be(McpResourceUris.ItemUri(_library.BookA));
        putEntry.ResourceType.Should().Be("item_bib");
        putEntry.Committed.Should().BeTrue();
        putEntry.ContentBytes.Should().Be(Encoding.UTF8.GetByteCount(updated));
        put.Envelope.Meta.LibraryRevision.Should().MatchRegex("^lib:[0-9]+$");
        McpCommandResult<McpFetchMeta, McpFetchResult> refreshed = await _library.Commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.ItemUri(_library.BookA)], null, null));
        refreshed.Envelope!.Entries.Single().Content.Should().Contain("Second Edition");
    }

    [Fact]
    public async Task Put_ignores_the_agent_supplied_biblatex_key_and_preserves_the_item_key()
    {
        string uri = McpResourceUris.ItemUri(_library.BookA);
        McpCommandResult<McpFetchMeta, McpFetchResult> before = await _library.Commands.FetchAsync(
            new McpFetchRequest([uri], null, null));
        string expectedKey = RegexKey(before.Envelope!.Entries.Single().Content!);
        const string agentSuppliedKey = "agent-chosen-key";

        McpCommandResult<McpPutMeta, McpPutResult> put = await _library.Commands.PutAsync(
            new McpPutRequest(uri,
                $"@book{{{agentSuppliedKey},\n" +
                "  author = {Doe, Jane},\n" +
                "  publisher = {Patchouli Press},\n" +
                "  title = {Updated by agent},\n" +
                "  year = {2024}\n}"));

        put.IsSuccess.Should().BeTrue($"error: {put.Error?.Code} {put.Error?.Detail}");
        put.Envelope!.Message!.Error.Should().BeNull();
        put.Envelope.Message.Warnings.Should().ContainSingle()
            .Which.Should()
            .Be("BIBLATEX_ENTRY_KEY_IGNORED: content entry 1 key was ignored; target identity comes from uri.");
        McpCommandResult<McpFetchMeta, McpFetchResult> after = await _library.Commands.FetchAsync(
            new McpFetchRequest([uri], null, null));
        string content = after.Envelope!.Entries.Single().Content!;
        RegexKey(content).Should().Be(expectedKey);
        content.Should().Contain("Updated by agent").And.NotContain(agentSuppliedKey);
    }

    [Fact]
    public async Task Put_invalid_content_fails_with_empty_entries()
    {
        McpCommandResult<McpPutMeta, McpPutResult> result = await _library.Commands.PutAsync(
            new McpPutRequest(McpResourceUris.ItemUri(_library.BookA), "not a bib entry"));
        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be((int)McpErrorCode.InvalidContent);
        result.Envelope!.Entries.Should().BeEmpty();
        ErrorCode(result.Envelope.Message!.Error).Should().Be((int)McpErrorCode.InvalidContent);
    }

    [Fact]
    public async Task Put_read_only_documents_pages_and_evidence_are_permission_denied()
    {
        McpCommandResult<McpPutMeta, McpPutResult> document = await _library.Commands.PutAsync(
            new McpPutRequest(McpResourceUris.DocumentUri(_library.DocumentA), "x"));
        document.Error!.Code.Should().Be((int)McpErrorCode.PermissionDenied);

        McpCommandResult<McpPutMeta, McpPutResult> page = await _library.Commands.PutAsync(
            new McpPutRequest(McpResourceUris.PageUri(_library.DocumentA, 1), "x"));
        page.Error!.Code.Should().Be((int)McpErrorCode.PermissionDenied);

        McpCommandResult<McpPutMeta, McpPutResult> evidence = await _library.Commands.PutAsync(
            new McpPutRequest(
                $"patchouli://texts/{_library.DocumentA}/page-1.md?rev=20000000-0000-0000-0000-000000000001&box=30000000-0000-0000-0000-000000000001",
                "x"));
        evidence.Error!.Code.Should().Be((int)McpErrorCode.PermissionDenied);
    }

    [Fact]
    public async Task Put_general_item_round_trips_as_misc_and_allows_explicit_type_refinement()
    {
        McpCommandResult<McpFetchMeta, McpFetchResult> fetched = await _library.Commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.ItemUri(_library.GeneralItem)], null, null));
        string key = RegexKey(fetched.Envelope!.Entries.Single().Content!);

        McpCommandResult<McpPutMeta, McpPutResult> roundTrip = await _library.Commands.PutAsync(
            new McpPutRequest(McpResourceUris.ItemUri(_library.GeneralItem),
                $"@misc{{{key},\n  title = {{General Note, Updated}}\n}}"));
        roundTrip.IsSuccess.Should().BeTrue($"error: {roundTrip.Error?.Code} {roundTrip.Error?.Detail}");
        (await _library.Api.GetItemMetadataAsync(_library.GeneralItem)).Value.ItemType.Should().Be("general");

        McpCommandResult<McpPutMeta, McpPutResult> refinement = await _library.Commands.PutAsync(
            new McpPutRequest(McpResourceUris.ItemUri(_library.GeneralItem),
                $"@book{{{key},\n" +
                "  author = {Doe, Jane},\n" +
                "  title = {General Note, Classified},\n" +
                "  publisher = {Example Press},\n" +
                "  year = {2024}\n" +
                "}"));
        refinement.IsSuccess.Should().BeTrue($"error: {refinement.Error?.Code} {refinement.Error?.Detail}");
        (await _library.Api.GetItemMetadataAsync(_library.GeneralItem)).Value.ItemType.Should().Be("book");
    }

    [Fact]
    public async Task Put_replaces_a_csl_style()
    {
        string updated =
            """
            <style xmlns="http://purl.org/net/xbiblio/csl" class="in-text" version="1.0">
              <info><title>APA 7th</title><id>apa</id></info>
              <citation><layout><text variable="title" suffix=" (updated)"/></layout></citation>
            </style>
            """;

        McpCommandResult<McpPutMeta, McpPutResult> put = await _library.Commands.PutAsync(
            new McpPutRequest(McpResourceUris.StyleUri("apa"), updated));
        put.IsSuccess.Should().BeTrue($"error: {put.Error?.Code} {put.Error?.Detail}");
        put.Envelope!.Entries.Single().ResourceType.Should().Be("csl_style");
        put.Envelope.Entries.Single().Committed.Should().BeTrue();
    }

    [Fact]
    public async Task Cite_renders_per_reference_results_and_supports_document_page_and_evidence_refs()
    {
        McpCommandResult<McpCiteMeta, McpCitationResult> item = await _library.Commands.CiteAsync(
            new McpCiteRequest([McpResourceUris.ItemUri(_library.BookA)], McpResourceUris.StyleUri("apa"), null, true,
                false));
        item.IsSuccess.Should().BeTrue();
        McpCitationResult itemResult = item.Envelope!.Entries.Single();
        itemResult.ItemUri.Should().Be(McpResourceUris.ItemUri(_library.BookA));
        itemResult.Citation.Should().NotBeNullOrWhiteSpace();
        itemResult.Error.Should().BeNull();
        item.Envelope.Meta.EffectiveStyleUri.Should().Be(McpResourceUris.StyleUri("apa"));
        item.Envelope.Meta.Bibliography.Should().NotBeNullOrWhiteSpace();
        item.Envelope.Meta.RenderFormat.Should().Be("text");

        McpCommandResult<McpCiteMeta, McpCitationResult> document = await _library.Commands.CiteAsync(
            new McpCiteRequest([McpResourceUris.DocumentUri(_library.DocumentA)], McpResourceUris.StyleUri("apa"),
                null, false, false));
        document.IsSuccess.Should().BeTrue();

        McpCommandResult<McpCiteMeta, McpCitationResult> page = await _library.Commands.CiteAsync(
            new McpCiteRequest([McpResourceUris.PageUri(_library.DocumentA, 1)], McpResourceUris.StyleUri("apa"),
                null, false, false));
        page.IsSuccess.Should().BeTrue();

        McpCommandResult<McpFindMeta, object> search = await _library.Commands.FindAsync(
            new McpFindRequest("quorum", "patchouli://texts/", null));
        string evidenceUri = Entry(search.Envelope!.Entries.First()).Uri;
        McpCommandResult<McpCiteMeta, McpCitationResult> evidence = await _library.Commands.CiteAsync(
            new McpCiteRequest([evidenceUri], McpResourceUris.StyleUri("apa"), null, false, false));
        evidence.IsSuccess.Should().BeTrue();
        evidence.Envelope!.Entries.Single().Citation.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Cite_general_item_uses_misc_fallback_with_warning()
    {
        McpCommandResult<McpCiteMeta, McpCitationResult> result = await _library.Commands.CiteAsync(
            new McpCiteRequest([McpResourceUris.ItemUri(_library.GeneralItem)], McpResourceUris.StyleUri("apa"),
                null, false, false));
        result.IsSuccess.Should().BeTrue($"error: {result.Error?.Code} {result.Error?.Detail}");
        result.Envelope!.Message.Should().NotBeNull();
        result.Envelope.Message!.Warnings.Should().Contain(warning => warning.Contains("general_as_misc"));
    }

    [Fact]
    public async Task Cite_missing_ref_and_invalid_style_fail()
    {
        McpCommandResult<McpCiteMeta, McpCitationResult> missing = await _library.Commands.CiteAsync(
            new McpCiteRequest([McpResourceUris.ItemUri(new ItemId(Guid.NewGuid()))],
                McpResourceUris.StyleUri("apa"), null, false, false));
        missing.IsSuccess.Should().BeFalse();
        ErrorCode(missing.Envelope!.Entries.Single().Error).Should().Be((int)McpErrorCode.NotFound);

        McpCommandResult<McpCiteMeta, McpCitationResult> invalidStyle = await _library.Commands.CiteAsync(
            new McpCiteRequest([McpResourceUris.ItemUri(_library.BookA)], "apa", null, false, false));
        invalidStyle.Error!.Code.Should().Be((int)McpErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task Clean_successes_omit_message_across_all_tools()
    {
        McpCommandResult<McpFindMeta, object> find = await _library.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://items/", null));
        find.Envelope!.Message.Should().BeNull();

        McpCommandResult<McpFetchMeta, McpFetchResult> fetch = await _library.Commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.ItemUri(_library.BookA)], null, null));
        fetch.Envelope!.Message.Should().BeNull();

        McpCommandResult<McpCiteMeta, McpCitationResult> cite = await _library.Commands.CiteAsync(
            new McpCiteRequest([McpResourceUris.ItemUri(_library.BookA)], McpResourceUris.StyleUri("apa"), null,
                false, false));
        cite.Envelope!.Message.Should().BeNull();
    }

    private static McpFindEntry Entry(object entry)
    {
        return (McpFindEntry)entry;
    }

    private static int ErrorCode(string? terminalLine)
    {
        McpToolError.TryGetCode(terminalLine, out McpErrorCode code).Should().BeTrue();
        return (int)code;
    }

    private static string RegexKey(string content)
    {
        return System.Text.RegularExpressions.Regex.Match(content, @"@\w+\{([^,]+),").Groups[1].Value;
    }

    private sealed record TestLibrary(
        string DatabasePath,
        ItemId BookA,
        ItemId BookB,
        ItemId GeneralItem,
        DocumentInstanceId DocumentA,
        DocumentInstanceId DocumentB,
        PageId PageA1,
        PageId PageA2,
        PageId PageB1,
        PageId PageB2,
        SqliteConnectionFactory ConnectionFactory,
        IMcpReadApi Api,
        IMcpWriteApi Writes,
        IBiblatexImportService Biblatex,
        McpCommandService Commands)
    {
        public static async Task<TestLibrary> SeedAsync(string databasePath)
        {
            SqliteConnectionFactory db = new(databasePath);
            SystemClock clock = new();
            await new MigrationRunner(db, Path.Combine(AppContext.BaseDirectory, "migrations")).RunAsync();

            LibraryIdentityService library = new(db, clock);
            Result<LibraryMetadata> created = await library.CreateLibraryAsync("Contract test library");
            if (created.IsFailure)
            {
                throw new InvalidOperationException(created.ErrorMessage);
            }

            ItemService items = new(db, library, clock);
            DocumentInstanceService documents = new(db, clock);
            PageService pages = new(db, clock);
            SearchUnitBuilder searchUnits = new(db, clock);
            DocumentTreeService tree = new(db, clock, new MarkdigMarkdownEngine());

            Result<ItemMetadata> bookA = await items.CreateItemAsync("book", "Distributed Systems Notes",
                status: "active");
            Result<ItemMetadata> bookB = await items.CreateItemAsync("book", "Evidence Methods Handbook",
                status: "active");
            Result<ItemMetadata> general = await items.CreateItemAsync("general", "General Note");
            Require(bookA);
            Require(bookB);
            Require(general);

            Result<DocumentInstance> docA = await documents.AttachDocumentInstanceAsync(
                bookA.Value.ItemId, null, DocumentInstanceType.PrimaryScan, "Distributed Systems Notes", true);
            Result<DocumentInstance> docB = await documents.AttachDocumentInstanceAsync(
                bookB.Value.ItemId, null, DocumentInstanceType.PrimaryScan, "Evidence Methods Handbook", true);
            Require(docA);
            Require(docB);

            PageId pageA1 = await CommitPageAsync(db, clock, pages, tree, docA.Value.DocumentInstanceId, 0, "1",
                "Consensus requires a quorum. Replication improves availability, while an append-only log preserves ordering.");
            PageId pageA2 = await CommitPageAsync(db, clock, pages, tree, docA.Value.DocumentInstanceId, 1, "2",
                "A quorum of replicas must agree before the log advances.");
            PageId pageB1 = await CommitPageAsync(db, clock, pages, tree, docB.Value.DocumentInstanceId, 0, "1",
                "Pinned evidence identifies a document, page, and committed text revision.");
            PageId pageB2 = await CommitPageAsync(db, clock, pages, tree, docB.Value.DocumentInstanceId, 1, "2",
                "A citation should preserve that provenance.");

            Require(await searchUnits.RebuildForDocumentInstanceAsync(docA.Value.DocumentInstanceId));
            Require(await searchUnits.RebuildForDocumentInstanceAsync(docB.Value.DocumentInstanceId));
            Require(await new SearchIndexRebuilder(db, clock).RebuildFtsForLibraryAsync());

            BlockingOperationService blockingOperations = new(db, clock);
            SearchProfileService profiles = new(db, library, clock);
            SqliteSearchService search = new(db, profiles);
            CslStyleStore cslStore = new(db, clock, blockingOperations: blockingOperations);
            CslRenderer cslRenderer = new(items, cslStore, new CslItemMapper());
            McpReadApi api = new(db, search, cslStyleStore: cslStore, cslRenderer: cslRenderer);
            McpWriteApi writes = new(items, new BiblatexHelperClient(), cslStore);
            BiblatexImportService biblatex = new(new BiblatexHelperClient(), items,
                new FileAssetService(db, library, clock), documents);
            IVersionedEvidenceReader evidenceReader = new VersionedEvidenceReader(
                db, library, tree, new DocumentMarkdownCompiler(tree, new MarkdigMarkdownEngine()));
            McpCommandService commands = new(api, writes, biblatex, items, evidenceReader);

            Result<CslStyle> installed = await cslStore.InstallStyleAsync(
                new CslCatalogStyle("apa", "APA 7th", null, "catalog"), ApaStyleXml);
            Require(installed);

            return new TestLibrary(databasePath, bookA.Value.ItemId, bookB.Value.ItemId, general.Value.ItemId,
                docA.Value.DocumentInstanceId, docB.Value.DocumentInstanceId, pageA1, pageA2, pageB1, pageB2,
                db, api, writes, biblatex, commands);
        }

        private static async Task<PageId> CommitPageAsync(
            SqliteConnectionFactory db,
            SystemClock clock,
            PageService pages,
            DocumentTreeService tree,
            DocumentInstanceId documentId,
            int index,
            string label,
            string text)
        {
            Result<Page> page = await pages.CreatePageAsync(
                documentId, index, label, null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "benchmark", null);
            Require(page);

            Result<DocumentTreeRevision> working = await tree.BeginWorkingRevisionAsync(
                documentId, page.Value.PageId,
                [
                    new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null, null,
                        new NormalizedBBox(.1, .1, .8, .1), new TextBoxPayload(text), null)
                ],
                DocumentTreeRevisionSource.Import);
            Require(working);
            Require(await tree.CommitWorkingRevisionAsync(working.Value.TreeRevisionId));
            return page.Value.PageId;
        }

        private static void Require(Result result)
        {
            if (result.IsFailure)
            {
                throw new InvalidOperationException($"{result.ErrorCode}: {result.ErrorMessage}");
            }
        }

        private static void Require<T>(Result<T> result)
        {
            if (result.IsFailure)
            {
                throw new InvalidOperationException($"{result.ErrorCode}: {result.ErrorMessage}");
            }
        }
    }
}
