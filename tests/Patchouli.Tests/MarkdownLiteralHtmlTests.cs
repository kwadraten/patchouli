using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Infrastructure.Documents;
using Patchouli.Reading;

namespace Patchouli.Tests;

public sealed class MarkdownLiteralHtmlTests
{
    [Theory]
    [InlineData("Option \"<option>\"", "Option \"&lt;option>\"")]
    [InlineData("<OPTION>A</OPTION>", "&lt;OPTION>A&lt;/OPTION>")]
    [InlineData("- **<option>**\n- <input>", "- **&lt;option>**\n- &lt;input>")]
    [InlineData("Use `<option>` literally.", "Use `<option>` literally.")]
    [InlineData("```html\n<option>A</option>\n```", "```html\n<option>A</option>\n```")]
    [InlineData("    <option>A</option>", "    <option>A</option>")]
    [InlineData("Already &lt;option&gt;", "Already &lt;option&gt;")]
    [InlineData("Escaped \\<option>", "Escaped \\<option>")]
    [InlineData("1. 2. 3. <option>", "1. 2. 3. &lt;option>")]
    public void Ocr_literal_escaping_preserves_markdown_and_is_idempotent(string source, string expected)
    {
        MarkdigMarkdownEngine engine = new();

        string escaped = engine.EscapeDangerousHtmlLiterals(source);

        escaped.Should().Be(expected);
        engine.EscapeDangerousHtmlLiterals(escaped).Should().Be(expected);
        engine.ValidateLeaf(DocumentBoxType.Text, new TextBoxPayload(escaped)).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("<option>A</option>")]
    [InlineData("Text <input value='x'>")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<iframe src='x'></iframe>")]
    public void Unescaped_active_html_is_still_rejected(string source)
    {
        MarkdigMarkdownEngine engine = new();

        engine.ValidateLeaf(DocumentBoxType.Text, new TextBoxPayload(source)).IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData("&lt;option&gt; &amp; &#x4E2D;", "<option> & 中")]
    [InlineData("&amp;lt;option&amp;gt;", "&lt;option&gt;")]
    [InlineData("`&lt;option&gt;`", "&lt;option&gt;")]
    public void Native_reading_inlines_decode_entities_once_and_preserve_code(string source, string expected)
    {
        MarkdigMarkdownEngine engine = new();

        MarkdownDocumentModel model = engine.Parse(source);
        ReadingScene scene = ReadingSceneBuilder.Build(model, [], [], "page");

        model.Blocks.Should().ContainSingle().Which.Text.Should().Be(expected);
        engine.ToPlainText(source).Should().Be(expected);
        scene.Blocks.Should().ContainSingle().Which.Text.Should().Be(expected);
        string.Concat(scene.Blocks.Single().Inlines!.Select(inline => inline.Text)).Should().Be(expected);
    }
}
