using System.Text.Json;
using FluentAssertions;
using Patchouli.Cli;

namespace Patchouli.Tests;

public sealed class CliPutBoxesTests
{
    [Fact]
    public void Boxes_stdin_maps_revision_and_batch_to_the_shared_put_arguments()
    {
        CliToolCall call = CliArguments.BuildToolCall("put",
            ["patchouli://texts/doc/page-1.xml", "--boxes-stdin"], false);

        call.PutBoxesStdin.Should().BeTrue();
        call = CliArguments.WithBoxesJson(call,
            "{\"source_revision\":\"rev-1\",\"translation_version\":3," +
            "\"boxes\":[{\"box_id\":\"b1\",\"content\":\"updated\"}]} ");

        call.Arguments["uri"].Should().Be("patchouli://texts/doc/page-1.xml");
        call.Arguments["source_revision"].Should().Be("rev-1");
        call.Arguments["translation_version"].Should().Be(3);
        call.Arguments["boxes"].Should().BeAssignableTo<object[]>();
        call.PutBoxesStdin.Should().BeFalse();
    }

    [Fact]
    public void Put_rejects_mixing_whole_content_and_box_batch_sources()
    {
        Action act = () => CliArguments.BuildToolCall("put",
            ["patchouli://texts/doc/page-1.xml", "--stdin", "--boxes-stdin"], false);

        act.Should().Throw<CliUsageException>();
    }

    [Fact]
    public void Boxes_file_preserves_source_xml_and_omits_translation_version()
    {
        const string xml = "<box id=\"b1\"><text>修订 &amp; updated</text></box>";
        CliToolCall call = CliArguments.BuildToolCall("put",
            ["texts/doc/page-1.xml", "--boxes-from", "batch.json"], true);
        call.PutBoxesSourcePath.Should().Be("batch.json");
        call = CliArguments.WithBoxesJson(call, JsonSerializer.Serialize(new
        {
            source_revision = "rev-1", boxes = new[] { new { box_id = "b1", content = xml } }
        }));

        call.Arguments["uri"].Should().Be("texts/doc/page-1.xml");
        call.Arguments["format"].Should().Be("json");
        call.Arguments.Should().NotContainKey("translation_version").And.NotContainKey("content");
        using JsonDocument arguments = JsonDocument.Parse(JsonSerializer.Serialize(call.Arguments));
        arguments.RootElement.GetProperty("boxes")[0].GetProperty("content").GetString().Should().Be(xml);
        call.PutBoxesSourcePath.Should().BeNull();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"source_revision\":\"rev\",\"boxes\":[]}")]
    [InlineData("{\"source_revision\":\"rev\",\"boxes\":[{\"content\":\"x\"}]}")]
    [InlineData(
        "{\"source_revision\":\"rev\",\"source_revision\":\"other\",\"boxes\":[{\"box_id\":\"b1\",\"content\":\"x\"}]}")]
    [InlineData("{\"source_revision\":\"rev\",\"boxes\":[{\"box_id\":\"b1\",\"content\":\"x\",\"unknown\":true}]}")]
    public void Boxes_input_requires_revision_and_nonempty_identified_items(string json)
    {
        CliToolCall call = CliArguments.BuildToolCall("put",
            ["patchouli://texts/doc/page-1.xml", "--boxes-stdin"], false);

        Action act = () => CliArguments.WithBoxesJson(call, json);
        act.Should().Throw<CliUsageException>();
    }
}
