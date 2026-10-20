using System.Text.Json;
using FluentAssertions;
using Patchouli.Cli;

namespace Patchouli.Tests;

public sealed class OcrCliIndependentTests
{
    private const string Document = "texts/11111111-1111-1111-1111-111111111111/";

    [Fact]
    public void Flat_options_map_to_the_same_MCP_fields_and_typed_args()
    {
        CliToolCall call = CliArguments.BuildToolCall("send",
        [
            "runs/translation/control", "start", Document, "--pages", "1,3-5", "--target-language", "zh-CN", "--force",
            "false", "--request-id", "stable"
        ], true);
        call.Tool.Should().Be("patchouli.send");
        call.Arguments["endpoint"].Should().Be("patchouli://runs/translation/control");
        call.Arguments["action"].Should().Be("start");
        call.Arguments["target"].Should().Be(Document);
        call.Arguments["request_id"].Should().Be("stable");
        call.Arguments["format"].Should().Be("json");
        JsonElement args = JsonSerializer.SerializeToElement(call.Arguments["args"]);
        args.GetProperty("force").GetBoolean().Should().BeFalse();
        args.GetProperty("target_language").GetString().Should().Be("zh-CN");
        args.GetProperty("pages").GetString().Should().Be("1,3-5");
        args.TryGetProperty("request_id", out _).Should().BeFalse();
    }

    [Fact]
    public void Raw_args_and_flat_options_combine_without_overwriting()
    {
        CliToolCall call = CliArguments.BuildToolCall("send", [
            "runs/ocr/control", "start", Document,
            "--args", "{\"pages\":\"1\"}", "--engine", "default"
        ], false);
        JsonElement args = JsonSerializer.SerializeToElement(call.Arguments["args"]);
        args.GetProperty("engine").GetString().Should().Be("default");
        args.GetProperty("pages").GetString().Should().Be("1");
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("retry")]
    public void Messages_without_domain_options_send_an_empty_object(string action)
    {
        CliToolCall call = CliArguments.BuildToolCall("send",
            ["runs/ocr/control", action, "runs/ocr/22222222-2222-2222-2222-222222222222/"], false);
        JsonSerializer.SerializeToElement(call.Arguments["args"]).EnumerateObject().Should().BeEmpty();
    }

    [Theory]
    [InlineData("--engine", "mock", "--engine", "rapidocr")]
    [InlineData("--engine", "mock", "--args", "{\"engine\":\"mock\"}")]
    [InlineData("--args", "{\"engine\":\"mock\"}", "--engine", "mock")]
    [InlineData("--args", "{\"engine\":\"mock\",\"engine\":\"rapidocr\"}", "--pages", "1")]
    [InlineData("--request-id", "r1", "--request-id", "r1")]
    public void Duplicate_or_conflicting_options_are_rejected(string a, string b, string c, string d)
    {
        Action parse = () =>
            CliArguments.BuildToolCall("send", ["runs/ocr/control", "start", Document, a, b, c, d], false);
        parse.Should().Throw<CliUsageException>();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("1")]
    [InlineData("{")]
    [InlineData("{\"request_id\":\"nested\"}")]
    [InlineData("{\"engine\":false}")]
    public void Invalid_raw_domain_arguments_are_rejected(string args)
    {
        Action parse = () =>
            CliArguments.BuildToolCall("send", ["runs/ocr/control", "start", Document, "--args", args], false);
        parse.Should().Throw<CliUsageException>();
    }

    [Theory]
    [InlineData("--force", "yes")]
    [InlineData("--preset", "p")]
    [InlineData("--target", "other")]
    public void Invalid_or_reserved_flat_options_are_rejected(string option, string value)
    {
        Action parse = () =>
            CliArguments.BuildToolCall("send", ["runs/translation/control", "start", Document, option, value], false);
        parse.Should().Throw<CliUsageException>();
    }

    [Fact]
    public void Old_combined_actions_and_agent_syntax_are_rejected_with_new_usage()
    {
        foreach (string[] args in new[]
                 {
                     new[] { Document, "ocr.start" }, new[] { "start", "workflow" },
                     new[] { "runs/ocr/control", "ocr.start", Document }
                 })
        {
            Action parse = () => CliArguments.BuildToolCall("send", args, false);
            parse.Should().Throw<CliUsageException>().Which.Message.Should().Contain("ENDPOINT");
        }
    }
}
