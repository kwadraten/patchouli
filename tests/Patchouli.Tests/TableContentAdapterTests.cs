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
}
