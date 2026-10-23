using System.Text.Json.Nodes;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.CitationInterop;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Host.CitationInterop;
using Patchouli.Infrastructure.Bibliography.Biblatex;

namespace Patchouli.Tests.CitationInterop;

/// <summary>
/// Independent verification of the TexPile JSON-RPC subset and the service
/// beneath it (issue #14 §7, checklist item 6): the 100-result cap as a cap
/// on RESULTS (not on pre-filter rows), deterministic ordering, projection
/// shapes for awkward-but-legal items, all-or-nothing export, and the
/// classic-BibTeX output read back through the real Rust helper parser.
/// </summary>
public sealed class CaywIndependentRpcTests
{
    private static JsonObject Parse(string json)
    {
        return JsonNode.Parse(json)!.AsObject();
    }

    private static async Task SeedCapScenarioAsync(CaywIndependentTestContext context)
    {
        // 105 citable books, then 3 general items NEWER than all of them, all
        // matching the same title token. The result cap (100) applies to
        // returned results; non-citable rows must not consume cap slots
        // (plan §7: filtering happens in the database layer, "避免先取 100
        // 条再过滤").
        DateTimeOffset start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        for (int i = 0; i < 105; i++)
        {
            await context.CreateItemAtAsync(start.AddMinutes(i),
                new CreateItemRequest("book", $"CapProbe Book {i:000}"));
        }

        for (int i = 0; i < 3; i++)
        {
            await context.CreateItemAtAsync(start.AddMinutes(200 + i),
                new CreateItemRequest("general", $"CapProbe Note {i}"));
        }
    }

    [Fact]
    public async Task Search_empty_query_cap_counts_results_not_prefilter_rows()
    {
        CaywIndependentTestContext context = await CaywIndependentTestContext.CreateAsync();
        await using (context)
        {
            await SeedCapScenarioAsync(context);

            Result<IReadOnlyList<CitationItemProjection>> results = await context.Interop.SearchAsync("");

            results.IsSuccess.Should().BeTrue(results.ErrorMessage);
            results.Value.Should().HaveCount(CaywCompatibility.MaxSearchResults,
                "105 citable items exist, so the search must fill the 100-result cap with citable items");
            results.Value.Should().OnlyContain(p => p.CslVariables["type"] as string != "general");
        }
    }

    [Fact]
    public async Task Search_matching_query_cap_counts_results_not_prefilter_rows()
    {
        CaywIndependentTestContext context = await CaywIndependentTestContext.CreateAsync();
        await using (context)
        {
            await SeedCapScenarioAsync(context);

            Result<IReadOnlyList<CitationItemProjection>> results =
                await context.Interop.SearchAsync("CapProbe");

            results.IsSuccess.Should().BeTrue(results.ErrorMessage);
            results.Value.Should().HaveCount(CaywCompatibility.MaxSearchResults,
                "105 citable items match, so the search must fill the 100-result cap with citable items");
        }
    }

    [Fact]
    public async Task Search_empty_query_order_is_newest_first_and_stable()
    {
        CaywIndependentTestContext context = await CaywIndependentTestContext.CreateAsync();
        await using (context)
        {
            DateTimeOffset start = DateTimeOffset.Parse("2026-03-01T00:00:00Z");
            List<ItemId> created = new();
            for (int i = 0; i < 5; i++)
            {
                ItemMetadata item = await context.CreateItemAtAsync(start.AddMinutes(i),
                    new CreateItemRequest("book", $"OrderProbe {i}"));
                created.Add(item.ItemId);
            }

            Result<IReadOnlyList<CitationItemProjection>> first = await context.Interop.SearchAsync("");
            Result<IReadOnlyList<CitationItemProjection>> second = await context.Interop.SearchAsync("");

            first.IsSuccess.Should().BeTrue(first.ErrorMessage);
            first.Value.Select(p => p.ItemId).Should().Equal(created.AsEnumerable().Reverse(),
                "empty-query search returns the most recent items first");
            second.Value.Select(p => p.ItemId).Should().Equal(first.Value.Select(p => p.ItemId),
                "the order must be deterministic across identical calls");
        }
    }

    [Fact]
    public async Task Search_projections_tolerate_literal_missing_and_partial_metadata()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            await context.CreateItemAsync(new CreateItemRequest(
                "book", "Literal Author Book",
                Creators: [new ItemCreatorInput(ItemCreatorRoles.Author, Literal: "World Health Organization")]));
            await context.CreateItemAsync(new CreateItemRequest("book", "Anonymous Pamphlet"));
            await context.CreateItemAsync(new CreateItemRequest(
                "book", "Partial Date Book",
                Dates: [new ItemDateInput(ItemDateRoles.Issued, "[[2020]]")]));
            JsonRpcHandler rpc = new(context.Interop);

