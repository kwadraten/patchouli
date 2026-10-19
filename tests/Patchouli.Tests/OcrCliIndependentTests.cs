using System.Text.Json;
using FluentAssertions;
using Patchouli.Cli;

namespace Patchouli.Tests;

public sealed class OcrCliIndependentTests
{
    private const string Document = "patchouli://texts/11111111-1111-1111-1111-111111111111/";
    private const string TaskUri = "patchouli://runs/ocr/22222222-2222-2222-2222-222222222222/";

    [Fact]
    public void Typed_send_preserves_ocr_scope_engine_and_request_identity_in_args()
    {
        CliToolCall call = CliArguments.BuildToolCall("send",
        [
            Document, "ocr.start", "--args",
            """{"pages":"1,3-5","engine":"rapidocr","request_id":"independent-request"}"""
        ], false);
        call.Tool.Should().Be("patchouli.send");
        call.Arguments["target"].Should().Be(Document);
        call.Arguments["action"].Should().Be("ocr.start");
        JsonElement args = JsonSerializer.SerializeToElement(call.Arguments["args"]);
        args.GetProperty("pages").GetString().Should().Be("1,3-5");
        args.GetProperty("engine").GetString().Should().Be("rapidocr");
        args.GetProperty("request_id").GetString().Should().Be("independent-request");
        call.Arguments.Keys.Should().BeEquivalentTo(["target", "action", "args"]);
    }

    [Theory]
    [InlineData("ocr.cancel")]
    [InlineData("ocr.retry")]
    public void Send_defaults_action_args_to_empty_object(string action)
    {
        CliToolCall call = CliArguments.BuildToolCall("send", [TaskUri, action], false);
        call.Tool.Should().Be("patchouli.send");
        call.Arguments["target"].Should().Be(TaskUri);
        call.Arguments["action"].Should().Be(action);
        JsonElement args = JsonSerializer.SerializeToElement(call.Arguments["args"]);
        args.ValueKind.Should().Be(JsonValueKind.Object);
        args.EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public void Unknown_action_is_forwarded_for_authoritative_registry_validation()
    {
        CliToolCall call = CliArguments.BuildToolCall("send", [Document, "unknown.action"], true);
        call.Arguments["action"].Should().Be("unknown.action");
        call.Arguments["format"].Should().Be("json");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("\"text\"")]
    [InlineData("{")]
    public void Send_rejects_invalid_or_non_object_args(string json)
    {
        Action parse = () => CliArguments.BuildToolCall("send", [Document, "ocr.start", "--args", json], false);
        parse.Should().Throw<CliUsageException>();
    }

    [Theory]
    [InlineData("start", "patchouli://workflows/wf")]
    [InlineData("cancel", "patchouli://runs/agent/session/")]
    [InlineData("message", "patchouli://runs/agent/session/")]
    public void Legacy_send_syntax_is_rejected_with_migration_hint(string verb, string oldTarget)
    {
        Action parse = () => CliArguments.BuildToolCall("send", [verb, oldTarget], false);
        parse.Should().Throw<CliUsageException>().Which.Message.ToLowerInvariant().Should().Contain("target");
    }

    [Fact]
    public void Removed_top_level_ocr_command_is_rejected()
    {
        Action parse = () => CliArguments.BuildToolCall("ocr", ["start", Document], false);
        parse.Should().Throw<CliUsageException>();
    }

    [Theory]
    [InlineData("--pages", "1")]
    [InlineData("--engine", "rapidocr")]
    [InlineData("--preset", "p")]
    public void Domain_options_are_not_promoted_to_generic_send_flags(string option, string value)
    {
        Action parse = () => CliArguments.BuildToolCall("send", [Document, "ocr.start", option, value], false);
        parse.Should().Throw<CliUsageException>();
    }
}
