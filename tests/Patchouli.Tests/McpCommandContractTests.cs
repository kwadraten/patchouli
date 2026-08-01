using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
using Patchouli.Infrastructure.Evidence;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Operations;
using Patchouli.Infrastructure.Search;
using Patchouli.Mcp;
using Patchouli.McpServer;
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
    public async Task Find_browses_items_then_documents_then_styles_then_evidence()
    {
        McpCommandResult<McpFindResponse> result = await _library.Commands.FindAsync(
            new McpFindRequest(null, null, null, false, false));

        result.IsSuccess.Should().BeTrue();
        McpFindResponse data = result.Envelope!.Data;
        data.Warnings.Should().BeEmpty();
        data.Results.Select(row => row.Kind).Should().Equal(
            "item", "item", "item", "document", "document", "style");
        data.Results.Should().Contain(row => row.Uri == McpResourceUris.ItemUri(_library.BookA));
        data.Results.Should().Contain(row => row.Uri == McpResourceUris.ItemUri(_library.BookB));
        data.Results.Should().Contain(row => row.Uri == McpResourceUris.ItemUri(_library.GeneralItem));
        data.Results.Should().Contain(row => row.Uri == McpResourceUris.DocumentUri(_library.DocumentA));
        data.Results.Should().Contain(row => row.Uri == McpResourceUris.DocumentUri(_library.DocumentB));
        data.Results.Should().Contain(row => row.Uri == McpResourceUris.StyleUri("apa"));
        McpFindResultRow item = data.Results.Should().ContainSingle(row => row.Kind == "item" &&
                                                                           row.Uri == McpResourceUris.ItemUri(
                                                                               _library.BookA)).Subject;
        item.Label.Should().Be("Distributed Systems Notes");
        item.Revision.Should().StartWith("item:");
        item.Writable.Should().BeTrue();
        item.Citable.Should().BeTrue();
        McpFindResultRow general = data.Results.Single(row => row.Uri == McpResourceUris.ItemUri(_library.GeneralItem));
        general.Writable.Should().BeTrue();
        general.Citable.Should().BeTrue();
        data.Results.Where(row => row.Kind == "document").Should().OnlyContain(row => row.Citable && !row.Writable);
        data.Results.Single(row => row.Kind == "style").Writable.Should().BeTrue();
    }

    [Fact]
    public async Task Find_searches_documents_and_returns_evidence_matches()
    {
        McpCommandResult<McpFindResponse> search = await _library.Commands.FindAsync(
            new McpFindRequest("quorum", null, null, false, false));
        McpFindResultRow row = search.Envelope!.Data.Results.First();
        row.Kind.Should().Be("document");
        row.Label.Should().Be("Distributed Systems Notes");
        row.Matches.Should().NotBeEmpty();
        McpFindMatch match = row.Matches!.First();
        match.Evidence.Should().NotBeNullOrWhiteSpace();
        match.Evidence.Should().StartWith("evref:v2:");
        match.Preview.Should().Contain("quorum", "preview should include the matched unit text");
        match.Ordinal.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Find_literal_and_regex_filter_matches()
    {
        McpCommandResult<McpFindResponse> literal = await _library.Commands.FindAsync(
            new McpFindRequest("Quorum", null, null, true, false));
        literal.IsSuccess.Should().BeTrue();
        literal.Envelope!.Data.Results.Should().NotBeEmpty();

        McpCommandResult<McpFindResponse> regex = await _library.Commands.FindAsync(
            new McpFindRequest("quorum|consensus", null, null, false, true));
        regex.IsSuccess.Should().BeTrue();
        regex.Envelope!.Data.Results.Should().NotBeEmpty();
        regex.Envelope!.Data.Results.SelectMany(row => row.Matches ?? [])
            .Should().Contain(match => Regex.IsMatch(match.Preview, "quorum|consensus"));

        McpCommandResult<McpFindResponse> stripped = await _library.Commands.FindAsync(
            new McpFindRequest("\\bquorum\\b", null, null, false, true));
        stripped.IsSuccess.Should().BeTrue();
        stripped.Envelope!.Data.Results.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Find_rejects_literal_plus_regex_and_invalid_regex()
    {
        McpCommandResult<McpFindResponse> both = await _library.Commands.FindAsync(
            new McpFindRequest("quorum", null, null, true, true));
        both.IsSuccess.Should().BeFalse();
        both.Error!.Code.Should().Be((int)McpErrorCode.InvalidArgument);

        McpCommandResult<McpFindResponse> invalidRegex = await _library.Commands.FindAsync(
            new McpFindRequest("(", null, null, false, true));
        invalidRegex.IsSuccess.Should().BeFalse();
        invalidRegex.Error!.Code.Should().Be((int)McpErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task Find_browses_a_single_scope_with_in()
    {
        McpCommandResult<McpFindResponse> result = await _library.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://styles/", null, false, false));

        result.IsSuccess.Should().BeTrue();
        result.Envelope!.Data.Results.Should().OnlyContain(row => row.Kind == "style");
        result.Envelope.Data.Results.Should().ContainSingle();
    }

    [Fact]
    public async Task Find_where_item_type_filters_items()
    {
        McpCommandResult<McpFindResponse> books = await _library.Commands.FindAsync(
            new McpFindRequest(null, null, [new McpWhereClause("item_type", "book")], false, false));
        books.IsSuccess.Should().BeTrue($"error: {books.Error?.Code} {books.Error?.Message}");
        books.Envelope!.Data.Results.Where(row => row.Kind == "item").Should().HaveCount(2);

        McpCommandResult<McpFindResponse> general = await _library.Commands.FindAsync(
            new McpFindRequest(null, null, [new McpWhereClause("item_type", "general")], false, false));
        general.IsSuccess.Should().BeTrue($"error: {general.Error?.Code} {general.Error?.Message}");
        general.Envelope!.Data.Results.Where(row => row.Kind == "item")
            .Should().ContainSingle(row => row.Uri == McpResourceUris.ItemUri(_library.GeneralItem));
    }

    [Fact]
    public async Task Find_where_accepts_multiple_clauses()
    {
        McpCommandResult<McpFindResponse> result = await _library.Commands.FindAsync(
            new McpFindRequest(null, null,
                [new McpWhereClause("item_type", "book"), new McpWhereClause("status", "active")],
                false, false));

        result.IsSuccess.Should().BeTrue($"error: {result.Error?.Code} {result.Error?.Message}");
        result.Envelope!.Data.Results.Where(row => row.Kind == "item").Should().HaveCount(2);
    }

    [Fact]
    public async Task Find_cursor_paginates_browse_results()
    {
        McpCommandResult<McpFindResponse> first = await _library.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://items/", null, false, false, 2));
        first.IsSuccess.Should().BeTrue();
        first.Envelope!.Data.Results.Should().HaveCount(2);
        first.Envelope.Data.Continuation.Should().NotBeNullOrWhiteSpace();

        McpCommandResult<McpFindResponse> second = await _library.Commands.FindAsync(
            new McpFindRequest(null, "patchouli://items/", null, false, false, 2,
                first.Envelope.Data.Continuation));
        second.IsSuccess.Should().BeTrue();
        second.Envelope!.Data.Results.Should().HaveCount(1);
        second.Envelope.Data.Results[0].Uri.Should().NotBe(first.Envelope.Data.Results[0].Uri);
    }

    [Fact]
    public async Task Fetch_item_returns_biblatex_projection_with_revision()
    {
        McpCommandResult<McpFetchResponse> result = await _library.Commands.FetchAsync(
            new McpFetchRequest(McpResourceUris.ItemUri(_library.BookA), null, null, null));

        result.IsSuccess.Should().BeTrue($"error: {result.Error?.Code} {result.Error?.Message}");
        McpFetchResponse response = result.Envelope!.Data;
        response.Kind.Should().Be("item");
        response.Revision.Should().StartWith("item:");
        response.Writable.Should().BeTrue();
        response.Citable.Should().BeTrue();
        McpFetchTextContent content = response.Content.Should().BeOfType<McpFetchTextContent>().Subject;
        content.Text.Should().Contain("@book{");
        content.Text.Should().Contain("Distributed Systems Notes");
    }

    [Fact]
    public async Task Fetch_document_returns_outline_with_page_links()
    {
        McpCommandResult<McpFetchResponse> result = await _library.Commands.FetchAsync(
            new McpFetchRequest(McpResourceUris.DocumentUri(_library.DocumentA), null, null, null));

        result.IsSuccess.Should().BeTrue();
        McpFetchResponse response = result.Envelope!.Data;
        response.Kind.Should().Be("document");
        response.Revision.Should().NotBeNullOrWhiteSpace();
        response.Citable.Should().BeTrue();
        response.ItemUri.Should().Be(McpResourceUris.ItemUri(_library.BookA));
        McpFetchOutlineContent content = response.Content.Should().BeOfType<McpFetchOutlineContent>().Subject;
        content.Title.Should().Be("Distributed Systems Notes");
        content.ItemUri.Should().Be(McpResourceUris.ItemUri(_library.BookA));
        content.Pages.Should().HaveCount(2);
        content.Pages.Should()
            .OnlyContain(page => page.Uri.StartsWith("patchouli://documents/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fetch_page_returns_canonical_markdown()
    {
        McpCommandResult<McpFetchResponse> result = await _library.Commands.FetchAsync(
            new McpFetchRequest(McpResourceUris.PageUri(_library.DocumentA, _library.PageA1), null, null, null));

        result.IsSuccess.Should().BeTrue();
        McpFetchResponse response = result.Envelope!.Data;
        response.Kind.Should().Be("page");
        response.Writable.Should().BeFalse();
        response.Citable.Should().BeTrue();
        response.ItemUri.Should().Be(McpResourceUris.ItemUri(_library.BookA));
        McpFetchPageContent content = response.Content.Should().BeOfType<McpFetchPageContent>().Subject;
        content.Text.Should().Contain("Consensus requires a quorum.");
        content.PageIndex.Should().Be(0);
        content.Uri.Should().Be(McpResourceUris.PageUri(_library.DocumentA, _library.PageA1));
    }

    [Fact]
    public async Task Fetch_document_page_range_returns_selected_pages()
    {
        McpCommandResult<McpFetchResponse> result = await _library.Commands.FetchAsync(
            new McpFetchRequest(McpResourceUris.DocumentUri(_library.DocumentA), "pages:2-2", null, null));

        result.IsSuccess.Should().BeTrue();
        McpFetchPagesContent content = result.Envelope!.Data.Content.Should().BeOfType<McpFetchPagesContent>().Subject;
        content.Pages.Should().ContainSingle();
        content.Pages[0].PageIndex.Should().Be(1);
    }

    [Fact]
    public async Task Fetch_style_returns_csl_xml()
    {
        McpCommandResult<McpFetchResponse> result = await _library.Commands.FetchAsync(
            new McpFetchRequest(McpResourceUris.StyleUri("apa"), null, null, null));

        result.IsSuccess.Should().BeTrue();
        McpFetchResponse response = result.Envelope!.Data;
        response.Kind.Should().Be("style");
        response.Revision.Should().StartWith("style:");
        response.Writable.Should().BeTrue();
        response.Citable.Should().BeFalse();
        McpFetchTextContent content = response.Content.Should().BeOfType<McpFetchTextContent>().Subject;
        content.Text.Should().Contain("<style");
        content.Text.Should().Contain("<id>apa</id>");
    }

    [Fact]
    public async Task Fetch_evidence_returns_record_and_source_links()
    {
        McpCommandResult<McpFindResponse> search = await _library.Commands.FindAsync(
            new McpFindRequest("quorum", null, null, false, false));
        string evidenceRef = search.Envelope!.Data.Results.First().Matches!.First().Evidence!;

        McpCommandResult<McpFetchResponse> result = await _library.Commands.FetchAsync(
            new McpFetchRequest(McpResourceUris.EvidenceUri(evidenceRef), null, null, null));

        result.IsSuccess.Should().BeTrue();
        McpFetchResponse response = result.Envelope!.Data;
        response.Kind.Should().Be("evidence");
        response.Citable.Should().BeTrue();
        response.ItemUri.Should().Be(McpResourceUris.ItemUri(_library.BookA));
        McpFetchEvidenceContent content = response.Content.Should().BeOfType<McpFetchEvidenceContent>().Subject;
        content.DocumentUri.Should().Be(McpResourceUris.DocumentUri(_library.DocumentA));
        content.PageUri.Should().StartWith(McpResourceUris.DocumentUri(_library.DocumentA));
        content.PinnedText.Should().Contain("quorum");
    }

    [Fact]
    public async Task Fetch_missing_resource_returns_not_found()
    {
        McpCommandResult<McpFetchResponse> missingItem = await _library.Commands.FetchAsync(
            new McpFetchRequest(McpResourceUris.ItemUri(new ItemId(Guid.NewGuid())), null, null, null));
        missingItem.Error!.Code.Should().Be((int)McpErrorCode.NotFound);

        McpCommandResult<McpFetchResponse> missingPage = await _library.Commands.FetchAsync(
            new McpFetchRequest(McpResourceUris.PageUri(_library.DocumentA, new PageId(Guid.NewGuid())), null, null,
                null));
        missingPage.Error!.Code.Should().Be((int)McpErrorCode.NotFound);
    }

    [Fact]
    public async Task Fetch_rejects_scopes_and_superfluous_options()
    {
        McpCommandResult<McpFetchResponse> scope = await _library.Commands.FetchAsync(
            new McpFetchRequest("patchouli://", null, null, null));
        scope.Error!.Code.Should().Be((int)McpErrorCode.InvalidArgument);

        McpCommandResult<McpFetchResponse> pageRevision = await _library.Commands.FetchAsync(
            new McpFetchRequest(McpResourceUris.PageUri(_library.DocumentA, _library.PageA1), null, "item:stale",
                null));
        pageRevision.Error!.Code.Should().Be((int)McpErrorCode.NotFound);
    }

    [Fact]
    public async Task Fetch_rejects_invalid_ranges_and_mismatched_page_document()
    {
        McpCommandResult<McpFetchResponse> wrongKind = await _library.Commands.FetchAsync(
            new McpFetchRequest(McpResourceUris.ItemUri(_library.BookA), "pages:1-2", null, null));
        wrongKind.Error!.Code.Should().Be((int)McpErrorCode.InvalidArgument);

        McpCommandResult<McpFetchResponse> malformed = await _library.Commands.FetchAsync(
            new McpFetchRequest(McpResourceUris.PageUri(_library.DocumentA, _library.PageA1), "lines:bad", null,
                null));
        malformed.Error!.Code.Should().Be((int)McpErrorCode.InvalidArgument);

        DocumentInstanceId otherDocument = new(Guid.NewGuid());
        McpCommandResult<McpFetchResponse> wrongDocument = await _library.Commands.FetchAsync(
            new McpFetchRequest(McpResourceUris.PageUri(otherDocument, _library.PageA1), null, null, null));
        wrongDocument.Error!.Code.Should().Be((int)McpErrorCode.NotFound);
    }

    [Theory]
    [InlineData("patchouli://items/00000000-0000-0000-0000-000000000001")]
    [InlineData("patchouli://items/00000000-0000-0000-0000-000000000001.bib/")]
    [InlineData("patchouli://items//00000000-0000-0000-0000-000000000001.bib")]
    [InlineData("patchouli://documents/00000000-0000-0000-0000-000000000001")]
    [InlineData("patchouli://documents/00000000-0000-0000-0000-000000000001//")]
    [InlineData("patchouli://documents/00000000-0000-0000-0000-000000000001/pages/00000000-0000-0000-0000-000000000002")]
    [InlineData("patchouli://styles/apa")]
    [InlineData("patchouli://styles/apa.csl/")]
    public void Resource_uris_require_canonical_shapes(string uri)
    {
        McpResourceUris.Parse(uri).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Fetch_revision_mismatch_returns_not_found()
    {
        McpCommandResult<McpFetchResponse> result = await _library.Commands.FetchAsync(
            new McpFetchRequest(McpResourceUris.ItemUri(_library.BookA), null, "item:1999-01-01T00:00:00.0000000+00:00",
                null));
        result.Error!.Code.Should().Be((int)McpErrorCode.NotFound);
    }

    [Fact]
    public async Task Fetch_oversized_response_returns_partial_content_and_error()
    {
        McpCommandResult<McpFetchResponse> result = await _library.Commands.FetchAsync(
            new McpFetchRequest(McpResourceUris.ItemUri(_library.BookA), null, null, 10));
        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be((int)McpErrorCode.ResponseTruncated);
        result.Envelope.Should().NotBeNull();
        result.Envelope!.Data.Complete.Should().BeFalse();
        result.Envelope.Data.Truncated.Should().BeTrue();
        result.Envelope.Data.NextRange.Should().NotBeNullOrWhiteSpace();
        result.Envelope.Error!.Code.Should().Be((int)McpErrorCode.ResponseTruncated);
    }

    [Fact]
    public async Task Put_replaces_an_item_bib_and_is_revision_gated()
    {
        McpResourceChangedEventArgs? changed = null;
        _library.Writes.ResourceChanged += (_, args) => changed = args;
        McpCommandResult<McpFetchResponse> fetched = await _library.Commands.FetchAsync(
            new McpFetchRequest(McpResourceUris.ItemUri(_library.BookA), null, null, null));
        string currentRevision = fetched.Envelope!.Revision!;
        string key = Regex.Match(
            ((McpFetchTextContent)fetched.Envelope.Data.Content).Text,
            @"@book\{([^,]+),").Groups[1].Value;

        string updated =
            "@book{" + key + ",\n" +
            "  author = {Doe, Jane},\n" +
            "  title = {Distributed Systems Notes, Second Edition},\n" +
            "  publisher = {Example Press},\n" +
            "  year = {2024}\n" +
            "}\n";

        McpCommandResult<McpPutResponse> first = await _library.Commands.PutAsync(
            new McpPutRequest(McpResourceUris.ItemUri(_library.BookA), updated, currentRevision));
        first.IsSuccess.Should().BeTrue();
        first.Envelope!.Revision.Should().NotBe(currentRevision);
        changed.Should().NotBeNull();
        changed!.Kind.Should().Be("item");
        changed.ItemId.Should().Be(_library.BookA);
        changed.Revision.Should().Be(first.Envelope.Revision);

        McpCommandResult<McpPutResponse> stale = await _library.Commands.PutAsync(
            new McpPutRequest(McpResourceUris.ItemUri(_library.BookA), updated, currentRevision));
        stale.IsSuccess.Should().BeFalse();
        stale.Error!.Code.Should().Be((int)McpErrorCode.RevisionConflict);

        McpCommandResult<McpFetchResponse> refreshed = await _library.Commands.FetchAsync(
            new McpFetchRequest(McpResourceUris.ItemUri(_library.BookA), null, null, null));
        ((McpFetchTextContent)refreshed.Envelope!.Data.Content).Text.Should().Contain("Second Edition");
    }

    [Fact]
    public async Task Put_invalid_biblatex_fails_with_invalid_content()
    {
        McpCommandResult<McpPutResponse> result = await _library.Commands.PutAsync(
            new McpPutRequest(McpResourceUris.ItemUri(_library.BookA), "not a bib entry",
                "item:2024-01-01T00:00:00.0000000+00:00"));
        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be((int)McpErrorCode.InvalidContent);
    }

    [Fact]
    public async Task Put_invalid_base_revision_fails_with_invalid_argument()
    {
        McpCommandResult<McpPutResponse> result = await _library.Commands.PutAsync(
            new McpPutRequest(McpResourceUris.ItemUri(_library.BookA), "not a bib entry", "not-a-revision"));
        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be((int)McpErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task Put_read_only_uris_return_permission_denied()
    {
        McpCommandResult<McpPutResponse> document = await _library.Commands.PutAsync(
            new McpPutRequest(McpResourceUris.DocumentUri(_library.DocumentA), "x",
                "item:2024-01-01T00:00:00.0000000+00:00"));
        document.Error!.Code.Should().Be((int)McpErrorCode.PermissionDenied);

        McpCommandResult<McpPutResponse> page = await _library.Commands.PutAsync(
            new McpPutRequest(McpResourceUris.PageUri(_library.DocumentA, _library.PageA1), "x",
                "item:2024-01-01T00:00:00.0000000+00:00"));
        page.Error!.Code.Should().Be((int)McpErrorCode.PermissionDenied);

        McpCommandResult<McpPutResponse> evidence = await _library.Commands.PutAsync(
            new McpPutRequest("patchouli://evidence/evref:v2:any", "x", "item:2024-01-01T00:00:00.0000000+00:00"));
        evidence.Error!.Code.Should().Be((int)McpErrorCode.PermissionDenied);
    }

    [Fact]
    public async Task Put_general_item_round_trips_as_misc_and_allows_explicit_type_refinement()
    {
        McpCommandResult<McpFetchResponse> fetched = await _library.Commands.FetchAsync(
            new McpFetchRequest(McpResourceUris.ItemUri(_library.GeneralItem), null, null, null));
        fetched.IsSuccess.Should().BeTrue();
        string currentRevision = fetched.Envelope!.Revision!;
        string key = Regex.Match(
            ((McpFetchTextContent)fetched.Envelope.Data.Content).Text,
            @"@misc\{([^,]+),").Groups[1].Value;

        McpCommandResult<McpPutResponse> result = await _library.Commands.PutAsync(
            new McpPutRequest(McpResourceUris.ItemUri(_library.GeneralItem),
                $"@misc{{{key},\n  title = {{General Note, Updated}}\n}}",
                currentRevision));
        result.IsSuccess.Should().BeTrue($"error: {result.Error?.Code} {result.Error?.Message}");

        McpCommandResult<McpFetchResponse> refreshed = await _library.Commands.FetchAsync(
            new McpFetchRequest(McpResourceUris.ItemUri(_library.GeneralItem), null, null, null));
        ((McpFetchTextContent)refreshed.Envelope!.Data.Content).Text.Should().Contain("@misc{")
            .And.Contain("General Note, Updated");
        (await _library.Api.GetItemMetadataAsync(_library.GeneralItem)).Value.ItemType.Should().Be("general");

        McpCommandResult<McpPutResponse> refinement = await _library.Commands.PutAsync(
            new McpPutRequest(McpResourceUris.ItemUri(_library.GeneralItem),
                $"@book{{{key},\n" +
                "  author = {Doe, Jane},\n" +
                "  title = {General Note, Classified},\n" +
                "  publisher = {Example Press},\n" +
                "  year = {2024}\n" +
                "}", refreshed.Envelope.Revision!));
        refinement.IsSuccess.Should().BeTrue();

        McpCommandResult<McpFetchResponse> classified = await _library.Commands.FetchAsync(
            new McpFetchRequest(McpResourceUris.ItemUri(_library.GeneralItem), null, null, null));
        ((McpFetchTextContent)classified.Envelope!.Data.Content).Text.Should().Contain("@book{")
            .And.Contain("General Note, Classified");
        (await _library.Api.GetItemMetadataAsync(_library.GeneralItem)).Value.ItemType.Should().Be("book");
    }

    [Fact]
    public async Task Put_style_replaces_csl_and_is_revision_gated()
    {
        McpCommandResult<McpFetchResponse> fetched = await _library.Commands.FetchAsync(
            new McpFetchRequest(McpResourceUris.StyleUri("apa"), null, null, null));
        string currentRevision = fetched.Envelope!.Revision!;

        string updated =
            """
            <style xmlns="http://purl.org/net/xbiblio/csl" class="in-text" version="1.0">
              <info><title>APA 7th</title><id>apa</id></info>
              <citation><layout><text variable="title" suffix=" (updated)"/></layout></citation>
            </style>
            """;

        McpCommandResult<McpPutResponse> first = await _library.Commands.PutAsync(
            new McpPutRequest(McpResourceUris.StyleUri("apa"), updated, currentRevision));
        first.IsSuccess.Should().BeTrue();
        first.Envelope!.Revision.Should().NotBe(currentRevision);

        McpCommandResult<McpPutResponse> stale = await _library.Commands.PutAsync(
            new McpPutRequest(McpResourceUris.StyleUri("apa"), updated, currentRevision));
        stale.Error!.Code.Should().Be((int)McpErrorCode.RevisionConflict);
    }

    [Fact]
    public async Task Cite_renders_bibliography_and_supports_general_document_page_and_evidence_refs()
    {
        McpCommandResult<McpCiteResponse> ok = await _library.Commands.CiteAsync(
            new McpCiteRequest([McpResourceUris.ItemUri(_library.BookA)], McpResourceUris.StyleUri("apa"), null,
                false, false));
        ok.IsSuccess.Should().BeTrue();
        ok.Envelope!.Data.Bibliography.Should().NotBeNullOrWhiteSpace();
        ok.Envelope.Data.Bibliography!.Should().Contain("Distributed Systems Notes");
        ok.Envelope.Data.Html.Should().BeNull();

        McpCommandResult<McpCiteResponse> general = await _library.Commands.CiteAsync(
            new McpCiteRequest([McpResourceUris.ItemUri(_library.GeneralItem)], McpResourceUris.StyleUri("apa"), null,
                false, false));
        general.IsSuccess.Should().BeTrue($"error: {general.Error?.Code} {general.Error?.Message}");
        general.Envelope!.Data.Warnings.Should().Contain(warning => warning.Contains("general_as_misc"));

        McpCommandResult<McpCiteResponse> document = await _library.Commands.CiteAsync(
            new McpCiteRequest([McpResourceUris.DocumentUri(_library.DocumentA)], McpResourceUris.StyleUri("apa"),
                null, false, false));
        document.IsSuccess.Should().BeTrue($"error: {document.Error?.Code} {document.Error?.Message}");
        document.Envelope!.Data.Bibliography.Should().Contain("Distributed Systems Notes");

        McpCommandResult<McpCiteResponse> page = await _library.Commands.CiteAsync(
            new McpCiteRequest([McpResourceUris.PageUri(_library.DocumentA, _library.PageA1)],
                McpResourceUris.StyleUri("apa"), null, false, false));
        page.IsSuccess.Should().BeTrue($"error: {page.Error?.Code} {page.Error?.Message}");

        McpCommandResult<McpFindResponse> search = await _library.Commands.FindAsync(
            new McpFindRequest("quorum", null, null, false, false));
        string evidenceRef = search.Envelope!.Data.Results.First().Matches!.First().Evidence!;
        McpCommandResult<McpCiteResponse> evidence = await _library.Commands.CiteAsync(
            new McpCiteRequest([McpResourceUris.EvidenceUri(evidenceRef)], McpResourceUris.StyleUri("apa"), null,
                false, false));
        evidence.IsSuccess.Should().BeTrue();
        evidence.Envelope!.Data.Bibliography.Should().Contain("Distributed Systems Notes");

        McpCommandResult<McpCiteResponse> missingStyle = await _library.Commands.CiteAsync(
            new McpCiteRequest([McpResourceUris.ItemUri(_library.BookA)], McpResourceUris.StyleUri("missing"), null,
                false, false));
        missingStyle.Error!.Code.Should().Be((int)McpErrorCode.NotFound);

        McpCommandResult<McpCiteResponse> defaultStyle = await _library.Commands.CiteAsync(
            new McpCiteRequest([McpResourceUris.ItemUri(_library.BookA)], null, null, false, false));
        defaultStyle.IsSuccess.Should().BeTrue();

        McpCommandResult<McpCiteResponse> invalidStyle = await _library.Commands.CiteAsync(
            new McpCiteRequest([McpResourceUris.ItemUri(_library.BookA)], "apa", null, false, false));
        invalidStyle.Error!.Code.Should().Be((int)McpErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task Cite_bibliography_only_suppresses_html_even_when_requested()
    {
        McpCommandResult<McpCiteResponse> result = await _library.Commands.CiteAsync(
            new McpCiteRequest([McpResourceUris.ItemUri(_library.BookA)], McpResourceUris.StyleUri("apa"), null,
                true, true));
        result.IsSuccess.Should().BeTrue();
        result.Envelope!.Data.Bibliography.Should().NotBeNullOrWhiteSpace();
        result.Envelope.Data.Html.Should().BeNull();
    }

    [Fact]
    public void Error_mappings_cover_every_app_error_code()
    {
        string[] codes = typeof(AppErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(string))
            .Select(field => (string)field.GetValue(null)!)
            .ToArray();

        codes.Should().NotBeEmpty();

        foreach (string code in codes)
        {
            McpErrorCode read = McpErrorMappings.ToReadError(code);
            McpErrorCode expectedRead = code switch
            {
                AppErrorCodes.InvalidArgument => McpErrorCode.InvalidArgument,
                AppErrorCodes.NotFound => McpErrorCode.NotFound,
                AppErrorCodes.ValidationFailed => McpErrorCode.InvalidArgument,
                AppErrorCodes.Conflict => McpErrorCode.RevisionConflict,
                AppErrorCodes.BiblatexGeneralExportForbidden => McpErrorCode.PermissionDenied,
                AppErrorCodes.NotCitable => McpErrorCode.NotCitable,
                _ => McpErrorCode.Unavailable
            };
            read.Should().Be(expectedRead, $"read mapping for {code}");

            McpErrorCode write = McpErrorMappings.ToWriteError(code);
            McpErrorCode expectedWrite = code switch
            {
                AppErrorCodes.InvalidArgument => McpErrorCode.InvalidArgument,
                AppErrorCodes.NotFound => McpErrorCode.NotFound,
                AppErrorCodes.Conflict => McpErrorCode.RevisionConflict,
                AppErrorCodes.NotCitable => McpErrorCode.NotCitable,
                AppErrorCodes.ValidationFailed => McpErrorCode.InvalidContent,
                AppErrorCodes.UnsupportedOperation => McpErrorCode.PermissionDenied,
                AppErrorCodes.BiblatexParseFailed or AppErrorCodes.BiblatexWriteFailed
                    or AppErrorCodes.BiblatexHelperFailed or AppErrorCodes.BiblatexVerifyFailed
                    or AppErrorCodes.BiblatexMissingTitle or AppErrorCodes.BiblatexEncodingError =>
                    McpErrorCode.InvalidContent,
                _ => McpErrorCode.Unavailable
            };
            write.Should().Be(expectedWrite, $"write mapping for {code}");
        }
    }

    [Fact]
    public async Task Cli_json_output_matches_mcp_envelope_for_find()
    {
        string cliDll = Path.Combine(AppContext.BaseDirectory, "Patchouli.Cli.dll");
        File.Exists(cliDll).Should().BeTrue("Patchouli.Cli must be referenced by the test project");

        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(cliDll);
        startInfo.ArgumentList.Add("--json");
        startInfo.ArgumentList.Add("--db");
        startInfo.ArgumentList.Add(_databasePath);
        startInfo.ArgumentList.Add("find");

        using Process process = Process.Start(startInfo)!;
        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        process.ExitCode.Should().Be(0, $"CLI exited {process.ExitCode}; stderr: {stderr}");

        McpProtocolHandler handler = new(_library.Api, _library.Writes, _library.Biblatex, _library.ConnectionFactory);
        string rpc =
            """
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"patchouli.find","arguments":{}}}
            """;
        string callResponse = await handler.HandleAsync(rpc);
        JsonNode? callJson = JsonNode.Parse(callResponse);
        string envelopeText = callJson!["result"]!["content"]![0]!["text"]!.GetValue<string>();
        JsonNode? mcpEnvelope = JsonNode.Parse(envelopeText);
        JsonNode? cliEnvelope = JsonNode.Parse(stdout);

        cliEnvelope!["data"]!.ToJsonString().Should().Be(mcpEnvelope!["data"]!.ToJsonString(),
            "the CLI and MCP must share the same find response schema");
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
            EvidenceReferenceService evidence = new(db, clock);
            CslStyleStore cslStore = new(db, clock, blockingOperations: blockingOperations);
            CslRenderer cslRenderer = new(items, cslStore, new CslItemMapper());
            McpReadApi api = new(db, search, evidence, cslStyleStore: cslStore, cslRenderer: cslRenderer);
            McpWriteApi writes = new(items, new BiblatexHelperClient(), cslStore);
            BiblatexImportService biblatex = new(new BiblatexHelperClient(), items,
                new FileAssetService(db, library, clock), documents);
            McpCommandService commands = new(api, writes, biblatex);

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

            Result<DocumentTreeRevision> staging = await tree.StagePageAsync(
                documentId, page.Value.PageId,
                [
                    new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null, null,
                        new NormalizedBBox(.1, .1, .8, .1), new TextBoxPayload(text), null)
                ],
                DocumentTreeRevisionSource.Import);
            Require(staging);
            Require(await tree.AdoptStagingRevisionAsync(staging.Value.TreeRevisionId));
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
