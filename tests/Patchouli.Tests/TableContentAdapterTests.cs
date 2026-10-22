using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Results;

namespace Patchouli.Tests;

public sealed class TableContentAdapterTests
{
    private const string SourceHtml = """
                                      <table><thead><tr><th rowspan="2"><strong>Metric</strong></th><th colspan="2">Value</th></tr><tr><th>2024</th><th>2025</th></tr></thead><tbody><tr><td><em>Revenue</em></td><td><eq>LaTex</eq></td><td>10</td></tr></tbody></table>
                                      """;

    [Fact]
    public void Valid_authoritative_html_keeps_merged_cells_schema_markup_and_eq_formula()
    {
        Result<TableBoxPayload> created = TableContentAdapter.CreateHtml(SourceHtml);

        created.IsSuccess.Should().BeTrue(created.ErrorMessage);
        created.Value.ContentFormat.Should().Be(TableContentFormat.Html);
        created.Value.ContentFormatVersion.Should().Be(TableContentFormat.AuthoritativeHtmlVersion);
        TableContentAdapter.IsHtml(created.Value).Should().BeTrue();
        TableContentAdapter.TryGetHtml(created.Value, out string html).Should().BeTrue();
        html.Should().Be(SourceHtml);
        TableContentAdapter.GetPlainText(created.Value).Should().Contain("Metric").And.Contain("Revenue")
            .And.Contain("LaTex");

        Result<TableBoxPayload> translated = TableContentAdapter.CreateHtml(SourceHtml
            .Replace("Metric", "Indicateur", StringComparison.Ordinal)
            .Replace("Value", "Valeur", StringComparison.Ordinal)
            .Replace("Revenue", "Chiffre d’affaires", StringComparison.Ordinal));

        translated.IsSuccess.Should().BeTrue(translated.ErrorMessage);
        Result<TableBoxPayload> validated = TableContentAdapter.ValidateTranslation(created.Value, translated.Value);
        validated.IsSuccess.Should().BeTrue(validated.ErrorMessage);
        TableContentAdapter.TryGetHtml(validated.Value, out string translatedHtml).Should().BeTrue();
        translatedHtml.Should().Contain("rowspan=\"2\"").And.Contain("colspan=\"2\"")
            .And.Contain("<strong>Indicateur</strong>").And.Contain("<em>Chiffre d’affaires</em>")
            .And.Contain("<eq>LaTex</eq>");
    }

