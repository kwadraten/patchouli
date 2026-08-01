using FluentAssertions;
using Patchouli.Cli;

namespace Patchouli.Tests;

/// <summary>
/// Verifies the CLI/MCP isomorphism: each patchouli-cli verb maps onto one MCP tool request
/// with the same parameter names, and the legacy shell/regex/base-revision surface is absent
/// from the CLI's external contract.
/// </summary>
public sealed class CliCommandMappingTests
{
    [Fact]
    public void Find_browses_with_no_arguments()
    {
        CliToolCall call = CliArguments.BuildToolCall("find", [], false);
        call.Tool.Should().Be(CliArguments.Find);
        call.Arguments.Should().NotContainKey("query")
            .And.NotContainKey("regex")
            .And.NotContainKey("format");
    }

    [Fact]
    public void Find_maps_query_and_flags_to_tool_arguments()
    {
        CliToolCall call = CliArguments.BuildToolCall("find",
            ["consensus", "--in", "patchouli://texts/", "--literal", "--limit", "10"], false);
        call.Tool.Should().Be(CliArguments.Find);
        call.Arguments["query"].Should().Be("consensus");
        call.Arguments["in"].Should().Be("patchouli://texts/");
        call.Arguments["literal"].Should().Be(true);
        call.Arguments["limit"].Should().Be(10);
    }

    [Fact]
    public void Find_maps_repeatable_where_clauses_to_array()
    {
        CliToolCall call = CliArguments.BuildToolCall("find",
            ["--where", "item_type=book", "--where", "item_status=active"], false);
        call.Arguments["where"].Should().BeAssignableTo<string[]>()
            .Which.Should().Equal("item_type=book", "item_status=active");
    }

    [Fact]
    public void Find_long_maps_to_detail_long()
    {
        CliToolCall call = CliArguments.BuildToolCall("find", ["--long"], false);
        call.Arguments["detail"].Should().Be("long");
    }

    [Fact]
    public void Find_rejects_regex_as_unknown_option()
    {
        Action act = () => CliArguments.BuildToolCall("find", ["--regex", "pattern"], false);
        act.Should().Throw<CliUsageException>();
    }

    [Fact]
    public void Fetch_maps_uris_range_and_limit_bytes()
    {
        CliToolCall call = CliArguments.BuildToolCall("fetch",
            ["patchouli://items/abc.bib", "--range", "lines:1-5", "--limit-bytes", "128"], false);
        call.Tool.Should().Be(CliArguments.Fetch);
        call.Arguments["uris"].Should().BeAssignableTo<string[]>()
            .Which.Should().Equal("patchouli://items/abc.bib");
        call.Arguments["range"].Should().Be("lines:1-5");
        call.Arguments["limit_bytes"].Should().Be(128);
        call.Arguments.Should().NotContainKey("revision");
    }

    [Fact]
    public void Fetch_requires_at_least_one_uri()
    {
        Action act = () => CliArguments.BuildToolCall("fetch", [], false);
        act.Should().Throw<CliUsageException>();
    }

    [Fact]
    public void Fetch_rejects_revision_as_unknown_option()
    {
        Action act = () => CliArguments.BuildToolCall("fetch",
            ["patchouli://items/abc.bib", "--revision", "item:x"], false);
        act.Should().Throw<CliUsageException>();
    }

    [Fact]
    public void Put_maps_uri_and_stdin_source()
    {
        CliToolCall call = CliArguments.BuildToolCall("put", ["patchouli://items/abc.bib", "--stdin"], false);
        call.Tool.Should().Be(CliArguments.Put);
        call.Arguments["uri"].Should().Be("patchouli://items/abc.bib");
        call.Arguments.Should().NotContainKey("base");
        call.PutStdin.Should().BeTrue();
        call.PutSourcePath.Should().BeNull();
    }

    [Fact]
    public void Put_maps_from_path_source()
    {
        CliToolCall call = CliArguments.BuildToolCall("put",
            ["patchouli://csl-styles/apa.csl", "--from", "apa.csl"], false);
        call.PutSourcePath.Should().Be("apa.csl");
        call.PutStdin.Should().BeFalse();
    }

    [Fact]
    public void Put_rejects_base_as_unknown_option()
    {
        Action act = () => CliArguments.BuildToolCall("put",
            ["patchouli://items/abc.bib", "--stdin", "--base", "item:x"], false);
        act.Should().Throw<CliUsageException>();
    }

