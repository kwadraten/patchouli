using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Csl;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Bibliography.Biblatex;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Search;
using Patchouli.Mcp;

namespace Patchouli.Tests;

public sealed class CslYamlRoundTripTests
{
    [Theory]
    [InlineData(2019)]
    [InlineData(2026)]
    public async Task Year_only_yaml_renders_without_a_literal(int year)
    {
        await using TestContext context = await CreateContextAsync();
        JsonObject csl = Fields();
        csl["issued"] = JsonNode.Parse($$"""{"date-parts":[[{{year}}]]}""");
        (await context.PutAsync(csl)).IsSuccess.Should().BeTrue();

        Result<CslRenderResult> rendered = await context.RenderAsync();

        rendered.IsSuccess.Should().BeTrue(rendered.ErrorMessage);
        rendered.Value.RenderedText.Should().Contain(year.ToString());
        ItemMetadata saved = (await context.Items.GetItemAsync(context.ItemId)).Value;
        saved.Dates.Should().ContainSingle().Which.Literal.Should().BeNull();
    }

    [Theory]
    [InlineData("issued")]
    [InlineData("accessed")]
    [InlineData("original-date")]
    [InlineData("event-date")]
    [InlineData("submitted")]
    public async Task Every_date_role_preserves_both_range_endpoints_through_fetch_put_and_render(string role)
    {
        await using TestContext context = await CreateContextAsync();
        const string parts = "[[2020,1,2],[2026,3,4]]";
        JsonObject csl = Fields();
        csl[role] = new JsonObject { ["date-parts"] = JsonNode.Parse(parts) };
        (await context.PutAsync(csl)).IsSuccess.Should().BeTrue();
        string fetched = (await context.Read.GetItemYamlAsync(context.ItemId, true)).Value;
        (await context.Write.PutAsync(new McpPutRequest(context.Uri, fetched))).IsSuccess.Should().BeTrue();

        ItemMetadata saved = (await context.Items.GetItemAsync(context.ItemId)).Value;
        saved.Dates.Should().ContainSingle().Which.DatePartsJson.Should().Be(parts);
        JsonNode root = McpYamlCodec.Decode((await context.Read.GetItemYamlAsync(context.ItemId, true)).Value);
        root["csl"]![role]!["date-parts"]!.ToJsonString().Should().Be(parts);
        Result<CslRenderResult> rendered = await context.RenderAsync();
        rendered.IsSuccess.Should().BeTrue(rendered.ErrorMessage);
        rendered.Value.RenderedText.Should().Contain("2020").And.Contain("2026");
    }

    [Theory]
    [InlineData("[2026]")]
    [InlineData("[[2026]]")]
    [InlineData("[\"2026\"]")]
    public async Task Existing_flat_nested_and_string_component_dates_render_and_round_trip(string parts)
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> created = await context.Items.CreateItemAsync(new CreateItemRequest("book",
            "Legacy date", Dates: [new ItemDateInput("issued", parts)]));
        ItemId itemId = created.Value.ItemId;
        Result<CslRenderResult> rendered = await context.Renderer.RenderAsync(new CslRenderRequest([itemId], "probe"));
        rendered.IsSuccess.Should().BeTrue(rendered.ErrorMessage);
        rendered.Value.RenderedText.Should().Contain("2026");

