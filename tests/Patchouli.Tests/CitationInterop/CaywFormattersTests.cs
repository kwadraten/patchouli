using System.Text.Json.Nodes;
using FluentAssertions;
using Patchouli.Core.CitationInterop;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Tests.CitationInterop;

/// <summary>Golden fixtures for the CAYW string formatters (issue #14 §3/§9).</summary>
public sealed class CaywFormattersTests
{
    private static readonly ItemId IdA = ItemId.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly ItemId IdB = ItemId.Parse("22222222-2222-2222-2222-222222222222");

    private static CitationPick Pick(params CitationPickEntry[] entries)
    {
        return new CitationPick(entries);
    }

    private static CitationPickEntry Entry(ItemId id, string key, string? locator = null,
        string? label = null, string? prefix = null, string? suffix = null, bool suppress = false)
    {
        return new CitationPickEntry(id, key, locator, label, prefix, suffix, suppress);
    }

    private static string Format(ICitationStringFormatter formatter, CitationPick pick,
        CitationFormatOptions? options = null, IReadOnlyList<CitationItemProjection>? items = null)
    {
        Result<string> result =
            formatter.Format(new CitationFormatRequest(pick, items ?? [], options ?? new CitationFormatOptions()));
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        return result.Value;
    }

    // ---- natbib / latex ----

    [Fact]
    public void Natbib_plain_multi_item_pick_is_one_combined_command()
    {
        Format(new NatbibCitationFormatter(), Pick(Entry(IdA, "austen1813"), Entry(IdB, "bronte1847")))
            .Should().Be(@"\cite{austen1813,bronte1847}");
    }

    [Fact]
    public void Natbib_command_parameter_selects_citep()
    {
        Format(new NatbibCitationFormatter(), Pick(Entry(IdA, "austen1813")),
                new CitationFormatOptions("citep"))
            .Should().Be(@"\citep{austen1813}");
    }

    [Fact]
    public void Natbib_all_suppressed_uses_year_command()
    {
        Format(new NatbibCitationFormatter(), Pick(
                Entry(IdA, "austen1813", suppress: true), Entry(IdB, "bronte1847", suppress: true)))
            .Should().Be(@"\citeyear{austen1813,bronte1847}");
    }

    [Fact]
    public void Natbib_all_suppressed_citep_uses_citeyearpar()
    {
        Format(new NatbibCitationFormatter(), Pick(Entry(IdA, "austen1813", suppress: true)),
                new CitationFormatOptions("citep"))
            .Should().Be(@"\citeyearpar{austen1813}");
    }

    [Fact]
    public void Natbib_mixed_notes_split_into_per_entry_commands_like_bbt()
    {
        // Mirrors the BBT documentation example shape: one entry with a
        // locator, another suppressed -> separate commands.
        Format(new NatbibCitationFormatter(), Pick(
                Entry(IdA, "a", "1"),
                Entry(IdB, "b", suppress: true)))
            .Should().Be(@"\cite[p. 1]{a}\citeyear{b}");
    }

    [Fact]
    public void Natbib_prefix_and_locator_and_suffix_use_two_optional_arguments()
    {
        Format(new NatbibCitationFormatter(), Pick(
                Entry(IdA, "a", "33", prefix: "see", suffix: "and more")))
            .Should().Be(@"\cite[see][p. 33, and more]{a}");
    }

    [Fact]
    public void Natbib_prefix_only_gets_empty_post_note()
    {
        Format(new NatbibCitationFormatter(), Pick(Entry(IdA, "a", prefix: "see")))
            .Should().Be(@"\cite[see][]{a}");
    }

    [Fact]
    public void Natbib_roman_numeral_locator_keeps_page_label()
    {
        Format(new NatbibCitationFormatter(), Pick(Entry(IdA, "a", "iv")))
            .Should().Be(@"\cite[p. iv]{a}");
    }

    [Fact]
    public void Natbib_locator_label_override_and_removal()
    {
        CitationFormatOptions options = new(LocatorLabelOverrides: new Dictionary<string, string>
        {
            ["page"] = "Seite",
            ["chapter"] = ""
        });
        Format(new NatbibCitationFormatter(), Pick(Entry(IdA, "a", "3")), options)
            .Should().Be(@"\cite[Seite 3]{a}");
        Format(new NatbibCitationFormatter(), Pick(Entry(IdA, "a", "3", "chapter")), options)
            .Should().Be(@"\cite[3]{a}");
    }