    [Fact]
    public void Put_requires_a_content_source()
    {
        Action act = () => CliArguments.BuildToolCall("put", ["patchouli://items/abc.bib"], false);
        act.Should().Throw<CliUsageException>();
    }

    [Fact]
    public void Put_rejects_both_from_and_stdin()
    {
        Action act = () => CliArguments.BuildToolCall("put",
            ["patchouli://items/abc.bib", "--from", "x.bib", "--stdin"], false);
        act.Should().Throw<CliUsageException>();
    }

    [Fact]
    public void Put_with_content_injects_inline_content()
    {
        CliToolCall call = CliArguments.BuildToolCall("put", ["patchouli://items/abc.bib", "--stdin"], false);
        CliToolCall withContent = CliArguments.WithContent(call, "@book{a,\n title = {x}\n}");
        withContent.Arguments["content"].Should().Be("@book{a,\n title = {x}\n}");
        withContent.Arguments["uri"].Should().Be("patchouli://items/abc.bib");
        withContent.PutStdin.Should().BeFalse();
    }

    [Fact]
    public void Cite_maps_refs_and_style_options()
    {
        CliToolCall call = CliArguments.BuildToolCall("cite",
        [
            "patchouli://items/abc.bib", "--style", "patchouli://csl-styles/apa.csl",
            "--locale", "en-US", "--bibliography", "--html"
        ], false);
        call.Tool.Should().Be(CliArguments.Cite);
        call.Arguments["refs"].Should().BeAssignableTo<string[]>().Which.Should().Equal("patchouli://items/abc.bib");
        call.Arguments["style"].Should().Be("patchouli://csl-styles/apa.csl");
        call.Arguments["locale"].Should().Be("en-US");
        call.Arguments["bibliography"].Should().Be(true);
        call.Arguments["html"].Should().Be(true);
    }

    [Fact]
    public void Cite_requires_at_least_one_ref()
    {
        Action act = () => CliArguments.BuildToolCall("cite", [], false);
        act.Should().Throw<CliUsageException>();
    }

    [Fact]
    public void Json_flag_maps_to_format_json_on_every_command()
    {
        CliToolCall find = CliArguments.BuildToolCall("find", ["x"], true);
        find.Arguments["format"].Should().Be("json");
        CliToolCall fetch = CliArguments.BuildToolCall("fetch", ["patchouli://items/abc.bib"], true);
        fetch.Arguments["format"].Should().Be("json");
        CliToolCall put = CliArguments.BuildToolCall("put", ["patchouli://items/abc.bib", "--stdin"], true);
        put.Arguments["format"].Should().Be("json");
        CliToolCall cite = CliArguments.BuildToolCall("cite", ["patchouli://items/abc.bib"], true);
        cite.Arguments["format"].Should().Be("json");
    }

    [Fact]
    public void Default_encoding_omits_format_matching_host_toon_default()
    {
        CliToolCall call = CliArguments.BuildToolCall("find", ["x"], false);
        call.Arguments.Should().NotContainKey("format");
    }

    [Fact]
    public void Unknown_command_is_rejected()
    {
        Action act = () => CliArguments.BuildToolCall("shell", ["ls /items"], false);
        act.Should().Throw<CliUsageException>();
    }

    [Fact]
    public void Extract_exit_code_reads_unified_json_error_envelope()
    {
        const string text =
            "{\"meta\":{\"library_revision\":\"lib:1\"},\"continuation\":null," +
            "\"message\":{\"warnings\":[],\"error\":{\"code\":3,\"name\":\"NOT_FOUND\",\"correlation_id\":null}}," +
            "\"entries\":[]}";
        McpHttpClient.ExtractExitCode(text, true).Should().Be(3);
    }

    [Fact]
    public void Extract_exit_code_returns_zero_for_clean_success()
    {
        const string text = "{\"meta\":{\"library_revision\":\"lib:1\"},\"continuation\":null,\"entries\":[]}";
        McpHttpClient.ExtractExitCode(text, false).Should().Be(0);
    }

    [Fact]
    public void Extract_exit_code_scans_toon_error_block_when_not_json()
    {
        const string text = "message:\n  error: { code: 2, name: \"INVALID_ARGUMENT\", correlation_id: null }";
        McpHttpClient.ExtractExitCode(text, true).Should().Be(2);
    }
}
