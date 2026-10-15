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

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"source_revision\":\"rev\",\"boxes\":[]}")]
    [InlineData("{\"source_revision\":\"rev\",\"boxes\":[{\"content\":\"x\"}]}")]
    public void Boxes_input_requires_revision_and_nonempty_identified_items(string json)
    {
        CliToolCall call = CliArguments.BuildToolCall("put",
            ["patchouli://texts/doc/page-1.xml", "--boxes-stdin"], false);

        Action act = () => CliArguments.WithBoxesJson(call, json);
        act.Should().Throw<CliUsageException>();
    }
}