    [Fact]
    public void Natbib_escapes_latex_specials_in_notes_but_never_rewrites_keys()
    {
        Format(new NatbibCitationFormatter(), Pick(Entry(IdA, "a", suffix: "50% & more")))
            .Should().Be(@"\cite[50\% \& more]{a}");
    }

    [Fact]
    public void Natbib_key_with_comma_is_an_explicit_error()
    {
        Result<string> result = new NatbibCitationFormatter().Format(
            new CitationFormatRequest(Pick(Entry(IdA, "a,b")), [], new CitationFormatOptions()));
        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().Contain("not modified");
    }

    [Fact]
    public void Natbib_command_with_latex_syntax_is_rejected()
    {
        Result<string> result = new NatbibCitationFormatter().Format(
            new CitationFormatRequest(Pick(Entry(IdA, "a")), [],
                new CitationFormatOptions(@"cite}\evil{")));
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Natbib_unicode_key_is_emitted_verbatim()
    {
        Format(new NatbibCitationFormatter(), Pick(Entry(IdA, "张三2024")))
            .Should().Be(@"\cite{张三2024}");
    }

    // ---- biblatex ----

    [Fact]
    public void Biblatex_default_command_is_autocite()
    {
        Format(new BiblatexCitationFormatter(), Pick(Entry(IdA, "a"), Entry(IdB, "b")))
            .Should().Be(@"\autocite{a,b}");
    }

    [Fact]
    public void Biblatex_single_suppressed_cite_becomes_starred()
    {
        Format(new BiblatexCitationFormatter(), Pick(Entry(IdA, "a", suppress: true)),
                new CitationFormatOptions("parencite"))
            .Should().Be(@"\parencite*{a}");
    }

    [Fact]
    public void Biblatex_suppress_is_ignored_for_other_commands()
    {
        Format(new BiblatexCitationFormatter(), Pick(Entry(IdA, "a", suppress: true)),
                new CitationFormatOptions("textcite"))
            .Should().Be(@"\textcite{a}");
    }

    [Fact]
    public void Biblatex_multi_with_notes_uses_s_variant()
    {
        Format(new BiblatexCitationFormatter(), Pick(
                Entry(IdA, "a", "33"),
                Entry(IdB, "b")))
            .Should().Be(@"\autocites[p. 33]{a}{b}");
    }

    // ---- pandoc ----

    [Fact]
    public void Pandoc_brackets_wrap_the_whole_pick()
    {
        Format(new PandocCitationFormatter(), Pick(Entry(IdA, "a"), Entry(IdB, "b")),
                new CitationFormatOptions(Brackets: true))
            .Should().Be("[@a; @b]");
    }

    [Fact]
    public void Pandoc_in_text_with_locator_prefix_suffix_and_suppress()
    {
        Format(new PandocCitationFormatter(), Pick(
                Entry(IdA, "a", "33", prefix: "see", suffix: "and passim"),
                Entry(IdB, "b", suppress: true)))
            .Should().Be("[see @a, p. 33 and passim] -@b");
    }

    [Fact]
    public void Pandoc_roman_locator_is_braced()
    {
        Format(new PandocCitationFormatter(), Pick(Entry(IdA, "a", "iv")))
            .Should().Be("[@a, {p. iv}]");
    }

    [Fact]
    public void Pandoc_bad_key_is_an_explicit_error()
    {
        Result<string> result = new PandocCitationFormatter().Format(
            new CitationFormatRequest(Pick(Entry(IdA, "a b")), [], new CitationFormatOptions()));
        result.IsFailure.Should().BeTrue();
    }

    // ---- typst ----

    [Fact]
    public void Typst_plain_pick_uses_label_shorthand()
    {
        Format(new TypstCitationFormatter(), Pick(Entry(IdA, "austen1813"), Entry(IdB, "bronte1847")))
            .Should().Be("@austen1813 @bronte1847");
    }

    [Fact]
    public void Typst_locator_uses_functional_form_with_supplement()
    {
        Format(new TypstCitationFormatter(), Pick(Entry(IdA, "a", "33")))
            .Should().Be("#cite(<a>, supplement: [p. 33])");
    }

    [Fact]
    public void Typst_suppress_author_uses_year_form()
    {
        Format(new TypstCitationFormatter(), Pick(Entry(IdA, "a", suppress: true)))
            .Should().Be("#cite(<a>, form: \"year\")");
    }

    [Fact]
    public void Typst_key_with_space_is_an_explicit_error()
    {
        Result<string> result = new TypstCitationFormatter().Format(
            new CitationFormatRequest(Pick(Entry(IdA, "a b")), [], new CitationFormatOptions()));
        result.IsFailure.Should().BeTrue();
    }

    // ---- json ----

    [Fact]
    public void Json_pick_contains_projection_pick_fields_and_citekey()
    {
        CitationItemProjection projection = new(IdA, "austen1813",
            new Dictionary<string, object?>
            {
                ["type"] = "book",
                ["title"] = "Pride and Prejudice",
                ["citation-key"] = "austen1813"
            });
        string json = Format(new JsonPickFormatter(),
            Pick(Entry(IdA, "austen1813", "33", suppress: true)),
            items: [projection]);

        JsonArray array = JsonNode.Parse(json)!.AsArray();
        JsonObject item = array.Single()!.AsObject();
        item["citekey"]!.GetValue<string>().Should().Be("austen1813");
        item["citation-key"]!.GetValue<string>().Should().Be("austen1813");
        item["id"]!.GetValue<string>().Should().Be(IdA.ToString());
        item["locator"]!.GetValue<string>().Should().Be("33");
        item["suppress-author"]!.GetValue<bool>().Should().BeTrue();
        // No fabricated Zotero identity.
        item.ContainsKey("uri").Should().BeFalse();
        json.Should().NotContain("zotero.org");
    }

    // ---- resolution ----

    [Theory]
    [InlineData("latex", "natbib")]
    [InlineData("cite", "natbib")]
    [InlineData("citep", "natbib")]
    [InlineData("citet", "natbib")]
    [InlineData("natbib", "natbib")]
    [InlineData(null, "natbib")]
    [InlineData("biblatex", "biblatex")]
    [InlineData("pandoc", "pandoc")]
    [InlineData("typst", "typst")]
    [InlineData("json", "json")]
    public void Format_aliases_resolve(string? format, string canonical)
    {
        Result<CitationFormatters.Resolution> resolved = CitationFormatters.Resolve(format);
        resolved.IsSuccess.Should().BeTrue();
        resolved.Value.CanonicalFormat.Should().Be(canonical);
        resolved.Value.IsFormattedCsl.Should().BeFalse();
    }

    [Theory]
    [InlineData("formatted-citation")]
    [InlineData("formatted-bibliography")]
    public void Formatted_formats_route_to_csl(string format)
    {
        Result<CitationFormatters.Resolution> resolved = CitationFormatters.Resolve(format);
        resolved.IsSuccess.Should().BeTrue();
        resolved.Value.IsFormattedCsl.Should().BeTrue();
    }

    [Theory]
    [InlineData("mmd")]
    [InlineData("jupyter")]
    [InlineData("asciidoctor-bibtex")]
    [InlineData("scannable-cite")]
    [InlineData("eta")]
    [InlineData("translate")]
    public void Known_unsupported_formats_fail_explicitly(string format)
    {
        Result<CitationFormatters.Resolution> resolved = CitationFormatters.Resolve(format);
        resolved.IsFailure.Should().BeTrue();
        resolved.ErrorCode.Should().Be("cayw_format_unsupported");
    }

    [Fact]
    public void Unknown_format_fails_explicitly()
    {
        Result<CitationFormatters.Resolution> resolved = CitationFormatters.Resolve("docx");
        resolved.IsFailure.Should().BeTrue();
        resolved.ErrorCode.Should().Be("cayw_format_unknown");
    }

    [Theory]
    [InlineData("false", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Flag_truthiness_follows_bbt_non_empty_rule(string? value, bool expected)
    {
        // BBT flags are "set" for ANY non-empty value — "false" included.
        CaywCompatibility.IsFlagSet(value).Should().Be(expected);
    }

    [Fact]
    public void Empty_pick_formats_to_empty_string()
    {
        Format(new NatbibCitationFormatter(), Pick()).Should().Be("");
        Format(new PandocCitationFormatter(), Pick()).Should().Be("");
        Format(new TypstCitationFormatter(), Pick()).Should().Be("");
    }
}
