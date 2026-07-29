using FluentAssertions;

namespace Patchouli.Tests;

public sealed class ItemEditorIdentifierMarkupTests
{
    [Fact]
    public void Item_editor_exposes_a_free_text_identifier_scheme_input_and_profile_shortcuts()
    {
        string editorXaml = File.ReadAllText(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.UI", "Views", "ItemEditorPage.axaml"));

        editorXaml.Should().Contain("Text=\"{Binding IdentifierScheme}\"");
        editorXaml.Should().NotContain("ItemsSource=\"{Binding AvailableIdentifierSchemes}\"");
        editorXaml.Should().Contain("ItemsSource=\"{Binding IdentifierSchemeShortcuts}\"");
    }
}
