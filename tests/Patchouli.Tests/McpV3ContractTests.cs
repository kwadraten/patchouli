using System.Text.Json;
using System.Text.Json.Nodes;
using Corvus.Toon;
using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Mcp;
using Xunit;

namespace Patchouli.Tests;

/// <summary>
/// Pure v3 protocol-contract tests: resource tree grammar, the closed
/// { meta, continuation, message?, entries } envelope shape, terminal diagnostics, and the error table.
/// These do not touch the database or infrastructure.
/// </summary>
public sealed class McpV3ContractTests
{
    private static readonly ItemId ItemId = new(Guid.Parse("00000000-0000-0000-0000-000000000001"));
    private static readonly DocumentInstanceId DocumentId = new(Guid.Parse("10000000-0000-0000-0000-000000000001"));

    [Theory]
    [InlineData("patchouli://", McpUriKind.Root)]
    [InlineData("patchouli://items/", McpUriKind.ItemsScope)]
    [InlineData("patchouli://items/00000000-0000-0000-0000-000000000001.bib", McpUriKind.Item)]
    [InlineData("patchouli://texts/", McpUriKind.TextsScope)]
    [InlineData("patchouli://texts/10000000-0000-0000-0000-000000000001/", McpUriKind.Document)]
    [InlineData("patchouli://texts/10000000-0000-0000-0000-000000000001/page-1.md", McpUriKind.Page)]
    [InlineData("patchouli://texts/10000000-0000-0000-0000-000000000001/page-7.md?evref=evref:v2:abc",
        McpUriKind.EvidenceRef)]
    [InlineData("patchouli://csl-styles/", McpUriKind.StylesScope)]
    [InlineData("patchouli://csl-styles/apa.csl", McpUriKind.Style)]
    public void Resource_uris_parse_the_v3_tree(string uri, McpUriKind expectedKind)
    {
        McpResourceUris.Parse(uri).Value.Kind.Should().Be(expectedKind);
    }

