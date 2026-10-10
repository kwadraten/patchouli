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
            ChatProviderId = "anthropic",
            ChatModel = "claude-sonnet",
            AgentMaxRetries = 0,
            ToolResultMaxCharacters = 8192
        };
        llm = llm.WithProvider(new LlmProviderAppSettings("azure-openai", "Azure OpenAI", "", "gpt-4o",
            "resource-a", "deployment-a", "2024-10-21") { ContextWindowTokens = 200000 });

        (PatchouliAppSettings.Default() with { Llm = llm }).Save(file.Path).IsSuccess.Should().BeTrue();

        PatchouliAppSettings loaded = PatchouliAppSettings.Load(file.Path);

        loaded.Llm.OcrSelection.Should().Be(("azure-openai", "gpt-4o"));
        loaded.Llm.ChatSelection.Should().Be(("anthropic", "claude-sonnet"));
        loaded.Llm.ToolResultMaxCharacters.Should().Be(8192);
        loaded.Llm.AgentMaxRetries.Should().Be(0);
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
            Llm = LlmAppSettings.Default() with { ChatModel = "configured-model" }
        }).Save(file.Path).IsSuccess.Should().BeTrue();

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file.Path));

        document.RootElement.TryGetProperty("Llm", out JsonElement llm).Should().BeTrue();
        llm.GetProperty("ChatModel").GetString().Should().Be("configured-model");
        llm.TryGetProperty("TargetLanguage", out _).Should().BeFalse();
        llm.TryGetProperty("TranslationProviderId", out _).Should().BeFalse();
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
            Llm = initial.Llm with { ChatModel = "updated-model" }
        };

        SettingsSaveResult saved = await updated.SaveFieldLevelAsync(settingsPath,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Llm" });

        saved.IsSuccess.Should().BeTrue();
        PatchouliAppSettings reloaded = PatchouliAppSettings.Load(settingsPath);
        reloaded.Llm.ChatModel.Should().Be("updated-model");
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
        }
        finally
        {
            TestTempFileCleanup.DeleteDirectoryWithRetry(root);
        }
    }

    [Fact]
    public void Legacy_workflow_values_are_preserved_for_migration_without_becoming_runtime_defaults()
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
            loaded.Llm.ChatSelection.Should().Be(("anthropic", "claude-sonnet"));
            loaded.Llm.FindProvider("openai")!.Model.Should().Be("gpt-4o");
            loaded.Llm.LegacyWorkflowValues["windowRadius"].Should().Be("900");
            loaded.Llm.LegacyWorkflowValues["targetLanguage"].Should().Be("   ");
            loaded.Llm.LegacyWorkflowValues["backfillPreviousWindowTranslation"].Should().Be("true");
            loaded.Llm.LegacyWorkflowValues["model"].Should().Contain("claude-sonnet");
            loaded.Save(path).IsSuccess.Should().BeTrue();
            PatchouliAppSettings.Load(path).Llm.LegacyWorkflowValues["windowRadius"].Should().Be("900");
            using JsonDocument rewritten = JsonDocument.Parse(File.ReadAllText(path));
            rewritten.RootElement.GetProperty("Llm").TryGetProperty("TranslationWindowRadius", out _)
                .Should().BeFalse();
            loaded.Llm.Providers.Should().HaveCount(LlmProviderCatalog.All.Count, "one row per provider");
        }
        finally
        {
            TestTempFileCleanup.DeleteDirectoryWithRetry(root);
        }
    }
}
