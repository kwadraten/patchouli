using System.Text.Json;
using FluentAssertions;
using Patchouli.McpServer;

namespace Patchouli.Tests;

public sealed class McpPutInterfaceTests
{
    [Fact]
    public void Published_put_schema_describes_bounded_box_objects_and_versions()
    {
        JsonElement put = JsonDocument.Parse(McpProtocolHandler.AgentToolDefinitions()
                .Single(definition => JsonDocument.Parse(definition).RootElement.GetProperty("function")
                    .GetProperty("name").GetString() == "put")).RootElement.GetProperty("function")
            .GetProperty("parameters");

        JsonElement properties = put.GetProperty("properties");
        properties.GetProperty("source_revision").GetProperty("type").GetString().Should().Be("string");
        properties.GetProperty("translation_version").GetProperty("type").GetString().Should().Be("integer");
        properties.GetProperty("content").GetProperty("description").GetString()
            .Should().Contain("not both").And.Contain("boxes");
        JsonElement boxes = properties.GetProperty("boxes");
        boxes.GetProperty("minItems").GetInt32().Should().Be(1);
        boxes.GetProperty("maxItems").GetInt32().Should().Be(100);
        boxes.GetProperty("items").GetProperty("required").EnumerateArray()
            .Select(value => value.GetString()).Should().Equal("box_id", "content");
        put.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should().Equal("uri");
    }

    [Theory]
    [InlineData(
        "{\"uri\":\"patchouli://texts/doc/page-1.xml\",\"source_revision\":\"rev\",\"boxes\":[{\"box_id\":\"b1\",\"content\":\"x\"}]}",
        null)]
    [InlineData("{\"uri\":\"patchouli://texts/doc/page-1.xml\",\"content\":\"<page/>\"}", null)]
    [InlineData("{\"uri\":\"x\",\"content\":\"x\",\"boxes\":[{\"box_id\":\"b1\",\"content\":\"x\"}]}", "exactly one")]
    [InlineData("{\"uri\":\"x\",\"boxes\":[]}", "at least 1")]
    [InlineData("{\"uri\":\"x\",\"boxes\":[{\"content\":\"x\"}]}", "box_id is required")]
    [InlineData("{\"uri\":\"x\",\"boxes\":[{\"box_id\":\"a\",\"box_id\":\"b\",\"content\":\"x\"}]}",
        "Duplicate field 'box_id'")]
    [InlineData("{\"uri\":\"x\",\"boxes\":[{\"box_id\":\"a\",\"content\":\"x\",\"content\":\"y\"}]}",
        "Duplicate field 'content'")]
    public void Published_argument_validation_accepts_content_or_box_batch(string json, string? expectedError)
    {
        using JsonDocument arguments = JsonDocument.Parse(json);
        string? error = McpProtocolHandler.ValidateToolArguments("patchouli.put", arguments.RootElement);

        if (expectedError is null)
        {
            error.Should().BeNull();
        }
        else
        {
            error.Should().Contain(expectedError);
        }
    }


    [Fact]
    public void Published_argument_validation_caps_box_batches_at_one_hundred()
    {
        string boxes = string.Join(',', Enumerable.Repeat("{\"box_id\":\"b\",\"content\":\"x\"}", 101));
        using JsonDocument arguments = JsonDocument.Parse(
            "{\"uri\":\"patchouli://texts/doc/page-1.xml\",\"boxes\":[" + boxes + "]}");

        McpProtocolHandler.ValidateToolArguments("patchouli.put", arguments.RootElement)
            .Should().Contain("at most 100");
    }
}
