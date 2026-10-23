using System.Text.Json.Nodes;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Host.CitationInterop;

namespace Patchouli.Tests.CitationInterop;

/// <summary>TexPile JSON-RPC subset behavior (issue #14 §7/§9).</summary>
public sealed class JsonRpcHandlerTests
{
    private static async Task<(JsonRpcHandler Handler, InteropTestContext Context, ItemMetadata Book)>
        CreateAsync()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        ItemMetadata book = await context.CreateItemAsync(new CreateItemRequest(
            "book", "Pride and Prejudice",
            Dates: [new ItemDateInput(ItemDateRoles.Issued, "[[1813]]")],
            Creators: [new ItemCreatorInput(ItemCreatorRoles.Author, "Austen", "Jane")]));
        return (new JsonRpcHandler(context.Interop), context, book);
    }

    private static JsonObject Parse(string json)
    {
        return JsonNode.Parse(json)!.AsObject();
    }

    [Fact]
    public async Task Search_without_id_still_returns_a_result_envelope()
    {
        (JsonRpcHandler handler, InteropTestContext context, ItemMetadata book) = await CreateAsync();
        await using (context)
        {
            // TexPile omits the id member entirely.
            string response = await handler.HandleAsync(
                """{"jsonrpc":"2.0","method":"item.search","params":["Pride"]}""");

            JsonObject envelope = Parse(response);
            envelope["jsonrpc"]!.GetValue<string>().Should().Be("2.0");
            envelope.ContainsKey("id").Should().BeTrue();
            envelope["id"].Should().BeNull();
            JsonArray results = envelope["result"]!.AsArray();
            JsonObject item = results.Single()!.AsObject();
            item["citekey"]!.GetValue<string>().Should().Be(book.CitationKey);
            item["citation-key"]!.GetValue<string>().Should().Be(book.CitationKey);
            item["title"]!.GetValue<string>().Should().Be("Pride and Prejudice");
        }
    }

    [Fact]
    public async Task Id_is_echoed_back_verbatim()
    {
        (JsonRpcHandler handler, InteropTestContext context, _) = await CreateAsync();
        await using (context)
        {
            string response = await handler.HandleAsync(
                """{"jsonrpc":"2.0","method":"item.search","params":[""],"id":"req-42"}""");
            Parse(response)["id"]!.GetValue<string>().Should().Be("req-42");

            string numeric = await handler.HandleAsync(
                """{"jsonrpc":"2.0","method":"item.search","params":[""],"id":7}""");
            Parse(numeric)["id"]!.GetValue<int>().Should().Be(7);
        }
    }

    [Fact]
    public async Task Export_round_trip_by_citekey()
    {
        (JsonRpcHandler handler, InteropTestContext context, ItemMetadata book) = await CreateAsync();
        await using (context)
        {
            string request = $$"""
                               {"jsonrpc":"2.0","method":"item.export","params":[["{{book.CitationKey}}"],"Better BibTeX"]}
                               """;
            JsonObject envelope = Parse(await handler.HandleAsync(request));

            envelope.ContainsKey("error").Should().BeFalse();
            envelope["result"]!.GetValue<string>().Should().Contain("@book{").And.Contain("1813");
        }
    }

    [Fact]
    public async Task Export_with_unknown_key_is_a_domain_error_not_partial_output()
    {
        (JsonRpcHandler handler, InteropTestContext context, _) = await CreateAsync();
        await using (context)
        {
            JsonObject envelope = Parse(await handler.HandleAsync(
                """{"jsonrpc":"2.0","method":"item.export","params":[["nope"],"Better BibLaTeX"],"id":1}"""));

            envelope["error"]!["code"]!.GetValue<int>().Should().Be(-32000);
            envelope["error"]!["message"]!.GetValue<string>().Should().Contain("cayw_key_not_found");
            envelope["id"]!.GetValue<int>().Should().Be(1);
        }
    }

    [Fact]
    public async Task Parse_error_and_unknown_method_and_bad_params_have_stable_codes()
    {
        (JsonRpcHandler handler, InteropTestContext context, _) = await CreateAsync();
        await using (context)
        {
            JsonObject parseError = Parse(await handler.HandleAsync("{not json"));
            parseError["error"]!["code"]!.GetValue<int>().Should().Be(-32700);

            JsonObject unknown = Parse(await handler.HandleAsync(
                """{"jsonrpc":"2.0","method":"item.bulkExport","params":[],"id":3}"""));
            unknown["error"]!["code"]!.GetValue<int>().Should().Be(-32601);

            JsonObject badParams = Parse(await handler.HandleAsync(
                """{"jsonrpc":"2.0","method":"item.export","params":["not-an-array"],"id":4}"""));
            badParams["error"]!["code"]!.GetValue<int>().Should().Be(-32602);

            JsonObject conditionSearch = Parse(await handler.HandleAsync(
                """{"jsonrpc":"2.0","method":"item.search","params":[[{"field":"title","value":"x"}]],"id":5}"""));
            conditionSearch["error"]!["code"]!.GetValue<int>().Should().Be(-32602);
            conditionSearch["error"]!["message"]!.GetValue<string>().Should().Contain("not supported");
        }
    }

    [Fact]
    public async Task Search_query_over_the_limit_is_rejected()
    {
        (JsonRpcHandler handler, InteropTestContext context, _) = await CreateAsync();
        await using (context)
        {
            string query = new('x', 600);
            JsonObject envelope = Parse(await handler.HandleAsync(
                $$"""{"jsonrpc":"2.0","method":"item.search","params":["{{query}}"],"id":9}"""));
            envelope["error"]!["code"]!.GetValue<int>().Should().Be(-32602);
        }
    }
}