        string yaml = (await context.Read.GetItemYamlAsync(itemId, true)).Value;
        Result<McpPutResponse> put =
            await context.Write.PutAsync(new McpPutRequest(McpResourceUris.ItemUri(itemId), yaml));
        put.IsSuccess.Should().BeTrue(put.ErrorMessage);
        (await context.Items.GetItemAsync(itemId)).Value.Dates.Should().ContainSingle()
            .Which.DatePartsJson.Should().Be("[[2026]]");
    }

    [Theory]
    [InlineData("\"not-an-array\"")]
    [InlineData("null")]
    [InlineData("[[]]")]
    [InlineData("[[2026],[2027],[2028]]")]
    [InlineData("[[2026,1,2,3]]")]
    [InlineData("[[2026,99,42]]")]
    [InlineData("[[2025,2,29]]")]
    [InlineData("[[2026,4,31]]")]
    [InlineData("[[2026,true]]")]
    [InlineData("[[2026.5]]")]
    [InlineData("[2026,[1]]")]
    public async Task Invalid_date_parts_refuse_the_entire_write(string parts)
    {
        await using TestContext context = await CreateContextAsync();
        string original = (await context.Read.GetItemYamlAsync(context.ItemId, true)).Value;
        JsonObject csl = Fields();
        csl["title"] = "Must not be saved";
        csl["issued"] = new JsonObject { ["date-parts"] = JsonNode.Parse(parts) };

        Result<McpPutResponse> put = await context.PutAsync(csl);

        put.IsFailure.Should().BeTrue();
        put.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        (await context.Read.GetItemYamlAsync(context.ItemId, true)).Value.Should().Be(original);
    }

    [Fact]
    public async Task Numeric_csl_fields_and_integer_string_dates_are_accepted_and_rendered()
    {
        await using TestContext context = await CreateContextAsync();
        JsonObject csl = Fields();
        foreach (string field in new[] { "edition", "volume", "issue", "number", "chapter-number", "page" })
        {
            csl[field] = 2;
        }

        csl["issued"] = JsonNode.Parse("""{"date-parts":[["2024","2","29"]],"season":1,"circa":true}""");
        Result<McpPutResponse> put = await context.PutAsync(csl);
        put.IsSuccess.Should().BeTrue(put.ErrorMessage);

        ItemMetadata saved = (await context.Items.GetItemAsync(context.ItemId)).Value;
        new[] { saved.Edition, saved.Volume, saved.Issue, saved.Number, saved.ChapterNumber, saved.Pages }
            .Should().OnlyContain(value => value == "2");
        saved.Dates.Should().ContainSingle().Which.DatePartsJson.Should().Be("[[2024,2,29]]");
        Result<CslRenderResult> rendered = await context.RenderAsync();
        rendered.IsSuccess.Should().BeTrue(rendered.ErrorMessage);
        rendered.Value.RenderedText.Should().Contain("2024");
    }

    [Fact]
    public async Task Custom_dates_names_null_and_nested_json_keep_their_types_and_render()
    {
        await using TestContext context = await CreateContextAsync();
        JsonObject custom = (JsonObject)JsonNode.Parse("""
                                                       {"original-title":null,"title":"Must not override the core title",
                                                        "original-date":{"date-parts":[[1900]]},
                                                        "author":[{"family":"CustomSurname","given":"Jane","non-dropping-particle":"van"}],
                                                        "custom":{"literal":"ordinary data","flags":[true,false],"weight":1.5}}
                                                       """)!;
        Result<McpPutResponse> put = await context.PutAsync(Fields(), custom);
        put.IsSuccess.Should().BeTrue(put.ErrorMessage);

        ItemMetadata saved = (await context.Items.GetItemAsync(context.ItemId)).Value;
        Result<CslMappedItem> mapped = await new CslItemMapper().MapAsync(saved);
        JsonObject engine = (JsonObject)JsonSerializer.SerializeToNode(FsharpCiteprocJsonAdapter.ToItem(mapped.Value))!;
        engine.ContainsKey("original-title").Should().BeFalse();
        engine["original-date"]!["date-parts"]![0]![0]!.GetValue<int>().Should().Be(1900);
        engine["author"]![0]!["non-dropping-particle"]!.GetValue<string>().Should().Be("van");
        engine["custom"]!["flags"]![0]!.GetValue<bool>().Should().BeTrue();
        engine["custom"]!["flags"]![1]!.GetValue<bool>().Should().BeFalse();
        engine["custom"]!["weight"]!.GetValue<double>().Should().Be(1.5);
        engine["custom"]!["literal"]!.GetValue<string>().Should().Be("ordinary data");
        Result<CslRenderResult> rendered = await context.RenderAsync();
        rendered.IsSuccess.Should().BeTrue(rendered.ErrorMessage);
        rendered.Value.RenderedText.Should().Contain("CustomSurname").And.Contain("1900")
            .And.NotContain("null").And.NotContain("Must not override");
    }

    [Theory]
    [InlineData("doi", "DOI")]
    [InlineData("url", "URL")]
    [InlineData("isbn", "ISBN")]
    [InlineData("issn", "ISSN")]
    [InlineData("call-number", "call-number")]
    [InlineData("call_number", "call-number")]
    public async Task Identifier_selection_matches_yaml_and_survives_replacement_order(string scheme, string field)
    {
        await using TestContext context = await CreateContextAsync();
        const string selected = "https://example.test/z-selected";
        const string sibling = "https://example.test/a-sibling";
        await context.Items.AddIdentifierAsync(context.ItemId, scheme, selected, "selected note");
        await context.Items.AddIdentifierAsync(context.ItemId, scheme, sibling, "sibling note");

        for (int pass = 0; pass < 3; pass++)
        {
            JsonNode root = McpYamlCodec.Decode((await context.Read.GetItemYamlAsync(context.ItemId, true)).Value);
            root["csl"]![field]!.GetValue<string>().Should().Be(selected);
            Result<CslMappedItem> mapped = await new CslItemMapper().MapAsync(
                (await context.Items.GetItemAsync(context.ItemId)).Value);
            mapped.Value.Variables[field].Should().Be(selected);
            if (pass == 2)
            {
                root["csl"]![field] = "https://example.test/zz-edited";
            }

            Result<McpPutResponse> put =
                await context.Write.PutAsync(new McpPutRequest(context.Uri, McpYamlCodec.Encode(root)));
            put.IsSuccess.Should().BeTrue(put.ErrorMessage);
        }

        ItemMetadata saved = (await context.Items.GetItemAsync(context.ItemId)).Value;
        saved.Identifiers.Select(identifier => identifier.Value).Should()
            .Equal("https://example.test/zz-edited", sibling);
        Result<CslMappedItem> edited = await new CslItemMapper().MapAsync(saved);
        edited.Value.Variables[field].Should().Be("https://example.test/zz-edited");
        (await context.RenderAsync()).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Literal_only_dates_accept_old_empty_wrappers_and_return_canonical_yaml()
    {
        await using TestContext context = await CreateContextAsync();
        JsonObject csl = Fields();
        csl["issued"] = JsonNode.Parse("""{"date-parts":[[]],"literal":"2026"}""");
        Result<McpPutResponse> put = await context.PutAsync(csl);
        put.IsSuccess.Should().BeTrue(put.ErrorMessage);
        string yaml = (await context.Read.GetItemYamlAsync(context.ItemId, true)).Value;
        JsonObject issued = (JsonObject)McpYamlCodec.Decode(yaml)["csl"]!["issued"]!;
        issued.ContainsKey("date-parts").Should().BeFalse();
        (await context.Write.PutAsync(new McpPutRequest(context.Uri, yaml))).IsSuccess.Should().BeTrue();
        Result<CslRenderResult> rendered = await context.RenderAsync();
        rendered.IsSuccess.Should().BeTrue(rendered.ErrorMessage);
        rendered.Value.RenderedText.Should().Contain("2026");
    }

    [Fact]
    public async Task Invalid_stored_dates_report_validation_errors_when_reading_or_rendering()
    {
        await using TestContext context = await CreateContextAsync();
        Result<ItemMetadata> created = await context.Items.CreateItemAsync(new CreateItemRequest("book",
            "Invalid legacy date", Dates: [new ItemDateInput("issued", "[[2026,99]]")]));
        Result<string> yaml = await context.Read.GetItemYamlAsync(created.Value.ItemId, true);
        yaml.IsFailure.Should().BeTrue();
        yaml.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        Result<CslRenderResult> rendered = await context.Renderer.RenderAsync(
            new CslRenderRequest([created.Value.ItemId], "probe"));
        rendered.IsFailure.Should().BeTrue();
        rendered.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    private static JsonObject Fields()
    {
        return new JsonObject { ["type"] = "book", ["title"] = "Boundary Probe" };
    }

    private static async Task<TestContext> CreateContextAsync()
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService library = new(database.ConnectionFactory, clock);
        await library.CreateLibraryAsync("CSL YAML Round Trip");
        ItemService items = new(database.ConnectionFactory, library, clock);
        CslStyleStore styles = new(database.ConnectionFactory, clock);
        string dates = string.Concat(new[] { "issued", "accessed", "original-date", "event-date", "submitted" }
            .Select(role => $"<date variable=\"{role}\"><date-part name=\"year\"/></date>"));
        string style = $$"""
                         <style xmlns="http://purl.org/net/xbiblio/csl" class="in-text" version="1.0">
                           <info><title>Boundary Probe</title><id>https://example.test/boundary-probe</id></info>
                           <citation><layout><text variable="title"/></layout></citation>
                           <bibliography><layout><group delimiter=" | ">
                             <text variable="title"/><text variable="original-title"/>
                             <names variable="author"><name/></names>{{dates}}
                             <text variable="DOI"/><text variable="URL"/><text variable="ISBN"/>
                             <text variable="ISSN"/><text variable="call-number"/><text variable="edition"/>
                           </group></layout></bibliography>
                         </style>
                         """;
        (await styles.InstallStyleAsync(new CslCatalogStyle("probe", "Boundary Probe", null, "test"), style))
            .IsSuccess.Should().BeTrue();
        CslRenderer renderer = new(items, styles, new CslItemMapper());
        ItemId itemId = (await items.CreateItemAsync("book", "Boundary Probe")).Value.ItemId;
        McpReadApi read = new(database.ConnectionFactory, new SqliteSearchService(database.ConnectionFactory));
        return new TestContext(database, items, read, new McpWriteApi(items, new BiblatexHelperClient(), styles),
            renderer, itemId);
    }

    private sealed record TestContext(
        TemporarySqliteDatabase Database,
        ItemService Items,
        McpReadApi Read,
        McpWriteApi Write,
        CslRenderer Renderer,
        ItemId ItemId) : IAsyncDisposable
    {
        public string Uri => McpResourceUris.ItemUri(ItemId);

        public Task<Result<McpPutResponse>> PutAsync(JsonObject csl, JsonObject? custom = null)
        {
            JsonObject root = new() { ["profile"] = "patchouli-item-1", ["csl"] = csl };
            if (custom is not null)
            {
                root["extensions"] = new JsonObject
                {
                    ["patchouli"] = new JsonObject { ["custom_fields"] = custom }
                };
            }

            return Write.PutAsync(new McpPutRequest(Uri, McpYamlCodec.Encode(root)));
        }

        public Task<Result<CslRenderResult>> RenderAsync()
        {
            return Renderer.RenderAsync(new CslRenderRequest([ItemId], "probe"));
        }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }
}