            JsonObject literal = Parse(await rpc.HandleAsync(
                """{"jsonrpc":"2.0","method":"item.search","params":["Literal Author"]}"""));
            JsonObject literalItem = literal["result"]!.AsArray().Single()!.AsObject();
            literalItem["author"]!.AsArray().Single()!.AsObject()["literal"]!.GetValue<string>()
                .Should().Be("World Health Organization");
            literalItem["citekey"]!.GetValue<string>().Should().Be(literalItem["citation-key"]!.GetValue<string>());
            literalItem["id"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();

            JsonObject anonymous = Parse(await rpc.HandleAsync(
                """{"jsonrpc":"2.0","method":"item.search","params":["Anonymous Pamphlet"]}"""));
            JsonObject anonymousItem = anonymous["result"]!.AsArray().Single()!.AsObject();
            anonymousItem["title"]!.GetValue<string>().Should().Be("Anonymous Pamphlet");
            if (anonymousItem.TryGetPropertyValue("author", out JsonNode? author))
            {
                author!.AsArray().Should().BeEmpty("a missing author must not be fabricated");
            }

            JsonObject partial = Parse(await rpc.HandleAsync(
                """{"jsonrpc":"2.0","method":"item.search","params":["Partial Date"]}"""));
            JsonObject partialItem = partial["result"]!.AsArray().Single()!.AsObject();
            partialItem["issued"]!["date-parts"]!.AsArray().Single()!.AsArray().Single()!.GetValue<int>()
                .Should().Be(2020);
        }
    }

    [Fact]
    public async Task Export_with_a_general_key_fails_the_whole_batch()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            ItemMetadata book = await context.CreateItemAsync(new CreateItemRequest("book", "Good Book"));
            ItemMetadata general = await context.CreateItemAsync(new CreateItemRequest("general", "Loose Note"));
            JsonRpcHandler rpc = new(context.Interop);

            string request = $$"""
                               {"jsonrpc":"2.0","method":"item.export","params":[["{{book.CitationKey}}","{{general.CitationKey}}"],"Better BibTeX"],"id":11}
                               """;
            JsonObject envelope = Parse(await rpc.HandleAsync(request));

            envelope.ContainsKey("result").Should().BeFalse("a partial bib must never be returned");
            envelope["error"]!["code"]!.GetValue<int>().Should().Be(-32000);
            envelope["error"]!["message"]!.GetValue<string>().Should().Contain("not_citable");
            envelope["id"]!.GetValue<int>().Should().Be(11);
        }
    }

    [Fact]
    public async Task Export_classic_bibtex_follows_request_order()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            ItemMetadata first = await context.CreateItemAsync(new CreateItemRequest("book", "Alpha Created First"));
            ItemMetadata second = await context.CreateItemAsync(new CreateItemRequest("book", "Beta Created Second"));

            Result<string> exported = await context.Interop.ExportAsync(
                [second.CitationKey, first.CitationKey], CaywCompatibility.TranslatorBetterBibtex);

            exported.IsSuccess.Should().BeTrue(exported.ErrorMessage);
            int secondIndex = exported.Value.IndexOf($"{{{second.CitationKey},", StringComparison.Ordinal);
            int firstIndex = exported.Value.IndexOf($"{{{first.CitationKey},", StringComparison.Ordinal);
            secondIndex.Should().BeGreaterThanOrEqualTo(0);
            firstIndex.Should().BeGreaterThan(secondIndex, "export order follows the request, not creation order");
        }
    }

    [Fact]
    public async Task Export_classic_bibtex_roundtrips_through_the_real_helper_parser()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            ItemMetadata article = await context.CreateItemAsync(new CreateItemRequest(
                "article-journal", "Roundtrip Article",
                PublicationTitle: "Journal of Testing",
                Pages: "33-41",
                Dates: [new ItemDateInput(ItemDateRoles.Issued, "[[2020,5]]")],
                Creators: [new ItemCreatorInput(ItemCreatorRoles.Author, "Doe", "John")]));

            Result<string> exported = await context.Interop.ExportAsync(
                [article.CitationKey], CaywCompatibility.TranslatorBetterBibtex);
            exported.IsSuccess.Should().BeTrue(exported.ErrorMessage);
            exported.Value.Should().Contain("@article{");
            exported.Value.Should().Contain("journal = {Journal of Testing}");
            exported.Value.Should().Contain("year = {2020}");
            exported.Value.Should().NotContain("journaltitle");
            exported.Value.Should().NotContain("date = {");

            // Independent read-back: the real Rust helper parser (not the
            // exporter's own code) must be able to parse the classic output.
            // The binary ships in the repo's tools/ build tree; when it is
            // absent the structural assertions above still stand.
            string? helper = FindHelperBinary();
            if (helper is not null)
            {
                BiblatexHelperClient client = new(helper);
                Result<IReadOnlyList<BiblatexEntryDto>> reparsed = await client.ParseAsync(exported.Value);
                reparsed.IsSuccess.Should().BeTrue(reparsed.ErrorMessage);
                BiblatexEntryDto entry = reparsed.Value.Should().ContainSingle().Subject;
                entry.EntryType.Should().Be("article");
                entry.Key.Should().Be(article.CitationKey);
                entry.Fields["journal"].Should().Be("Journal of Testing");
                entry.Fields["year"].Should().Be("2020");
                entry.Fields.Should().NotContainKey("journaltitle");
                entry.Fields.Should().NotContainKey("date");
            }
        }
    }

    [Fact]
    public async Task Rpc_parameter_shape_errors_have_stable_codes()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            JsonRpcHandler rpc = new(context.Interop);

            JsonObject objectParams = Parse(await rpc.HandleAsync(
                """{"jsonrpc":"2.0","method":"item.search","params":{"q":"x"},"id":1}"""));
            objectParams["error"]!["code"]!.GetValue<int>().Should().Be(-32602);

            JsonObject numericQuery = Parse(await rpc.HandleAsync(
                """{"jsonrpc":"2.0","method":"item.search","params":[42],"id":2}"""));
            numericQuery["error"]!["code"]!.GetValue<int>().Should().Be(-32602);

            JsonObject batchRoot = Parse(await rpc.HandleAsync(
                """[{"jsonrpc":"2.0","method":"item.search","params":[""],"id":1}]"""));
            batchRoot["error"]!["code"]!.GetValue<int>().Should().Be(-32600);

            JsonObject noMethod = Parse(await rpc.HandleAsync(
                """{"jsonrpc":"2.0","params":[],"id":3}"""));
            noMethod["error"]!["code"]!.GetValue<int>().Should().Be(-32600);

            JsonObject exportMissingTranslator = Parse(await rpc.HandleAsync(
                """{"jsonrpc":"2.0","method":"item.export","params":[["k"]],"id":4}"""));
            exportMissingTranslator["error"]!["code"]!.GetValue<int>().Should().Be(-32602);

            JsonObject exportNumericTranslator = Parse(await rpc.HandleAsync(
                """{"jsonrpc":"2.0","method":"item.export","params":[["k"],7],"id":5}"""));
            exportNumericTranslator["error"]!["code"]!.GetValue<int>().Should().Be(-32602);

            string[] manyKeys = Enumerable.Range(0, CaywCompatibility.MaxExportKeys + 1)
                .Select(i => $"key{i}").ToArray();
            string keyList = string.Join(",", manyKeys.Select(k => $"\"{k}\""));
            JsonObject tooMany = Parse(await rpc.HandleAsync(
                $$"""{"jsonrpc":"2.0","method":"item.export","params":[[{{keyList}}],"Better BibTeX"],"id":6}"""));
            tooMany["error"]!["code"]!.GetValue<int>().Should().Be(-32602);

            // An empty params array for search is the default (empty) query.
            JsonObject emptyParams = Parse(await rpc.HandleAsync(
                """{"jsonrpc":"2.0","method":"item.search","params":[],"id":7}"""));
            emptyParams.ContainsKey("result").Should().BeTrue();
        }
    }

    [Fact]
    public async Task Rpc_search_query_length_boundary_is_exact()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            JsonRpcHandler rpc = new(context.Interop);
            string atLimit = new('x', CaywCompatibility.MaxSearchQueryLength);
            string overLimit = new('x', CaywCompatibility.MaxSearchQueryLength + 1);

            JsonObject ok = Parse(await rpc.HandleAsync(
                $$"""{"jsonrpc":"2.0","method":"item.search","params":["{{atLimit}}"],"id":1}"""));
            ok.ContainsKey("result").Should().BeTrue("a query exactly at the cap is accepted");

            JsonObject over = Parse(await rpc.HandleAsync(
                $$"""{"jsonrpc":"2.0","method":"item.search","params":["{{overLimit}}"],"id":2}"""));
            over["error"]!["code"]!.GetValue<int>().Should().Be(-32602);
        }
    }

    [Fact]
    public async Task Pick_entry_cap_is_enforced_before_any_lookup()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            CitationBridgePickEntry[] entries = Enumerable.Range(0, CitationInteropService.MaxPickEntries + 1)
                .Select(_ => new CitationBridgePickEntry(ItemId.New()))
                .ToArray();

            Result<CitationPick> pick = await context.Interop.ValidatePickAsync(entries);

            pick.IsFailure.Should().BeTrue();
            pick.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        }
    }

    private static string? FindHelperBinary()
    {
        string fileName = OperatingSystem.IsWindows() ? "biblatex-helper.exe" : "biblatex-helper";
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Patchouli.sln")))
            {
                string candidate = Path.Combine(
                    current.FullName, "tools", "biblatex-helper", "target", "release", fileName);
                return File.Exists(candidate) ? candidate : null;
            }

            current = current.Parent;
        }

        return null;
    }
}
