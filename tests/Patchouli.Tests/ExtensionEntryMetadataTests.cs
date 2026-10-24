using FluentAssertions;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Tests;

public sealed class ExtensionEntryMetadataTests
{
    [Fact]
    public void Parse_reads_only_documentation_attached_to_exported_entry()
    {
        ExtensionPackageSnapshot package = Package(""""
                                                   let fake = "/// <patchouli-extension id=\"fake\" />"
                                                   let multiline = $"""
                                                   /// <patchouli-extension id="interpolated" />
                                                   """
                                                   let raw = """
                                                   /// <patchouli-extension id="triple" />
                                                   """
                                                   let nested =
                                                       let extension = "not the top-level export"
                                                   (* /// <patchouli-extension id="comment" /> *)
                                                   /// <summary>Readable &amp; safe.</summary>
                                                   /// <patchouli-extension id="test.parser" kind="metadata" version="1.2.3" host-api="patchouli.extensions/3">
                                                   ///   <name>Parser</name>
                                                   /// </patchouli-extension>
                                                   let extension : IMetadataExtension = failwith "not evaluated"
                                                   """");

        ExtensionEntryMetadata metadata = ExtensionEntryMetadata.Parse(package);

        metadata.Id.Should().Be("test.parser");
        metadata.Kind.Should().Be(ExtensionKind.Metadata);
        metadata.Name.Should().Be("Parser");
        metadata.Summary.Should().Be("Readable & safe.");
    }

    [Fact]
    public void RewriteId_changes_only_metadata_attribute()
    {
        const string source = "let sample = \"test.parser\"\n" +
                              "/// <summary>id=\"test.parser\" must remain text</summary>\n" +
                              "/// <patchouli-extension\n" +
                              "/// id = 'test&#46;parser' kind='translation' version='1.0.0'\n" +
                              "/// host-api='patchouli.extensions/3'>\n" +
                              "/// <name>Parser</name>\n/// </patchouli-extension>\nlet extension = failwith \"test.parser\"";
        ExtensionPackageSnapshot updated = ExtensionEntryMetadata.RewriteId(Package(source), "test.copy");

        ExtensionEntryMetadata.Parse(updated).Id.Should().Be("test.copy");
        updated.Sources["main.fsx"].Should().Contain("id = 'test.copy'");
        updated.Sources["main.fsx"].Should().Contain("id=\"test.parser\" must remain text");
        updated.Sources["main.fsx"].Should().Contain("let sample = \"test.parser\"");
        updated.Sources["main.fsx"].Should().Contain("failwith \"test.parser\"");
    }

    [Fact]
    public void RewriteId_uses_parsed_direct_child_not_tag_lookalikes_in_summary()
    {
        const string source =
            "/// <summary><![CDATA[<patchouli-extension id='test.parser' kind='fake'>]]></summary>\n" +
            "/// <!-- <patchouli-extension id='test.parser' kind='fake'> -->\n" +
            "/// <patchouli-extension\n" +
            "/// id = 'test&#46;parser' kind='translation' version='1.0.0'\n" +
            "/// host-api='patchouli.extensions/3'>\n" +
            "/// <name>Parser</name>\n/// </patchouli-extension>\nlet extension = 1";

        ExtensionPackageSnapshot updated = ExtensionEntryMetadata.RewriteId(Package(source), "test.copy");
        string rewritten = updated.Sources["main.fsx"];

        ExtensionEntryMetadata.Parse(updated).Id.Should().Be("test.copy");
        rewritten.Should().Contain("CDATA[<patchouli-extension id='test.parser'");
        rewritten.Should().Contain("<!-- <patchouli-extension id='test.parser'");
        rewritten.Should().Contain("id = 'test.copy'");
    }

    [Fact]
    public void Parse_rejects_multiple_top_level_exports()
    {
        const string source = "/// <summary>First.</summary>\n" +
                              "/// <patchouli-extension id=\"test.parser\" kind=\"translation\" version=\"1.0.0\" host-api=\"patchouli.extensions/3\"><name>Parser</name></patchouli-extension>\n" +
                              "let extension = 1\nlet extension = 2";

        Action parse = () => ExtensionEntryMetadata.Parse(Package(source));

        parse.Should().Throw<ExtensionMetadataException>().WithMessage("*exactly one top-level*");
    }

    [Fact]
    public void Parse_rejects_a_package_targeting_the_v2_host_api()
    {
        const string source = "/// <summary>Summary.</summary>\n" +
                              "/// <patchouli-extension id=\"test.parser\" kind=\"translation\" version=\"1.0.0\" host-api=\"patchouli.extensions/2\">\n" +
                              "/// <name>Parser</name>\n/// </patchouli-extension>\nlet extension = 1";

        Action parse = () => ExtensionEntryMetadata.Parse(Package(source));

        parse.Should().Throw<ExtensionMetadataException>().WithMessage("*unsupported*host API*");
    }

    [Fact]
    public void Parse_maps_xml_errors_to_original_main_source_line()
    {
        string source = "let value = 1\n" +
                        "/// <summary>Summary.</summary>\n" +
                        "/// <patchouli-extension id=\"test.parser\" kind=\"translation\" version=\"1.0.0\" host-api=\"patchouli.extensions/3\">\n" +
                        "/// <name>bad & invalid</name>\n" +
                        "/// </patchouli-extension>\nlet extension = 1";

        Action parse = () => ExtensionEntryMetadata.Parse(Package(source));

        parse.Should().Throw<ExtensionMetadataException>().Which.Line.Should().Be(4);
    }

    [Fact]
    public void Parse_maps_invalid_kind_to_its_attribute_line()
    {
        string source = "/// <summary>Summary.</summary>\n" +
                        "/// <patchouli-extension id=\"test.parser\"\n" +
                        "/// kind=\"unknown\" version=\"1.0.0\" host-api=\"patchouli.extensions/3\">\n" +
                        "/// <name>Parser</name>\n/// </patchouli-extension>\nlet extension = 1";

        Action parse = () => ExtensionEntryMetadata.Parse(Package(source));

        parse.Should().Throw<ExtensionMetadataException>().Which.Line.Should().Be(3);
    }

    [Fact]
    public void Parse_accepts_the_existing_retrieval_kind()
    {
        string source = "/// <summary>Summary.</summary>\n" +
                        "/// <patchouli-extension id=\"test.parser\"\n" +
                        "/// kind=\"retrieval\" version=\"1.0.0\" host-api=\"patchouli.extensions/3\">\n" +
                        "/// <name>Parser</name>\n/// </patchouli-extension>\nlet extension = 1";

        ExtensionEntryMetadata.Parse(Package(source)).Kind.Should().Be(ExtensionKind.Retrieval);
    }

    [Theory]
    [InlineData("dtd")]
    [InlineData("size")]
    [InlineData("depth")]
    public void Parse_rejects_external_entities_and_resource_exhaustion(string variant)
    {
        string prefix = variant == "dtd"
            ? "/// <!DOCTYPE x [<!ENTITY external SYSTEM 'https://unit.test/external.dtd'>]>\n"
            : "";
        string summary = variant == "size" ? new string('x', 17_000) : "Summary";
        string nested = variant == "depth"
            ? string.Concat(Enumerable.Repeat("<node>", 20)) + "value" + string.Concat(Enumerable.Repeat("</node>", 20))
            : "";
        string source = prefix + $"/// <summary>{summary}</summary>\n" +
                        "/// <patchouli-extension id='test.parser' kind='translation' version='1.0.0' host-api='patchouli.extensions/3'>\n" +
                        $"/// <name>Parser</name><remarks>{nested}</remarks></patchouli-extension>\nlet extension = 1";

        Action parse = () => ExtensionEntryMetadata.Parse(Package(source));

        parse.Should().Throw<ExtensionMetadataException>().Which.Code.Should().Be("EXT0006");
    }

    [Theory]
    [InlineData("extension.yml", "EXT0014")]
    [InlineData(
        "/// <patchouli-extension id=\"test.parser\" kind=\"unknown\" version=\"1.0.0\" host-api=\"patchouli.extensions/3\"><name>x</name></patchouli-extension>\nlet extension = 1",
        "EXT0006")]
    public void Parse_rejects_legacy_or_unsafe_package_metadata(string input, string expectedCode)
    {
        ExtensionPackageSnapshot package = input == "extension.yml"
            ? new ExtensionPackageSnapshot("test.parser", new Dictionary<string, string>
                { ["extension.yml"] = "old", ["main.fsx"] = "let extension = 1" })
            : Package(input);

        Action parse = () => ExtensionEntryMetadata.Parse(package);

        parse.Should().Throw<ExtensionMetadataException>().Which.Code.Should().Be(expectedCode);
    }

    private static ExtensionPackageSnapshot Package(string source)
    {
        return new ExtensionPackageSnapshot("test.parser", new Dictionary<string, string> { ["main.fsx"] = source });
    }
}
