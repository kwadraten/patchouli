using System.Text.Json.Nodes;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.CitationInterop;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Host.CitationInterop;
using static Patchouli.Tests.CitationInterop.CaywIndependentQueries;

namespace Patchouli.Tests.CitationInterop;

/// <summary>
/// Independent golden checks for the CAYW string formatters and the
/// formatted-CSL routing (issue #14 §3/§6, checklist item 2), concentrating
/// on combinations and hostile keys that single-format happy-path fixtures
/// tend to miss.
/// </summary>
public sealed class CaywIndependentFormatterTests
{
    private static CitationPick Pick(params CitationPickEntry[] entries)
    {
        return new CitationPick(entries);
    }

    private static CitationPickEntry Entry(string key, string? locator = null, bool suppress = false)
    {
        return new CitationPickEntry(ItemId.New(), key, locator, SuppressAuthor: suppress);
    }

    private static Result<string> Format(string format, CitationPick pick, CitationFormatOptions? options = null)
    {
        Result<CitationFormatters.Resolution> resolution = CitationFormatters.Resolve(format);
        resolution.IsSuccess.Should().BeTrue();
        return resolution.Value.Formatter!.Format(
            new CitationFormatRequest(pick, [], options ?? new CitationFormatOptions()));
    }

    [Theory]
    [InlineData("cite2")]
    [InlineData("cite p")]
    [InlineData("cite_p")]
    [InlineData("cite{x}")]
    [InlineData("\\cite")]
    public void Natbib_command_with_non_letters_is_rejected(string command)
    {
        Result<string> result = Format("natbib", Pick(Entry("doe2020")),
            new CitationFormatOptions(command));

        result.IsFailure.Should().BeTrue($"command '{command}' must never reach the output");
    }

    [Fact]
    public void Natbib_combined_command_preserves_pick_order()
    {
        Result<string> result = Format("natbib", Pick(Entry("zzz1999"), Entry("aaa2000")));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("\\cite{zzz1999,aaa2000}");
    }

    [Fact]
    public void Biblatex_multi_entry_without_notes_is_one_combined_command()
    {
        Result<string> result = Format("biblatex", Pick(Entry("doe2020"), Entry("smith2021")));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("\\autocite{doe2020,smith2021}");
    }

    [Fact]
    public void Biblatex_single_entry_with_locator_uses_optional_argument()
    {
        Result<string> result = Format("biblatex", Pick(Entry("doe2020", "33")));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("\\autocite[p. 33]{doe2020}");
    }

    [Fact]
    public void Pandoc_brackets_combine_suppress_author_and_locator()
    {
        Result<string> result = Format("pandoc",
            Pick(Entry("doe2020", "33", true)),
            new CitationFormatOptions(Brackets: true));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("[-@doe2020, p. 33]");
    }

    [Fact]
    public void Pandoc_locator_label_override_and_removal_apply()
    {
        Dictionary<string, string> overridden = new() { ["page"] = "S." };
        Result<string> withOverride = Format("pandoc", Pick(Entry("doe2020", "33")),
            new CitationFormatOptions(LocatorLabelOverrides: overridden));
        withOverride.IsSuccess.Should().BeTrue();
        withOverride.Value.Should().Be("[@doe2020, S. 33]");

        Dictionary<string, string> removed = new() { ["page"] = "" };
        Result<string> withRemoval = Format("pandoc", Pick(Entry("doe2020", "33")),
            new CitationFormatOptions(LocatorLabelOverrides: removed));
        withRemoval.IsSuccess.Should().BeTrue();
        withRemoval.Value.Should().Be("[@doe2020, 33]");
    }

    [Fact]
    public void Typst_key_with_slash_is_an_explicit_error_not_an_invalid_label()
    {
        // Typst labels cannot contain '/': the shorthand rejects it, and the
        // functional form must not emit a label Typst cannot parse either —
        // the key has to fail explicitly (plan §3: 有效转义或明确报错).
        Result<string> result = Format("typst", Pick(Entry("doe/2020")));

        result.IsFailure.Should().BeTrue(
            $"a key containing '/' cannot be expressed as a Typst label and must not be emitted verbatim; " +
            $"the formatter actually returned '{(result.IsSuccess ? result.Value : "<failure>")}'");
    }

