using FluentAssertions;
using Patchouli.Core.Csl;
using Patchouli.Core.Results;

namespace Patchouli.Tests;

/// <summary>Issue #11: pure extraction of style id, title and dependencies from CSL XML.</summary>
public sealed class CslStyleXmlParserTests
{
    private const string ValidStyle =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <style xmlns="http://purl.org/net/xbiblio/csl" class="in-text" version="1.0" default-locale="en-US">
          <info>
            <title>Test Style</title>
            <id>http://www.zotero.org/styles/test-style</id>
            <link href="http://www.zotero.org/styles/apa" rel="independent-parent"/>
            <link href="http://www.zotero.org/styles/apa" rel="template"/>
          </info>
          <citation><layout/></citation>
        </style>
        """;

    [Fact]
    public void Parse_extracts_identity_dependencies_and_parent()
    {
        Result<CslLocalStyleInfo> result = CslStyleXmlParser.Parse(ValidStyle);

        result.IsSuccess.Should().BeTrue();
        result.Value.StyleId.Should().Be("test-style");
        result.Value.Title.Should().Be("Test Style");
        result.Value.DefaultLocale.Should().Be("en-US");
        result.Value.IndependentParentId.Should().Be("apa");
        result.Value.Dependencies.Should().ContainSingle().Which.Should().Be("apa");
    }

    [Fact]
    public void Parse_rejects_empty_content()
    {
        CslStyleXmlParser.Parse("").IsSuccess.Should().BeFalse();
        CslStyleXmlParser.Parse("   ").IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Parse_rejects_malformed_xml()
    {
        Result<CslLocalStyleInfo> result = CslStyleXmlParser.Parse("<style><info>");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public void Parse_rejects_non_style_root()
    {
        Result<CslLocalStyleInfo> result = CslStyleXmlParser.Parse("<html><body/></html>");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Parse_rejects_missing_info_section()
    {
        Result<CslLocalStyleInfo> result =
            CslStyleXmlParser.Parse("<style xmlns=\"http://purl.org/net/xbiblio/csl\"/>");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Parse_rejects_missing_id()
    {
        Result<CslLocalStyleInfo> result = CslStyleXmlParser.Parse(
            "<style><info><title>Nameless</title></info></style>");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Parse_rejects_missing_title()
    {
        Result<CslLocalStyleInfo> result = CslStyleXmlParser.Parse(
            "<style><info><id>http://example.org/styles/x</id></info></style>");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Parse_prohibits_dtd_instead_of_fetching_external_entities()
    {
        string xml =
            """
            <!DOCTYPE style [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <style><info><id>&xxe;</id><title>t</title></info></style>
            """;

        Result<CslLocalStyleInfo> result = CslStyleXmlParser.Parse(xml);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Parse_style_without_links_has_no_dependencies()
    {
        string xml =
            "<style><info><title>Plain</title><id>http://example.org/styles/plain</id></info></style>";

        Result<CslLocalStyleInfo> result = CslStyleXmlParser.Parse(xml);

        result.IsSuccess.Should().BeTrue();
        result.Value.StyleId.Should().Be("plain");
        result.Value.Dependencies.Should().BeEmpty();
        result.Value.IndependentParentId.Should().BeNull();
        result.Value.DefaultLocale.Should().BeNull();
    }

    [Fact]
    public void Parse_deduplicates_template_links_pointing_at_same_style()
    {
        string xml =
            """
            <style><info><title>D</title><id>http://example.org/styles/d</id>
            <link href="http://example.org/styles/base" rel="template"/>
            <link href="http://example.org/styles/base" rel="template"/>
            </info></style>
            """;

        Result<CslLocalStyleInfo> result = CslStyleXmlParser.Parse(xml);

        result.IsSuccess.Should().BeTrue();
        result.Value.Dependencies.Should().ContainSingle().Which.Should().Be("base");
    }

    [Theory]
    [InlineData("http://www.zotero.org/styles/apa", "apa")]
    [InlineData("http://www.zotero.org/styles/apa/", "apa")]
    [InlineData("apa", "apa")]
    [InlineData("http://example.org/styles/x#frag", "x")]
    public void ShortId_derives_portable_id(string input, string expected)
    {
        CslStyleXmlParser.ShortId(input).Should().Be(expected);
    }
}
