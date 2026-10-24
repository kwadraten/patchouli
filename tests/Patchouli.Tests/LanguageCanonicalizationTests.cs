using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Translation;
using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Translation;

namespace Patchouli.Tests;

/// <summary>
/// Independent verification of the language canonicalization boundary: the host canonical form is
/// the ItemLanguage script-tag style (zh-Hans), region-style Chinese tags unify onto it, the
/// auto sentinel survives, and every declared language surface (job tags, translation settings,
/// extension config fields) stores the canonical value rather than raw user text.
/// </summary>
public sealed class LanguageCanonicalizationTests
{
    [Theory]
    // Region-style Chinese tags unify onto the host script tags.
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("zh-MO", "zh-Hant")]
    // Casing and separators are normalized before unification.
    [InlineData("zh-cn", "zh-Hans")]
    [InlineData("ZH-TW", "zh-Hant")]
    [InlineData("zh_hk", "zh-Hant")]
    // Host aliases land on the same script tags.
    [InlineData("Chinese-Simplified", "zh-Hans")]
    [InlineData("Chinese-Traditional", "zh-Hant")]
    [InlineData("Chinese", "zh")]
    // Canonical values are fixed points.
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("zh-Hant", "zh-Hant")]
    [InlineData("zh", "zh")]
    // The auto sentinel is lowercased, never treated as a tag.
    [InlineData("auto", "auto")]
    [InlineData("AUTO", "auto")]
    [InlineData("Auto", "auto")]
    [InlineData(" auto ", "auto")]
    // Non-Chinese tags get BCP-47 normalization only, never a rewrite.
    [InlineData("en-US", "en-US")]
    [InlineData("EN-us", "en-US")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("ja", "ja")]
    [InlineData("fr", "fr")]
    public void Canonicalize_unifies_chinese_region_tags_and_preserves_other_tags(string input, string expected)
    {
        ItemLanguage.Canonicalize(input).Should().Be(expected);
    }

    [Fact]
    public void Canonicalize_blank_input_is_null()
    {
        ItemLanguage.Canonicalize(null).Should().BeNull();
        ItemLanguage.Canonicalize("").Should().BeNull();
        ItemLanguage.Canonicalize("   ").Should().BeNull();
    }

    [Fact]
    public void Canonicalize_is_idempotent()
    {
        foreach (string input in new[]
                     { "zh-CN", "zh-TW", "zh-HK", "zh-Hans", "zh-Hant", "Chinese-Simplified", "AUTO", "en-US", "ja" })
        {
            string? once = ItemLanguage.Canonicalize(input);
            ItemLanguage.Canonicalize(once).Should().Be(once, "canonicalizing a canonical value changes nothing");
        }
    }

    [Theory]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("Chinese-Simplified", "zh-Hans")]
    [InlineData("EN-us", "en-US")]
    public void LanguageTag_create_stores_the_canonical_value(string input, string expected)
    {
        LanguageTag.Create(input).Value.Should().Be(expected);
    }

    [Fact]
    public void LanguageTag_equates_raw_forms_of_the_same_canonical_tag()
    {
        LanguageTag.Create("zh-CN").Should().Be(LanguageTag.Create("zh-Hans"));
        LanguageTag.Create("zh-TW").Should().Be(LanguageTag.Create("zh-Hant"));
        LanguageTag.Create("zh-HK").Should().Be(LanguageTag.Create("zh-Hant"));
        LanguageTag.Create("zh-CN").Should().NotBe(LanguageTag.Create("zh-Hant"));
    }

    [Fact]
    public void LanguageTag_blank_input_is_unspecified()
    {
        foreach (string? input in new string?[] { null, "", "   " })
        {
            LanguageTag tag = LanguageTag.Create(input);

            tag.IsUnspecified.Should().BeTrue();
            tag.Value.Should().Be("");
            tag.Should().Be(LanguageTag.Unspecified);
        }
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("Auto")]
    public void LanguageTag_auto_variants_are_the_auto_sentinel(string input)
    {
        LanguageTag tag = LanguageTag.Create(input);

        tag.IsAuto.Should().BeTrue();
        tag.IsUnspecified.Should().BeFalse();
        tag.Should().Be(LanguageTag.Auto);
    }

    [Fact]
    public void TranslationSettings_normalize_canonicalizes_stored_language_values()
    {
        TranslationSettings settings = new() { TargetLanguage = "zh-CN", SourceLanguage = "zh-TW" };

        TranslationSettings normalized = settings.Normalize();

        normalized.TargetLanguage.Should().Be("zh-Hans");
        normalized.SourceLanguage.Should().Be("zh-Hant");
    }

    [Fact]
    public void TranslationSettings_normalize_keeps_canonical_values_and_defaults()
    {
        new TranslationSettings { TargetLanguage = "zh-Hans" }.Normalize().TargetLanguage.Should().Be("zh-Hans");
        new TranslationSettings { TargetLanguage = " " }.Normalize().TargetLanguage.Should()
            .Be(TranslationSettings.DefaultTargetLanguage);
        new TranslationSettings { SourceLanguage = " " }.Normalize().SourceLanguage.Should()
            .Be(TranslationSettings.DefaultSourceLanguage);
        new TranslationSettings { SourceLanguage = "AUTO" }.Normalize().SourceLanguage.Should().Be("auto");
    }

    private static readonly ConfigFieldDescriptor[] Fields =
    [
        new("target-language", "Target language", ConfigValueKind.Language, false, "A language field."),
        new("target-language-defaulted", "Defaulted language", ConfigValueKind.Language, false,
            "A language field whose value comes from its declared default.", "zh-TW"),
        new("note", "Note", ConfigValueKind.Text, false, "A plain text field.")
    ];

    [Fact]
    public void Resolve_canonicalizes_language_fields_and_leaves_text_fields_raw()
    {
        ExtensionConfigSnapshot stored = new(new Dictionary<string, string>
        {
            ["target-language"] = "zh-CN",
            ["note"] = "zh-CN"
        }, 1);

        ExtensionConfigSnapshot resolved = ExtensionConfiguration.Resolve(Fields, stored);

        resolved.Values["target-language"].Should().Be("zh-Hans",
            "a field declared as a language is stored canonically");
        resolved.Values["target-language-defaulted"].Should().Be("zh-Hant",
            "a declared default of a language field is canonicalized too");
        resolved.Values["note"].Should().Be("zh-CN", "a text field is not a language field and stays raw");
    }

    [Fact]
    public void Resolve_canonicalizes_the_auto_sentinel_of_a_language_field()
    {
        ExtensionConfigSnapshot stored = new(new Dictionary<string, string>
        {
            ["target-language"] = "AUTO",
            ["note"] = "AUTO"
        }, 1);

        ExtensionConfigSnapshot resolved = ExtensionConfiguration.Resolve(Fields, stored);

        resolved.Values["target-language"].Should().Be("auto");
        resolved.Values["note"].Should().Be("AUTO");
    }

    [Fact]
    public void Stored_page_header_with_a_legacy_region_spelling_is_current_for_the_canonical_setting()
    {
        LoadedPageWithStoredLanguage("zh-CN").IsCurrentIn("zh-Hans").Should().BeTrue(
            "a header stored before canonicalization still counts as current for the canonical setting");
        LoadedPageWithStoredLanguage("zh-TW").IsCurrentIn("zh-Hant").Should().BeTrue();
        LoadedPageWithStoredLanguage("zh-CN").IsCurrentIn("zh-Hant").Should().BeFalse(
            "canonicalization unifies spellings of one script, never simplified with traditional");
    }

    private static LoadedTranslationPage LoadedPageWithStoredLanguage(string storedTargetLanguage)
    {
        PageId pageId = PageId.New();
        DocumentTreeRevisionId revisionId = DocumentTreeRevisionId.New();
        DocumentTreeRevision revision = new(
            revisionId,
            DocumentInstanceId.New(),
            pageId,
            null,
            DocumentTreeRevisionSource.Import,
            DocumentTreeRevisionStatus.Committed,
            true,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        PageTranslationStatus status = new(
            1, 1, [], revisionId, true, TargetLanguage: storedTargetLanguage);
        return new LoadedTranslationPage(
            new TranslationPageTarget(pageId, 0), revision, [], string.Empty, status,
            new HashSet<DocumentBoxId>());
    }
}