    [Fact]
    public void Typst_key_with_space_is_an_explicit_error()
    {
        Result<string> result = Format("typst", Pick(Entry("doe 2020", "33")));

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Json_pick_carries_patchouli_identity_and_typed_pick_fields()
    {
        ItemId itemId = ItemId.New();
        CitationItemProjection projection = new(itemId, "doe2020",
            new Dictionary<string, object?>
            {
                ["id"] = itemId.ToString(),
                ["citation-key"] = "doe2020",
                ["type"] = "book",
                ["title"] = "A Title"
            });
        CitationPick pick = Pick(new CitationPickEntry(
            itemId, "doe2020", "33", SuppressAuthor: true));
        Result<CitationFormatters.Resolution> resolution = CitationFormatters.Resolve("json");
        resolution.IsSuccess.Should().BeTrue();

        Result<string> result = resolution.Value.Formatter!.Format(
            new CitationFormatRequest(pick, [projection], new CitationFormatOptions()));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        JsonObject item = JsonNode.Parse(result.Value)!.AsArray().Single()!.AsObject();
        item["id"]!.GetValue<string>().Should().Be(itemId.ToString(),
            "the projection id is the Patchouli item id, not a minted Zotero id");
        item["citekey"]!.GetValue<string>().Should().Be("doe2020");
        item["citation-key"]!.GetValue<string>().Should().Be("doe2020");
        item["locator"]!.GetValue<string>().Should().Be("33");
        item["suppress-author"]!.GetValue<bool>().Should().BeTrue();
        result.Value.Should().NotContain("zotero.org");
        result.Value.Should().NotContain("libraryID");
    }

    private static async Task<(CaywRequestHandler Handler, CountingCitationBridge Bridge, ItemMetadata Book)>
        SelectedSetupAsync(InteropTestContext context)
    {
        ItemMetadata book = await context.CreateItemAsync(new CreateItemRequest(
            "book", "Pride and Prejudice",
            Dates: [new ItemDateInput(ItemDateRoles.Issued, "[[1813]]")],
            Creators: [new ItemCreatorInput(ItemCreatorRoles.Author, "Austen", "Jane")]));
        CountingCitationBridge bridge = new() { Selected = [book.ItemId] };
        return (new CaywRequestHandler(context.Interop, bridge), bridge, book);
    }

    [Fact]
    public async Task Formatted_citation_is_inline_and_formatted_bibliography_is_the_reference_list()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            (CaywRequestHandler handler, _, _) = await SelectedSetupAsync(context);

            CitationHttpResponse citation = await handler.HandleAsync(
                Query(("selected", "1"), ("format", "formatted-citation")), CancellationToken.None);
            citation.StatusCode.Should().Be(200);
            citation.ContentType.Should().StartWith("text/plain");
            citation.Body.Should().Contain("Austen").And.Contain("1813");
            citation.Body.Should().NotContain("Pride and Prejudice",
                "the inline citation layout has no title; the title would mean bibliography output leaked in");

            CitationHttpResponse bibliography = await handler.HandleAsync(
                Query(("selected", "1"), ("format", "formatted-bibliography")), CancellationToken.None);
            bibliography.StatusCode.Should().Be(200);
            bibliography.Body.Should().Contain("Pride and Prejudice");
        }
    }

    [Fact]
    public async Task Formatted_citation_html_flag_switches_content_type_and_body()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            (CaywRequestHandler handler, _, _) = await SelectedSetupAsync(context);

            CitationHttpResponse html = await handler.HandleAsync(
                Query(("selected", "1"), ("format", "formatted-citation"), ("html", "1")),
                CancellationToken.None);

            html.StatusCode.Should().Be(200);
            html.ContentType.Should().StartWith("text/html");
            html.Body.Should().Contain("Austen");
        }
    }

    [Fact]
    public async Task Formatted_citation_with_citation_only_style_still_renders_inline()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            (CaywRequestHandler handler, _, _) = await SelectedSetupAsync(context);

            CitationHttpResponse response = await handler.HandleAsync(
                Query(("selected", "1"), ("format", "formatted-citation"), ("style", "citation-only")),
                CancellationToken.None);

            response.StatusCode.Should().Be(200);
            response.Body.Should().Contain("Austen");
        }
    }

    [Fact]
    public async Task Formatted_citation_with_unknown_style_fails_explicitly()
    {
        InteropTestContext context = await InteropTestContext.CreateAsync();
        await using (context)
        {
            (CaywRequestHandler handler, _, _) = await SelectedSetupAsync(context);

            CitationHttpResponse response = await handler.HandleAsync(
                Query(("selected", "1"), ("format", "formatted-citation"), ("style", "no-such-style")),
                CancellationToken.None);

            response.StatusCode.Should().Be(500,
                "an explicitly requested but missing style must fail, not silently fall back");
            response.Body.Should().NotContain("Austen");
        }
    }
}