    [Theory]
    [InlineData("patchouli://documents/")]
    [InlineData("patchouli://documents/10000000-0000-0000-0000-000000000001/")]
    [InlineData(
        "patchouli://documents/10000000-0000-0000-0000-000000000001/pages/20000000-0000-0000-0000-000000000001.md")]
    [InlineData("patchouli://styles/")]
    [InlineData("patchouli://styles/apa.csl")]
    [InlineData("patchouli://evidence/")]
    [InlineData("patchouli://evidence/evref:v2:any")]
    [InlineData("patchouli://texts/10000000-0000-0000-0000-000000000001/page-0.md")]
    [InlineData("patchouli://texts/10000000-0000-0000-0000-000000000001/page-1")]
    [InlineData("patchouli://texts/10000000-0000-0000-0000-000000000001/page-1.md?foo=bar")]
    [InlineData("patchouli://texts/10000000-0000-0000-0000-000000000001/pages/20000000-0000-0000-0000-000000000001.md")]
    [InlineData("patchouli://items/00000000-0000-0000-0000-000000000001")]
    [InlineData("patchouli://items/00000000-0000-0000-0000-000000000001.bib/")]
    [InlineData("patchouli://?evref=x")]
    public void Legacy_and_malformed_uris_are_rejected(string uri)
    {
        McpResourceUris.Parse(uri).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Page_uri_uses_one_based_physical_page_index()
    {
        McpResourceUris.PageUri(DocumentId, 1).Should().Be(
            "patchouli://texts/10000000-0000-0000-0000-000000000001/page-1.md");
        McpResourceUris.PageUri(DocumentId, 12).Should().Be(
            "patchouli://texts/10000000-0000-0000-0000-000000000001/page-12.md");
    }

    [Fact]
    public void Evidence_page_uri_embeds_the_canonical_evref_query()
    {
        McpResourceUris.EvidencePageUri(DocumentId, 2, "evref:v2:abc").Should().Be(
            "patchouli://texts/10000000-0000-0000-0000-000000000001/page-2.md?evref=evref:v2:abc");
    }

    [Fact]
    public void Item_and_style_uris_use_v3_scopes()
    {
        McpResourceUris.ItemUri(ItemId).Should().Be(
            "patchouli://items/00000000-0000-0000-0000-000000000001.bib");
        McpResourceUris.StyleUri("apa").Should().Be("patchouli://csl-styles/apa.csl");
        McpResourceUris.DocumentUri(DocumentId).Should().Be(
            "patchouli://texts/10000000-0000-0000-0000-000000000001/");
    }

    [Fact]
    public void Clean_success_envelope_omits_message()
    {
        McpEnvelope<McpFindMeta, McpFindEntry> envelope =
            McpEnvelope<McpFindMeta, McpFindEntry>.Create(new McpFindMeta("lib:1", 3, 3, 3),
                [new McpFindEntry("patchouli://items/", "/items", "directory")]);
        JsonNode root = JsonSerializer.SerializeToNode(envelope)!;
        root.AsObject().Select(pair => pair.Key).Should().Equal("meta", "continuation", "entries");
        root["continuation"].Should().BeNull("JSON null values surface as null nodes");
        root["meta"]!["library_revision"]!.GetValue<string>().Should().Be("lib:1");
        root["meta"]!["domain_total"]!.GetValue<int>().Should().Be(3);
    }

    [Fact]
    public void Warning_envelope_carries_message_with_null_error()
    {
        McpEnvelope<McpFindMeta, McpFindEntry> envelope =
            McpEnvelope<McpFindMeta, McpFindEntry>.Create(
                new McpFindMeta("lib:1", 3, 3, 1),
                [new McpFindEntry("patchouli://items/", "/items", "directory")],
                message: new McpMessage(null,
                    [McpWarningCodes.ToTerminalLine(McpWarningCodes.WhitespaceQueryTreatedAsBrowse)]));
        JsonNode root = JsonSerializer.SerializeToNode(envelope)!;
        root["message"]!["warnings"]![0]!.GetValue<string>().Should()
            .Be(McpWarningCodes.ToTerminalLine(McpWarningCodes.WhitespaceQueryTreatedAsBrowse));
        root["message"]!["error"].Should().BeNull();
    }

    [Fact]
    public void Error_renders_a_compact_sanitized_terminal_diagnostic()
    {
        McpToolError error = McpToolError.From(McpErrorCode.NotFound, "resource was not found",
            "corr-1");
        error.Code.Should().Be(3);
        error.Name.Should().Be("NOT_FOUND");
        error.ToTerminalLine().Should().Be("NOT_FOUND [code 3; ref corr-1]: resource was not found");
    }

    [Theory]
    [InlineData(McpErrorCode.Ok, "OK", 0)]
    [InlineData(McpErrorCode.Internal, "INTERNAL", 1)]
    [InlineData(McpErrorCode.InvalidArgument, "INVALID_ARGUMENT", 2)]
    [InlineData(McpErrorCode.NotFound, "NOT_FOUND", 3)]
    [InlineData(McpErrorCode.PermissionDenied, "PERMISSION_DENIED", 4)]
    [InlineData(McpErrorCode.Reserved, "RESERVED", 5)]
    [InlineData(McpErrorCode.InvalidContent, "INVALID_CONTENT", 6)]
    [InlineData(McpErrorCode.ResponseTruncated, "RESPONSE_TRUNCATED", 7)]
    [InlineData(McpErrorCode.Unavailable, "UNAVAILABLE", 8)]
    [InlineData(McpErrorCode.NotCitable, "NOT_CITABLE", 9)]
    [InlineData(McpErrorCode.DeadlineExceeded, "DEADLINE_EXCEEDED", 10)]
    [InlineData(McpErrorCode.Cancelled, "CANCELLED", 11)]
    public void Error_table_names_match_the_prd(McpErrorCode code, string name, int numeric)
    {
        ((int)code).Should().Be(numeric);
        McpToolError.ErrorName(code).Should().Be(name);
        McpToolError error = McpToolError.From(code);
        error.Code.Should().Be(numeric);
        error.Name.Should().Be(name);
    }

    [Fact]
    public void Find_entry_projection_has_no_legacy_fields()
    {
        McpFindEntry entry = new("patchouli://items/x.bib", "Title", "file");
        JsonNode node = JsonSerializer.SerializeToNode(entry)!;
        node.AsObject().Select(pair => pair.Key).Should().Equal("uri", "title", "type");
    }

    [Fact]
    public void Long_find_variants_omit_inapplicable_fields()
    {
        McpItemLongEntry entry = new("patchouli://items/x.bib", "Title", "file", "active", "indexed", true);
        JsonNode node = JsonSerializer.SerializeToNode(entry)!;
        node["item_status"]!.GetValue<string>().Should().Be("active");
        node["primary_document_ocr_index_status"]!.GetValue<string>().Should().Be("indexed");
        node.AsObject().ContainsKey("document_status").Should().BeFalse();
        node.AsObject().ContainsKey("source_status").Should().BeFalse();
        node.AsObject().ContainsKey("style_enabled").Should().BeFalse();
        node["citable"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Toon_codec_pins_the_fixed_deterministic_profile()
    {
        McpToonCodec.MediaType.Should().Be("text/toon");
        McpToonCodec.WriterOptions.Delimiter.Should().Be(ToonDelimiter.Tab);
        McpToonCodec.WriterOptions.KeyFolding.Should().Be(ToonKeyFolding.Off);
        McpToonCodec.ReaderOptions.Strict.Should().BeTrue();
        McpToonCodec.ReaderOptions.ExpandPaths.Should().Be(ToonPathExpansion.Off);
    }

    [Fact]
    public void Toon_encoding_uses_utf8_lf_without_carriage_returns()
    {
        string toon = McpToonCodec.Encode(CleanFindEnvelope());
        toon.Should().Contain("\n");
        toon.Should().NotContain("\r");
    }

    [Fact]
    public void Toon_tabular_arrays_use_literal_tab_delimiters()
    {
        string toon = McpToonCodec.Encode(CleanFindEnvelope());
        toon.Should().Contain("entries[3\t]{uri\ttitle\ttype}:");
        toon.Should().Contain("\"patchouli://items/\"\t/items\tdirectory");
    }

    [Fact]
    public void Toon_encoding_keeps_nested_keys_with_key_folding_off()
    {
        string toon = McpToonCodec.Encode(CleanFindEnvelope());
        toon.Should().Contain("meta:\n  library_revision: \"lib:1\"");
        toon.Should().NotContain("meta.library_revision");
    }

    [Fact]
    public void Toon_tabular_array_declares_the_exact_row_count()
    {
        string[] lines = McpToonCodec.Encode(CleanFindEnvelope()).Split('\n');
        int header = Array.FindIndex(lines, line => line.StartsWith("entries[", StringComparison.Ordinal));
        header.Should().BeGreaterThanOrEqualTo(0);
        lines[header].Should().Contain("entries[3\t]{uri\ttitle\ttype}:");
        int rows = lines.Skip(header + 1).Count(line => line.StartsWith("  ", StringComparison.Ordinal));
        rows.Should().Be(3);
    }

    [Fact]
    public void Toon_clean_find_envelope_round_trips_to_identical_json()
    {
        McpEnvelope<McpFindMeta, McpFindEntry> envelope = CleanFindEnvelope();
        string json = JsonSerializer.Serialize(envelope);
        McpToonCodec.DecodeToJson(McpToonCodec.Encode(envelope)).Should().Be(json);
    }

    [Fact]
    public void Toon_warning_and_error_envelope_round_trips_to_identical_json()
    {
        McpEnvelope<McpFindMeta, McpFindEntry> envelope =
            McpEnvelope<McpFindMeta, McpFindEntry>.Create(
                new McpFindMeta("lib:1", 3, 3, 1),
                [new McpFindEntry("patchouli://items/", "/items", "directory")],
                message: new McpMessage(
                    McpToolError.From(McpErrorCode.NotFound, "resource was not found", "corr-1").ToTerminalLine(),
                    [McpWarningCodes.ToTerminalLine(McpWarningCodes.WhitespaceQueryTreatedAsBrowse)]));
        string json = JsonSerializer.Serialize(envelope);
        McpToonCodec.DecodeToJson(McpToonCodec.Encode(envelope)).Should().Be(json);
    }

    [Fact]
    public void Toon_preserves_number_boolean_and_null_json_types()
    {
        Dictionary<string, object?> value = new()
        {
            ["int"] = 42,
            ["number"] = 42.5,
            ["flag"] = true,
            ["negated"] = false,
            ["nil"] = null,
            ["text"] = "lib:42"
        };
        string json = JsonSerializer.Serialize(value);
        string toon = McpToonCodec.Encode(value);
        toon.Should().Contain("int: 42");
        toon.Should().Contain("number: 42.5");
        toon.Should().Contain("flag: true");
        toon.Should().Contain("negated: false");
        toon.Should().Contain("nil: null");
        toon.Should().Contain("text: \"lib:42\"");
        McpToonCodec.DecodeToJson(toon).Should().Be(json);
    }

    [Fact]
    public void Toon_lexical_quoting_and_escaping_round_trips_strings()
    {
        Dictionary<string, object?> value = new()
        {
            ["spaced"] = "hello world",
            ["unicode"] = "华北解放区",
            ["special"] = "a\tb\"c\nd",
            ["empty"] = ""
        };
        string json = JsonSerializer.Serialize(value);
        string toon = McpToonCodec.Encode(value);
        toon.Should().Contain("spaced: hello world");
        toon.Should().Contain("special: \"a\\tb\\\"c\\nd\"");
        toon.Should().Contain("empty: \"\"");
        McpToonCodec.DecodeToJson(toon).Should().Be(json);
    }

    [Fact]
    public void Default_text_output_helpers_are_wired_in_the_command_contract()
    {
        McpEnvelope<McpFindMeta, McpFindEntry> envelope = CleanFindEnvelope();
        McpCommandService.DefaultToonEncoder.Should().NotBeNull();
        McpCommandService.DefaultJsonEncoder.Should().NotBeNull();
        McpCommandService.DefaultToonEncoder(envelope).Should().Be(McpToonCodec.Encode(envelope));
        McpCommandService.DefaultJsonEncoder(envelope).Should().Be(JsonSerializer.Serialize(envelope));
        McpCommandService.RenderText(envelope, null).Should().Be(McpToonCodec.Encode(envelope));
        McpCommandService.RenderText(envelope, "toon").Should().Be(McpToonCodec.Encode(envelope));
        McpCommandService.RenderText(envelope, "json").Should().Be(JsonSerializer.Serialize(envelope));
    }

    [Fact]
    public void Toon_and_json_projections_differ_only_in_encoding()
    {
        McpEnvelope<McpFindMeta, McpFindEntry> envelope = CleanFindEnvelope();
        string toon = McpCommandService.RenderText(envelope, "toon");
        string json = McpCommandService.RenderText(envelope, "json");
        toon.Should().NotBe(json);
        JsonDocument.Parse(json).RootElement.GetProperty("meta").GetProperty("library_revision").GetString()
            .Should().Be("lib:1");
        McpToonCodec.DecodeToJson(toon).Should().Be(json);
    }

    private static McpEnvelope<McpFindMeta, McpFindEntry> CleanFindEnvelope()
    {
        return McpEnvelope<McpFindMeta, McpFindEntry>.Create(
            new McpFindMeta("lib:1", 3, 3, 3),
            [
                new McpFindEntry("patchouli://items/", "/items", "directory"),
                new McpFindEntry("patchouli://texts/", "/texts", "directory"),
                new McpFindEntry("patchouli://csl-styles/", "/csl-styles", "directory")
            ]);
    }
}
