using System.Text.Json;
using FluentAssertions;
using Patchouli.Llm;
using Patchouli.UI;

namespace Patchouli.Tests;

public sealed class LlmSettingsPersistenceTests
{
    [Fact]
    public void Llm_section_round_trips_through_the_app_settings_file()
    {
        using TemporaryAppSettingsFile file = new();
        LlmAppSettings llm = LlmAppSettings.Default() with
        {
            OcrProviderId = "azure-openai",
            OcrModel = "gpt-4o",
            TranslationProviderId = "anthropic",
            TranslationModel = "claude-sonnet",
            TargetLanguage = "ja",
            TranslationWindowRadius = 3,
            BackfillPreviousWindowTranslation = false,
            ToolResultMaxCharacters = 8192
        };
        llm = llm.WithProvider(new LlmProviderAppSettings("azure-openai", "Azure OpenAI", "", "gpt-4o",
            "resource-a", "deployment-a", "2024-10-21") { ContextWindowTokens = 200000 });

        (PatchouliAppSettings.Default() with { Llm = llm }).Save(file.Path).IsSuccess.Should().BeTrue();

        PatchouliAppSettings loaded = PatchouliAppSettings.Load(file.Path);

        loaded.Llm.OcrSelection.Should().Be(("azure-openai", "gpt-4o"));
        loaded.Llm.TranslationSelection.Should().Be(("anthropic", "claude-sonnet"));
        loaded.Llm.EffectiveTargetLanguage.Should().Be("ja");
        loaded.Llm.EffectiveTranslationWindowRadius.Should().Be(3);
        loaded.Llm.BackfillPreviousWindowTranslation.Should().BeFalse();
        loaded.Llm.ToolResultMaxCharacters.Should().Be(8192);
        LlmProviderAppSettings azure = loaded.Llm.FindProvider("azure-openai")!;
        azure.Subscription.Should().Be("resource-a");
        azure.Deployment.Should().Be("deployment-a");
        azure.ApiVersion.Should().Be("2024-10-21");
        azure.ContextWindowTokens.Should().Be(200000);
        loaded.Llm.Providers.Select(provider => provider.ProviderId).Should()
            .Equal((IEnumerable<string>)LlmProviderCatalog.ProviderIds);
    }

    [Fact]
    public void Llm_section_is_written_under_the_Llm_json_node()
    {
        using TemporaryAppSettingsFile file = new();

        (PatchouliAppSettings.Default() with
        {
            Llm = LlmAppSettings.Default() with { TargetLanguage = "zh-Hans" }
        }).Save(file.Path).IsSuccess.Should().BeTrue();

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file.Path));

        document.RootElement.TryGetProperty("Llm", out JsonElement llm).Should().BeTrue();
        llm.GetProperty("TargetLanguage").GetString().Should().Be("zh-Hans");
        llm.GetProperty("TranslationWindowRadius").GetInt32().Should().Be(1);
        llm.GetProperty("Providers").GetArrayLength().Should().Be(LlmProviderCatalog.All.Count, "one row per provider");
    }

    [Fact]
    public async Task Field_level_save_updates_only_the_dirty_llm_section()
    {
        using TemporaryAppSettingsFile file = new();
        string settingsPath = file.Path;
        PatchouliAppSettings initial = PatchouliAppSettings.Load(settingsPath);
        PatchouliAppSettings updated = initial with
        {
            Llm = initial.Llm with { TranslationWindowRadius = 4 }
        };

        SettingsSaveResult saved = await updated.SaveFieldLevelAsync(settingsPath,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Llm" });

        saved.IsSuccess.Should().BeTrue();
        PatchouliAppSettings reloaded = PatchouliAppSettings.Load(settingsPath);
        reloaded.Llm.EffectiveTranslationWindowRadius.Should().Be(4);
        reloaded.OcrEngines.Should().Be(initial.OcrEngines);
    }

    [Fact]
    public void A_stored_api_key_never_reaches_the_settings_file()
    {
        using TemporaryAppSettingsFile file = new();
        const string secret = "sk-should-never-be-persisted";
        PatchouliAppSettings settings = PatchouliAppSettings.Default() with
        {
            Llm = LlmAppSettings.Default()
                .WithProvider(new LlmProviderAppSettings("openai", "OpenAI", "https://proxy.example", "gpt-4o"))
        };

        settings.Save(file.Path).IsSuccess.Should().BeTrue();

        string json = File.ReadAllText(file.Path);
        json.Should().NotContain(secret);
        json.Should().NotContain("SecretValue");
        json.Should().NotContain("ApiKey");
        json.Should().NotContain("apiKey");

        // The key belongs to the credential surface instead: the settings model has no property that could
        // carry one, so the round trip above proves the boundary rather than relying on a scrubbing step.
        // Numeric context token counts cannot carry credentials; credential-like text fields can.
        typeof(LlmProviderAppSettings).GetProperties().Where(property => property.PropertyType == typeof(string))
            .Select(property => property.Name)
            .Should().NotContain(name =>
                name.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Key", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Old_settings_files_without_the_Llm_section_load_defaults()
    {
        string root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"patchouli-llm-settings-{Guid.NewGuid():N}")).FullName;
        string path = Path.Combine(root, "settings.json");
        try
        {
            File.WriteAllText(path, """{"Patchouli":{"RuntimeDatabasePath":"x"}}""");

            PatchouliAppSettings loaded = PatchouliAppSettings.Load(path);

            loaded.Llm.Providers.Should().HaveCount(LlmProviderCatalog.All.Count, "one row per provider");
            loaded.Llm.EffectiveTranslationWindowRadius.Should().Be(1);
            loaded.Llm.EffectiveTargetLanguage.Should().Be(LlmAppSettings.FallbackTargetLanguage);
        }
        finally
        {
            TestTempFileCleanup.DeleteDirectoryWithRetry(root);
        }
    }

    [Fact]
    public void Malformed_llm_values_are_normalized_on_load()
    {
        string root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"patchouli-llm-settings-{Guid.NewGuid():N}")).FullName;
        string path = Path.Combine(root, "settings.json");
        try
        {
            File.WriteAllText(path,
                """
                {
                  "Llm": {
                    "Providers": [ { "ProviderId": "openai", "Model": "  gpt-4o  " }, { "ProviderId": "" } ],
                    "OcrProviderId": "not-a-provider",
                    "TranslationProviderId": "anthropic",
                    "TranslationModel": "claude-sonnet",
                    "TargetLanguage": "   ",
                    "TranslationWindowRadius": 900,
                    "BackfillPreviousWindowTranslation": true
                  }
                }
                """);

            PatchouliAppSettings loaded = PatchouliAppSettings.Load(path);

            loaded.Llm.OcrProviderId.Should().Be(LlmAppSettings.DefaultProviderId);
            loaded.Llm.TranslationSelection.Should().Be(("anthropic", "claude-sonnet"));
            loaded.Llm.FindProvider("openai")!.Model.Should().Be("gpt-4o");
            loaded.Llm.EffectiveTargetLanguage.Should().Be(LlmAppSettings.FallbackTargetLanguage);
            loaded.Llm.EffectiveTranslationWindowRadius.Should().Be(LlmAppSettings.MaxTranslationWindowRadius);
            loaded.Llm.Providers.Should().HaveCount(LlmProviderCatalog.All.Count, "one row per provider");
        }
        finally
        {
            TestTempFileCleanup.DeleteDirectoryWithRetry(root);
        }
    }
}