    [Fact]
    public void Translation_preserves_verified_mineru_eq_formula_subtree()
    {
        const string sourceHtml = "<table><tr><td>Equation</td><td><eq>LaTex</eq></td></tr></table>";
        TableBoxPayload source = TableContentAdapter.CreateHtml(sourceHtml).Value;
        TableBoxPayload translated = TableContentAdapter.CreateHtml(
            sourceHtml.Replace("Equation", "Gleichung", StringComparison.Ordinal)).Value;

        Result<TableBoxPayload> accepted = TableContentAdapter.ValidateTranslation(source, translated);

        accepted.IsSuccess.Should().BeTrue(accepted.ErrorMessage);
        TableContentAdapter.TryGetHtml(accepted.Value, out string acceptedHtml).Should().BeTrue();
        acceptedHtml.Should().Contain("<eq>LaTex</eq>");

        TableBoxPayload changedFormula = TableContentAdapter.CreateHtml(
            sourceHtml.Replace("LaTex", "different", StringComparison.Ordinal)).Value;
        TableContentAdapter.ValidateTranslation(source, changedFormula).IsFailure.Should().BeTrue();

        TableBoxPayload missingFormula = TableContentAdapter.CreateHtml(
            sourceHtml.Replace("<eq>LaTex</eq>", "LaTex", StringComparison.Ordinal)).Value;
        TableContentAdapter.ValidateTranslation(source, missingFormula).IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData("rowspan=\"2\"", "rowspan=\"3\"")]
    [InlineData("colspan=\"2\"", "colspan=\"1\"")]
    [InlineData("<em>Revenue</em>", "Revenue")]
    [InlineData("<eq>LaTex</eq>", "<eq>Different</eq>")]
    [InlineData("<td><eq>LaTex</eq></td>", "<td>LaTex</td>")]
    public void Translation_rejects_lost_or_changed_table_structure_and_protected_content(
        string original, string changed)
    {
        TableBoxPayload source = TableContentAdapter.CreateHtml(SourceHtml).Value;
        Result<TableBoxPayload> candidate = TableContentAdapter.CreateHtml(
            SourceHtml.Replace(original, changed, StringComparison.Ordinal));

        candidate.IsSuccess.Should().BeTrue(candidate.ErrorMessage);
        Result<TableBoxPayload> validated = TableContentAdapter.ValidateTranslation(source, candidate.Value);

        validated.IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData("<table><tr><td onclick=\"alert(1)\">value</td></tr></table>")]
    [InlineData("<table><tr><td><script>alert(1)</script></td></tr></table>")]
    [InlineData("<table><tr><td><a href=\"javascript:alert(1)\">value</a></td></tr></table>")]
    public void Unsupported_or_unsafe_html_is_rejected(string html)
    {
        Result<TableBoxPayload> result = TableContentAdapter.CreateHtml(html);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Legacy_placeholder_and_authoritative_html_have_the_same_semantic_fingerprint()
    {
        TableBoxPayload legacy = new("[Table]", SourceHtml);
        TableBoxPayload authoritative = TableContentAdapter.CreateHtml(SourceHtml).Value;

        TableContentAdapter.GetSemanticFingerprint(legacy)
            .Should().Be(TableContentAdapter.GetSemanticFingerprint(authoritative));
    }

    [Fact]
    public void Editor_load_roundtrip_requires_unchanged_semantic_content_and_structure()
    {
        TableBoxPayload source = TableContentAdapter.CreateHtml(SourceHtml).Value;

        TableContentAdapter.IsLosslessEditorRoundtrip(source, SourceHtml).Should().BeTrue();
        TableContentAdapter.IsLosslessEditorRoundtrip(source,
            SourceHtml.Replace("rowspan=\"2\"", "rowspan=\"1\"", StringComparison.Ordinal)).Should().BeFalse();
        TableContentAdapter.IsLosslessEditorRoundtrip(source,
            SourceHtml.Replace("Metric", "Titre", StringComparison.Ordinal)).Should().BeFalse();
    }

    [Fact]
    public void Editor_normalization_handles_observed_empty_paragraphs_colgroup_and_header_cells()
    {
        const string sourceHtml = "<table><tbody><tr><th rowspan=\"2\">A</th><th colspan=\"2\">B</th>" +
                                  "</tr><tr><td>C</td><td>D</td></tr></tbody></table>";
        const string editorHtml = """
                                  <p data-are-empty="1" style="text-align:left;"><br></p>
                                  <table border="1" style="border-collapse:collapse; width:100%;">
                                  <colgroup><col style="width:100px"/><col style="width:100px"/><col style="width:100px"/></colgroup>
                                  <tr>
                                  <td rowspan="2">A</td>
                                  <td colspan="2">B</td>
                                  </tr>
                                  <tr>
                                  <td>C</td>
                                  <td>D</td>
                                  </tr>
                                  </table>
                                  <p data-are-empty="1" style="text-align:left;"><br></p>
                                  """;
        TableBoxPayload source = TableContentAdapter.CreateHtml(sourceHtml).Value;

        TableContentAdapter.TryNormalizeEditorHtml(source, editorHtml, out string normalized).Should().BeTrue();

        normalized.Should().Contain("<th rowspan=\"2\">A</th>")
            .And.Contain("<th colspan=\"2\">B</th>")
            .And.NotContain("data-are-empty")
            .And.NotContain("colgroup");
        TableContentAdapter.IsLosslessEditorRoundtrip(source, editorHtml).Should().BeTrue();
    }

    [Fact]
    public void Editor_normalization_restores_only_unchanged_mineru_eq_formula_cells()
    {
        const string sourceHtml = "<table><tr><td>Label</td><td><eq>x^2</eq></td></tr></table>";
        const string editorHtml = """
                                  <p data-are-empty="1" style="text-align:left;"><br></p>
                                  <table border="1" style="border-collapse:collapse; width:100%;">
                                  <colgroup><col style="width:100px"/><col style="width:100px"/></colgroup>
                                  <tr>
                                  <td>Label</td>
                                  <td>x^2</td>
                                  </tr>
                                  </table>
                                  <p data-are-empty="1" style="text-align:left;"><br></p>
                                  """;
        TableBoxPayload source = TableContentAdapter.CreateHtml(sourceHtml).Value;

        TableContentAdapter.TryNormalizeEditorHtml(source, editorHtml, out string normalized).Should().BeTrue();
        normalized.Should().Contain("<eq>x^2</eq>");
        TableContentAdapter.IsLosslessEditorRoundtrip(source, editorHtml).Should().BeTrue();

        const string editedNeighbor = """
                                      <p data-are-empty="1" style="text-align:left;"><br></p>
                                      <table border="1" style="border-collapse:collapse; width:100%;">
                                      <colgroup><col style="width:100px"/><col style="width:100px"/></colgroup>
                                      <tr>
                                      <td>Label translated</td>
                                      <td>x^2</td>
                                      </tr>
                                      </table>
                                      <p data-are-empty="1" style="text-align:left;"><br></p>
                                      """;
        TableContentAdapter.TryNormalizeEditorHtml(source, editedNeighbor, out string editedNormalized)
            .Should().BeTrue();
        editedNormalized.Should().Contain("Label translated").And.Contain("<eq>x^2</eq>");
        TableContentAdapter.IsLosslessEditorRoundtrip(source, editedNeighbor).Should().BeFalse();

        const string changedFormula = """
                                      <p data-are-empty="1" style="text-align:left;"><br></p>
                                      <table border="1" style="border-collapse:collapse; width:100%;">
                                      <colgroup><col style="width:100px"/><col style="width:100px"/></colgroup>
                                      <tr>
                                      <td>Label</td>
                                      <td>x^3</td>
                                      </tr>
                                      </table>
                                      <p data-are-empty="1" style="text-align:left;"><br></p>
                                      """;
        TableContentAdapter.TryNormalizeEditorHtml(source, changedFormula, out _).Should().BeFalse();
    }

    [Fact]
    public void Editor_save_ignores_presentation_attribute_changes_instead_of_blocking()
    {
        const string sourceHtml = "<table><tr><td>Label</td><td>1</td></tr></table>";
        const string editorHtml = """
                                  <table border="1" style="border-collapse:collapse; width:100%; margin:0;">
                                  <tr>
                                  <td data-custom="1" style="font-family:Arial; color:red;">Label</td>
                                  <td>1</td>
                                  </tr>
                                  </table>
                                  """;
        TableBoxPayload source = TableContentAdapter.CreateHtml(sourceHtml).Value;

        Result<string> saved = TableContentAdapter.TryCreateEditorSaveHtml(source, editorHtml);

        saved.IsSuccess.Should().BeTrue(saved.ErrorMessage);
        TableContentAdapter.ValidateHtml(saved.Value).IsSuccess.Should().BeTrue();
        saved.Value.Should().Contain("color:red").And.Contain("Label")
            .And.NotContain("font-family").And.NotContain("data-custom").And.NotContain("margin");
    }

    [Fact]
    public void Editor_save_keeps_row_column_and_span_structure_edits()
    {
        const string sourceHtml = "<table><tr><td>A</td><td>B</td></tr></table>";
        TableBoxPayload source = TableContentAdapter.CreateHtml(sourceHtml).Value;

        Result<string> addedRow = TableContentAdapter.TryCreateEditorSaveHtml(source,
            "<table><tr><td>A</td><td>B</td></tr><tr><td>C</td><td>D</td></tr></table>");
        addedRow.IsSuccess.Should().BeTrue(addedRow.ErrorMessage);
        addedRow.Value.Split("<tr>").Length.Should().Be(3);

        Result<string> removedColumn = TableContentAdapter.TryCreateEditorSaveHtml(source,
            "<table><tr><td>A</td></tr></table>");
        removedColumn.IsSuccess.Should().BeTrue(removedColumn.ErrorMessage);
        removedColumn.Value.Should().Contain("<td>A</td>").And.NotContain("<td>B</td>");

        Result<string> merged = TableContentAdapter.TryCreateEditorSaveHtml(source,
            "<table><tr><td colspan=\"2\">A</td></tr></table>");
        merged.IsSuccess.Should().BeTrue(merged.ErrorMessage);
        merged.Value.Should().Contain("colspan=\"2\"");
        TableContentAdapter.ValidateHtml(merged.Value).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Editor_save_restores_header_cells_after_structural_edits()
    {
        const string sourceHtml = "<table><tr><th>Name</th><th>Value</th></tr><tr><td>A</td><td>1</td></tr></table>";
        TableBoxPayload source = TableContentAdapter.CreateHtml(sourceHtml).Value;

        Result<string> saved = TableContentAdapter.TryCreateEditorSaveHtml(source,
            "<table><tr><td>Name</td><td>Value</td></tr><tr><td>A</td><td>1</td></tr>" +
            "<tr><td>B</td><td>2</td></tr></table>");

        saved.IsSuccess.Should().BeTrue(saved.ErrorMessage);
        saved.Value.Should().Contain("<th>Name</th>").And.Contain("<th>Value</th>");
    }

    [Fact]
    public void Editor_save_keeps_protected_formula_cells_by_content_match_after_structural_edits()
    {
        const string sourceHtml = "<table><tr><td>Label</td><td><eq>x^2</eq></td></tr></table>";
        TableBoxPayload source = TableContentAdapter.CreateHtml(sourceHtml).Value;

        Result<string> saved = TableContentAdapter.TryCreateEditorSaveHtml(source,
            "<table><tr><td>Label</td><td>x^2</td></tr><tr><td>New</td><td>1</td></tr></table>");

        saved.IsSuccess.Should().BeTrue(saved.ErrorMessage);
        saved.Value.Should().Contain("<eq>x^2</eq>").And.Contain("<td>New</td>");
        TableContentAdapter.ValidateHtml(saved.Value).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Editor_save_rejects_modified_or_removed_protected_formula_cells()
    {
        const string sourceHtml = "<table><tr><td>Label</td><td><eq>x^2</eq></td></tr></table>";
        TableBoxPayload source = TableContentAdapter.CreateHtml(sourceHtml).Value;

        Result<string> modified = TableContentAdapter.TryCreateEditorSaveHtml(source,
            "<table><tr><td>Label</td><td>x^3</td></tr></table>");
        modified.IsFailure.Should().BeTrue();
        modified.ErrorMessage.Should().Contain("公式");

        Result<string> removed = TableContentAdapter.TryCreateEditorSaveHtml(source,
            "<table><tr><td>Label</td><td>other</td></tr></table>");
        removed.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Editor_save_keeps_user_formatted_block_content_inside_cells()
    {
        const string sourceHtml = "<table><tr><td>A</td><td>B</td></tr></table>";
        TableBoxPayload source = TableContentAdapter.CreateHtml(sourceHtml).Value;

        Result<string> multiParagraph = TableContentAdapter.TryCreateEditorSaveHtml(source,
            "<table><tr><td><p>line one</p><p>line two</p></td><td>B</td></tr></table>");
        multiParagraph.IsSuccess.Should().BeTrue(multiParagraph.ErrorMessage);
        multiParagraph.Value.Should().Contain("<p>line one</p>").And.Contain("<p>line two</p>");

        Result<string> centered = TableContentAdapter.TryCreateEditorSaveHtml(source,
            "<table><tr><td><p style=\"text-align:center\">A</p></td><td>B</td></tr></table>");
        centered.IsSuccess.Should().BeTrue(centered.ErrorMessage);
        centered.Value.Should().Contain("text-align:center");
    }

    [Fact]
    public void Editor_save_rejects_content_outside_the_table()
    {
        const string sourceHtml = "<table><tr><td>A</td></tr></table>";
        TableBoxPayload source = TableContentAdapter.CreateHtml(sourceHtml).Value;

        TableContentAdapter.TryCreateEditorSaveHtml(source,
            "<p>extra</p><table><tr><td>A</td></tr></table>").IsFailure.Should().BeTrue();
        TableContentAdapter.TryCreateEditorSaveHtml(source,
            "<table><tr><td>A</td></tr></table><table><tr><td>B</td></tr></table>").IsFailure.Should().BeTrue();
    }
}
