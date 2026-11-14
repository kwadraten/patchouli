using System.Xml.Linq;
using FluentAssertions;
using Patchouli.Core.Mcp;

namespace Patchouli.Tests;

/// <summary>
/// Guards the Agent 资源访问权限 permission matrix in SettingsPage.axaml: the removed MCP
/// "view" verb must not reappear as a stale eighth header column, and the header/row grids,
/// UniformGrid and minimum width must stay sized for exactly the six policy verbs.
/// </summary>
public sealed class McpPermissionMatrixLayoutTests
{
    private const string SixVerbColumns = "160,64,64,64,64,64,64";

    private static readonly string AxamlPath =
        TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "SettingsPage.axaml");

    private static XElement MatrixRoot()
    {
        return XDocument.Load(AxamlPath).Descendants()
            .Single(element => element.Name.LocalName == "SettingsCard" &&
                               (string?)element.Attribute("AnchorId") == "permissions_resource");
    }

    [Fact]
    public void Permission_rows_have_six_cells_in_policy_verb_order()
    {
        // McpDomainPermissionRowViewModel.Cells is projected from McpPermissionPolicy.Verbs.
        McpPermissionPolicy.Verbs.Should().Equal("find", "fetch", "put", "cite", "send", "add");
        McpPermissionPolicy.Verbs.Should().NotContain("view");
    }

    [Fact]
    public void Permission_matrix_header_lists_exactly_the_policy_verbs()
    {
        string[] headers = MatrixRoot().Descendants()
            .Single(element => element.Name.LocalName == "Grid" &&
                               element.Elements().FirstOrDefault() is { } first &&
                               first.Name.LocalName == "TextBlock" &&
                               (string?)first.Attribute("Text") == "VFS 域")
            .Elements()
            .Select(element => (string?)element.Attribute("Text"))
            .Where(text => text is not null && text != "VFS 域")
            .OfType<string>()
            .ToArray();

        headers.Should().Equal(McpPermissionPolicy.Verbs,
            "each header column must map 1:1 onto a permission cell");
    }

    [Fact]
    public void Runs_expander_uses_lucide_chevrons_instead_of_text_glyphs()
    {
        XElement toggle = MatrixRoot().Descendants()
            .Single(element => element.Name.LocalName == "ToggleButton" &&
                               (string?)element.Attribute("IsVisible") == "{Binding HasChildren}");

        toggle.Attribute("Content").Should().BeNull("the expander must not render a font-dependent glyph");
        toggle.Attribute("Width")!.Value.Should().Be("20");
        toggle.Attribute("Height")!.Value.Should().Be("22");
        toggle.Attributes().Single(attribute => attribute.Name.LocalName == "AutomationProperties.Name")
            .Value.Should().Be("展开 runs 子域权限");

        XElement[] icons = toggle.Descendants().Where(element => element.Name.LocalName == "Lucide").ToArray();
        icons.Select(icon => ((string?)icon.Attribute("Icon"), (string?)icon.Attribute("IsVisible")))
            .Should().Equal(("ChevronRight", "{Binding !IsExpanded}"), ("ChevronDown", "{Binding IsExpanded}"));
        icons.Should().OnlyContain(icon => (string?)icon.Attribute("Width") == "14" &&
                                           (string?)icon.Attribute("Height") == "14");

        string xaml = File.ReadAllText(AxamlPath);
        xaml.Should().NotContain("▶").And.NotContain("▼").And.NotContain("ExpansionGlyph");
    }

    [Fact]
    public void Permission_matrix_layout_is_sized_for_six_columns()
    {
        XElement root = MatrixRoot();

        root.Descendants().Count(element =>
                element.Name.LocalName == "Grid" &&
                (string?)element.Attribute("ColumnDefinitions") == SixVerbColumns)
            .Should().Be(2, "the header grid and the runs child grid share the six-verb layout");

        root.Descendants().Should().Contain(element =>
            element.Name.LocalName == "Grid" &&
            (string?)element.Attribute("ColumnDefinitions") == "160,384");

        root.Descendants().Single(element => element.Name.LocalName == "UniformGrid")
            .Attribute("Columns")!.Value.Should().Be("6");

        root.Descendants().Single(element =>
                element.Name.LocalName == "StackPanel" && element.Attribute("MinWidth") is not null)
            .Attribute("MinWidth")!.Value.Should().Be("544");

        root.Descendants().Should().NotContain(element =>
            (string?)element.Attribute("Text") == "view" ||
            (string?)element.Attribute("ColumnDefinitions") == "160,64,64,64,64,64,64,64" ||
            (string?)element.Attribute("ColumnDefinitions") == "160,448");
    }
}
