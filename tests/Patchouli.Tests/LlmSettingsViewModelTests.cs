using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Patchouli.Core.Credentials;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Llm;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

/// <summary>The 「模型连接」settings section: the provider form is rendered from the catalog (D3), the
/// Azure-only fields follow the catalog flags, secrets travel through <see cref="ICredentialStore"/> only,
/// and the direct translation bounds are validated before they are persisted.</summary>
[Collection("Avalonia")]
public sealed class LlmSettingsViewModelTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    [Fact]
    public async Task Multimodal_layout_selection_and_mode_are_saved_and_discarded()
    {
        (MainWindowViewModel main, LlmSettingsViewModel section) = CreateSection(new RecordingCredentialStore());
        await using (main)
        {
            section.OcrUseLocalLayout.Should().BeTrue();
            section.SelectedOcrLayoutModel!.ModelId.Should().Be(OcrLayoutModelIds.PpDocLayoutV3);
            section.SelectedOcrLayoutModel = section.OcrLayoutModelOptions.Single(option =>
                option.ModelId == OcrLayoutModelIds.DocLayoutYolo);
            section.SelectedOcrLayoutModel = null;
            section.SelectedOcrLayoutModel!.ModelId.Should().Be(OcrLayoutModelIds.DocLayoutYolo);
            section.OcrUseLocalLayout = false;
            section.OcrConcurrency = 6;
            section.SelectedOcrReasoning = section.OcrReasoningOptions.Single(option => option.ModelId == "low");
            section.SelectedOcrReasoning = null;
            await section.SaveAsync();

            LlmAppSettings saved = PatchouliAppSettings.Load(_settings.Path).Llm;
            saved.OcrUseLocalLayout.Should().BeFalse();
            saved.OcrLayoutModel.Should().Be(OcrLayoutModelIds.DocLayoutYolo);
            saved.OcrConcurrency.Should().Be(6);
            saved.OcrReasoningEffort.Should().Be("low");
            section.OcrUseLocalLayout = true;
            section.OcrConcurrency = 1;
            section.SelectedOcrReasoning = section.OcrReasoningOptions.First();
            section.SelectedOcrLayoutModel = section.OcrLayoutModelOptions.First();
            await section.DiscardAsync();
            section.OcrUseLocalLayout.Should().BeFalse();
            section.OcrConcurrency.Should().Be(6);
            section.SelectedOcrReasoning!.ModelId.Should().Be("low");
            section.IsDirty.Should().BeFalse();
        }
    }

    [Theory]
    [InlineData("sync")]
    [InlineData("mcp")]
    public async Task Rendering_an_api_provider_preserves_its_saved_model_and_reopened_form(string awaySection)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<bool>(async () =>
        {
            RecordingCredentialStore store = new();
            store.SeedSecret("deepseek", "test-secret");
            (MainWindowViewModel main, LlmSettingsViewModel section) = CreateSection(store);
            await using (main)
            {
                await section.LoadAsync();
                LlmProviderSettingsRowViewModel row =
                    section.Providers.Single(provider => provider.ProviderId == "deepseek");
                row.Model = "deepseek-flash";
                await section.SaveAsync();
                await main.Settings.SelectSectionAsync("llm");
                row.Model.Should().Be("deepseek-flash", "the model must first round-trip without a view");
                section.IsDirty.Should().BeFalse();

                SettingsPage page = new() { DataContext = main.Settings };
                Window window = new() { Width = 1200, Height = 900, Content = page };
                window.Show();
                try
                {
                    Dispatcher.UIThread.RunJobs();
                    row.Model.Should().Be("deepseek-flash", "hidden subscription selectors must not clear API models");
                    page.GetVisualDescendants().OfType<TextBox>().Should().NotContain(input =>
                            ReferenceEquals(input.DataContext, row) && input.PasswordChar != '•' &&
                            input.PlaceholderText == row.ModelWatermark,
                        "the API provider card no longer offers a model text box");
                    Button[] rowButtons = page.GetVisualDescendants().OfType<Button>()
                        .Where(button => ReferenceEquals(button.DataContext, row)).ToArray();
                    rowButtons.Should().ContainSingle(button => button.Command == row.RemoveConnectionCommand,
                        "the API card exposes exactly one destructive action");
                    rowButtons.Should().NotContain(button => button.Command == row.RemoveApiKeyCommand);
                    section.IsDirty.Should().BeFalse("rendering saved controls is not a user edit");

                    TextBox key = page.GetVisualDescendants().OfType<TextBox>().Single(input =>
                        ReferenceEquals(input.DataContext, row) && input.PasswordChar == '•');
                    key.Focus();
                    key.SelectAll();
                    window.KeyTextInput("replacement-secret");
                    Dispatcher.UIThread.RunJobs();
                    row.ApiKeyInput.Should().Be("replacement-secret", "typing must update the credential draft");
                    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
                    while (section.IsDirty || section.IsSaving)
                    {
                        await Task.Delay(20, timeout.Token);
                    }

                    store.SavedSecrets["deepseek"].Should().Be("replacement-secret");
                    row.HasCredential.Should().BeTrue();
                    key.Text.Should().Be("replacement-secret");
                    key.PlaceholderText.Should().NotContain("•");
                    key.IsFocused.Should().BeTrue(
                        "the credential autosave refresh must not rebuild or defocus the editor");
                    PatchouliAppSettings.Load(_settings.Path).Llm.FindProvider("deepseek")!.Model.Should()
                        .Be("deepseek-flash");
                    await main.Settings.SelectSectionAsync(awaySection);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    await Task.Delay(600);
                    Dispatcher.UIThread.RunJobs();
                    await main.Settings.SelectSectionAsync("llm");
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    await Task.Delay(1200);
                    Dispatcher.UIThread.RunJobs();
                    row.Model.Should().Be("deepseek-flash");
                    TextBox reopenedKey = page.GetVisualDescendants().OfType<TextBox>().Single(input =>
                        ReferenceEquals(input.DataContext, row) && input.PasswordChar == '•');
                    reopenedKey.Should().BeSameAs(key,
                        "returning to the provider section should reveal the cached editor instance");
                    reopenedKey.Text.Should().Be("replacement-secret");
                    section.IsDirty.Should().BeFalse("re-entering the section must not fabricate edits");
                }
                finally
                {
                    window.Close();
                }
            }

            await using MainWindowViewModel reopened = new(settingsPath: _settings.Path);
            reopened.Settings.LlmSettings.UseCredentialStore(store);
            await reopened.Settings.SelectSectionAsync("llm");
            LlmProviderSettingsRowViewModel savedRow = reopened.Settings.LlmSettings.VisibleProviders
                .Single(provider => provider.ProviderId == "deepseek");
            savedRow.Model.Should().Be("deepseek-flash");
            savedRow.HasCredential.Should().BeTrue();
            savedRow.ApiKeyInput.Should().Be("replacement-secret");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Empty_provider_selectors_do_not_clear_defaults_or_prevent_saving_a_provider()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<bool>(async () =>
        {
            RecordingCredentialStore store = new();
            (MainWindowViewModel vm, LlmSettingsViewModel section) = CreateSection(store);
            await vm.Settings.SelectSectionAsync("llm");
            await vm.Settings.WaitForActiveSectionLoadAsync();
            SettingsPage page = new() { DataContext = vm.Settings };
            page.FindControl<Border>("SettingsHeader")!.Child.Should().BeOfType<TextBlock>();
            Window window = new() { Width = 1200, Height = 900, Content = page };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                ComboBox chat = page.GetVisualDescendants().OfType<ComboBox>()
                    .Single(combo => ReferenceEquals(combo.ItemsSource, section.ChatProviderOptions));
                chat.SelectedItem.Should().BeNull();

                chat.PlaceholderText.Should().Be("OpenAI（未就绪）");

                section.ChatProviderId.Should().Be("openai");
                section.OcrProviderId.Should().Be("openai");
                section.IsDirty.Should().BeFalse();

                section.SelectedProviderToAdd = section.AvailableProviderOptions.Single(option =>
                    option.ProviderId == "openai");
                section.AddProviderCommand.Execute(null);
                LlmProviderSettingsRowViewModel added = section.VisibleProviders.Single();
                added.BaseUrl = "https://api.openai.com";
                added.ApiKeyInput = "test-secret";
                (await vm.Settings.SaveAllDirtySectionsAsync()).Should().BeTrue();
                Dispatcher.UIThread.RunJobs();
                section.LastError.Should().BeNull();
                chat.SelectedItem.Should().BeSameAs(section.ChatProviderOptions.Single());

                TextBox keyInput = page.GetVisualDescendants().OfType<TextBox>()
                    .Single(input => input.PasswordChar == '•');
                keyInput.Text.Should().Be("test-secret");
                keyInput.PlaceholderText.Should().NotContain("•");
                await section.LoadAsync();
                Dispatcher.UIThread.RunJobs();
                keyInput.PlaceholderText.Should().NotContain("•");

                await section.VisibleProviders.Single().RemoveApiKeyCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();
                keyInput.PlaceholderText.Should().NotContain("•");
                chat.SelectedItem.Should().BeNull();

                section.ChatProviderId.Should().Be("openai");
                section.OcrProviderId.Should().Be("openai");
                section.IsDirty.Should().BeFalse("removing a credential already writes the credential store");
                return true;
            }
            finally
            {
                window.Close();
                vm.Settings.Dispose();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Returning_to_saved_values_and_cancelling_an_addition_do_not_write_settings()
    {
        RecordingCredentialStore store = new();
        store.SeedSecret("openai", "seeded-key");
        (_, LlmSettingsViewModel section) = CreateSection(store);
        await section.LoadAsync();
        DateTime unchangedTime = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_settings.Path, unchangedTime);
        section.SelectedChatProvider = null;
        section.SelectedOcrProvider = null;
        LlmProviderSettingsRowViewModel openAi = section.Providers.Single(row => row.ProviderId == "openai");
        openAi.Model = "  gpt-4o-mini  ";
        openAi.Model = "";
        openAi.ApiKeyInput = "  ";
        section.IsDirty.Should().BeFalse();
        section.CanSave.Should().BeFalse();
        await section.SaveAsync();
        File.GetLastWriteTimeUtc(_settings.Path).Should().Be(unchangedTime);
        store.SaveCount.Should().Be(0);

        section.SelectedProviderToAdd =
            section.AvailableProviderOptions.Single(option => option.ProviderId == "anthropic");
        section.AddProviderCommand.Execute(null);
        LlmProviderSettingsRowViewModel draft = section.VisibleProviders.Single();
        draft.Model = "claude-sonnet";
        draft.ApiKeyInput = "cancelled-secret";
        section.IsDirty.Should().BeTrue();
        section.CancelAddProviderCommand.Execute(null);
        section.IsDirty.Should().BeFalse();
        await section.SaveAsync();
        File.GetLastWriteTimeUtc(_settings.Path).Should().Be(unchangedTime);
        store.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Credential_only_changes_do_not_rewrite_settings_or_repeat_secret_writes()
    {
        RecordingCredentialStore store = new();
        store.SeedSecret("openai", "seeded-key");
        (_, LlmSettingsViewModel section) = CreateSection(store);
        await section.LoadAsync();
        DateTime unchangedTime = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_settings.Path, unchangedTime);
        LlmProviderSettingsRowViewModel row = section.Providers.Single(provider => provider.ProviderId == "openai");
        row.ApiKeyInput = "test-secret";
        await section.SaveAsync();
        section.SaveState.Should().Be(SettingsSaveState.Saved);
        section.IsDirty.Should().BeFalse();
        store.SaveCount.Should().Be(1);
        File.GetLastWriteTimeUtc(_settings.Path).Should().Be(unchangedTime);
        await section.SaveAsync();
        store.SaveCount.Should().Be(1);
        File.GetLastWriteTimeUtc(_settings.Path).Should().Be(unchangedTime);
    }

    [Fact]
    public async Task Saving_a_key_preserves_a_newer_replacement_typed_during_the_write()
    {
        RecordingCredentialStore store = new() { PauseWrites = true };
        store.SeedSecret("openai", "saved-key");
        (MainWindowViewModel main, LlmSettingsViewModel section) = CreateSection(store);
        await using (main)
        {
            await section.LoadAsync();
            LlmProviderSettingsRowViewModel row = section.Providers.Single(provider => provider.ProviderId == "openai");
            section.IsDirty.Should().BeFalse();
            row.ApiKeyInput = "first-replacement";
            Task saving = section.SaveAsync();
            await store.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            row.ApiKeyInput = "newer-replacement";
            store.ReleaseWrite.TrySetResult();
            await saving;
            row.ApiKeyInput.Should().Be("newer-replacement");
            section.IsDirty.Should().BeTrue();
            store.SavedSecrets["openai"].Should().Be("first-replacement");
            store.PauseWrites = false;
            await section.SaveAsync();
            store.SavedSecrets["openai"].Should().Be("newer-replacement");
            row.ApiKeyInput.Should().Be("newer-replacement");
            section.IsDirty.Should().BeFalse();
            store.SaveCount.Should().Be(2);
            await section.LoadAsync();
            await section.SaveAsync();
            store.SaveCount.Should().Be(2, "loading a saved key is not another edit");
        }
    }

    [Fact]
    public async Task A_null_scope_default_does_not_break_an_unrelated_provider_save()
    {
        RecordingCredentialStore store = new();
        store.SeedSecret("openai", "seeded-key");
        (_, LlmSettingsViewModel section) = CreateSection(store);
        await section.LoadAsync();
        section.ChatProviderId = null!;
        section.OcrProviderId = null!;
        section.Providers.Single(row => row.ProviderId == "openai").Model = "new-model";
        await section.SaveAsync();
        section.SaveState.Should().Be(SettingsSaveState.Saved);
        section.ValidationState.Should().Be(SettingsValidationState.Invalid);
        PatchouliAppSettings.Load(_settings.Path).Llm.FindProvider("openai")!.Model.Should().Be("new-model");
    }

    [Fact]
    public async Task Credential_only_updates_refresh_the_runtime_snapshot_without_changing_settings()
    {
        RecordingCredentialStore store = new();
        store.SeedSecret("openai", "seeded-key");
        (MainWindowViewModel vm, LlmSettingsViewModel section) = CreateSection(store);
        Host.Composition.HostServices services = await vm.ServicesAsync();
        try
        {
            LlmAppSettings original = services.LlmSettings;
            await section.LoadAsync();
            section.Providers.Single(row => row.ProviderId == "openai").ApiKeyInput = "test-secret";
            await section.SaveAsync();
            services.LlmSettings.Should().NotBeSameAs(original,
                "cached agent clients resolve credentials again when the snapshot changes");
            services.LlmSettings.Should().Be(original);
            vm.AppOptions.Llm.Should().BeSameAs(original);
        }
        finally
        {
            vm.Dispose();
            await services.ShutdownAsync();
        }
    }

    [Fact]
    public void Fresh_provider_form_is_empty_and_catalog_is_available_for_adding()
    {
        RecordingCredentialStore store = new();
        (_, LlmSettingsViewModel section) = CreateSection(store);

        section.Providers.Should().BeEmpty("no saved records or credentials means no cards");
        section.VisibleProviders.Should().BeEmpty();
        section.HasNoProviders.Should().BeTrue();
        section.AvailableProviderOptions.Select(option => option.ProviderId).Should()
            .Equal((IEnumerable<string>)LlmProviderCatalog.ProviderIds);
        section.AvailableProviderOptions.Select(option => option.ProviderId).Should()
            .NotContain(["azure-openai", "openai-subscription", "custom"]);
        section.ChatProviderOptions.Should().BeEmpty();
        section.OcrProviderOptions.Should().BeEmpty();
    }

    [Fact]
    public async Task Adding_a_provider_persists_incomplete_fields_and_allows_the_next_addition()
    {
        RecordingCredentialStore store = new();
        (_, LlmSettingsViewModel section) = CreateSection(store);
        await section.LoadAsync();
        section.SelectedProviderToAdd =
            section.AvailableProviderOptions.Single(option => option.ProviderId == "anthropic");
        section.AddProviderCommand.Execute(null);
        LlmProviderSettingsRowViewModel row = section.VisibleProviders.Should().ContainSingle().Which;
        row.ProviderId.Should().Be("anthropic");
        section.HasPendingProvider.Should().BeTrue();
        section.AddProviderCommand.CanExecute(null).Should().BeFalse();
        section.ChatProviderOptions.Should().BeEmpty();

        row.Model = "claude-sonnet";
        row.BaseUrl = "https://api.anthropic.com";
        await section.SaveAsync();
        section.HasPendingProvider.Should().BeFalse();
        section.IsDirty.Should().BeFalse();
        section.ValidationState.Should().Be(SettingsValidationState.Invalid);
        section.Status.Should().Contain("已保存").And.Contain("补全");
        (_, LlmSettingsViewModel incompleteReload) = CreateSection(store);
        await incompleteReload.LoadAsync();
        incompleteReload.VisibleProviders.Should().ContainSingle().Which.Model.Should().Be("claude-sonnet");
        row.ApiKeyInput = "test-secret";
        section.IsDirty.Should().BeTrue("entering only the key must trigger auto-save");
        await section.SaveAsync();

        section.HasPendingProvider.Should().BeFalse();
        section.AvailableProviderOptions.Should().NotContain(option => option.ProviderId == "anthropic");
        section.ChatProviderOptions.Should().ContainSingle().Which.ProviderId.Should().Be("anthropic");
        section.OcrProviderOptions.Should().ContainSingle().Which.ProviderId.Should().Be("anthropic");
        (_, LlmSettingsViewModel reloaded) = CreateSection(store);
        await reloaded.LoadAsync();
        reloaded.VisibleProviders.Should().ContainSingle().Which.ProviderId.Should().Be("anthropic");

        section.AddProviderCommand.Execute(null);
        section.VisibleProviders.Should().HaveCount(2);
        section.CancelAddProviderCommand.Execute(null);
        section.VisibleProviders.Should().ContainSingle().Which.ProviderId.Should().Be("anthropic");
    }

    [Fact]
    public async Task Existing_incomplete_credentials_remain_editable_and_revoked_providers_leave_the_choices()
    {
        RecordingCredentialStore store = new();
        store.SeedSecret("anthropic", "test-secret");
        (_, LlmSettingsViewModel section) = CreateSection(store);
        await section.LoadAsync();
        LlmProviderSettingsRowViewModel row = section.VisibleProviders.Should().ContainSingle().Which;
        section.ChatProviderOptions.Should().BeEmpty();
        row.Model = "claude-sonnet";
        row.BaseUrl = "https://api.anthropic.com";
        await section.SaveAsync();
        section.ChatProviderOptions.Should().ContainSingle();
        await row.RemoveApiKeyCommand.ExecuteAsync(null);
        section.VisibleProviders.Should().ContainSingle().Which.Should().BeSameAs(row);
        section.ChatProviderOptions.Should().BeEmpty();
        section.AvailableProviderOptions.Should().NotContain(option => option.ProviderId == "anthropic");
        await row.RemoveConnectionCommand.ExecuteAsync();
        await section.SaveAsync();
        section.VisibleProviders.Should().BeEmpty();
        section.AvailableProviderOptions.Should().Contain(option => option.ProviderId == "anthropic");
    }

    [Fact]
    public async Task An_empty_added_connection_survives_restart_without_a_credential()
    {
        RecordingCredentialStore store = new();
        (_, LlmSettingsViewModel section) = CreateSection(store);
        section.SelectedProviderToAdd =
            section.AvailableProviderOptions.Single(option => option.ProviderId == "openai");
        section.AddProviderCommand.Execute(null);
        await section.SaveAsync();
        section.IsDirty.Should().BeFalse();
        section.LastError.Should().NotBeNullOrWhiteSpace();
        (_, LlmSettingsViewModel reopened) = CreateSection(store);
        await reopened.LoadAsync();
        reopened.VisibleProviders.Should().ContainSingle().Which.ProviderId.Should().Be("openai");
        PatchouliAppSettings.Load(_settings.Path).Llm.FindProvider("openai")!.IsAdded.Should().BeTrue();
        store.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Azure_only_fields_are_shown_for_exactly_the_catalog_entries_that_need_them()
    {
        (PatchouliAppSettings.Default() with
        {
            Llm = LlmAppSettings.Default()
                .WithProvider(new LlmProviderAppSettings("azure-openai", "Azure OpenAI", "", "gpt-4o",
                    "res", "dep") { IsAdded = true })
                .WithProvider(new LlmProviderAppSettings("custom", "Custom", "http://localhost:11434",
                    "llama") { IsAdded = true })
                .WithProvider(new LlmProviderAppSettings("deepseek", "", "", "deepseek-chat")
                    { IsAdded = true })
        }).Save(_settings.Path);
        RecordingCredentialStore store = new();
        (_, LlmSettingsViewModel section) = CreateSection(store);
        await section.LoadAsync();

        LlmProviderSettingsRowViewModel azure =
            section.Providers.Single(row => row.ProviderId == "azure-openai");
        azure.IsAzure.Should().BeTrue();
        azure.IsAzureSubscriptionRequired.Should().BeTrue();
        azure.IsAzureDeploymentRequired.Should().BeTrue();
        azure.DefaultBaseUrl.Should().Be("https://{resource}.openai.azure.com/openai/deployments/{deployment}");

        section.Providers.Where(row => row.ProviderId != "azure-openai").Should()
            .OnlyContain(row => !row.IsAzure && !row.IsAzureSubscriptionRequired && !row.IsAzureDeploymentRequired);

        section.Providers.Single(row => row.ProviderId == "custom").RequiresBaseUrl.Should().BeTrue();
        section.Providers.Single(row => row.ProviderId == "deepseek").DefaultBaseUrl.Should()
            .Be("https://api.deepseek.com");
        LlmProviderCatalog.All.Where(entry => entry.RequiresBaseUrl).Should()
            .OnlyContain(entry => entry.DefaultBaseUrl.Length == 0);
    }

    [Fact]
    public async Task Subscription_login_and_live_models_coexist_with_api_credentials_and_are_excluded_from_ocr()
    {
        RecordingCredentialStore store = new();
        store.SeedSecret("openai", "api-secret");
        store.SeedSecret(LlmSubscriptionCatalog.CredentialProviderId("openai-subscription"),
            """{"AccessToken":"old","RefreshToken":"old","AccountId":"account"}""");
        (PatchouliAppSettings.Default() with
        {
            Llm = LlmAppSettings.Default().WithProvider(
                LlmProviderAppSettings.FromCatalog(LlmProviderCatalog.Find("openai-subscription")!) with
                {
                    IsAdded = true
                })
        }).Save(_settings.Path);
        (_, LlmSettingsViewModel section) = CreateSection(store);
        FakeSubscriptionService service = new(store);
        bool browserOpened = false;
        section.UseSubscriptionService(service, _ => browserOpened = true);
        await section.LoadAsync();
        LlmProviderSettingsRowViewModel row =
            section.VisibleProviders.Single(item => item.ProviderId == "openai-subscription");
        row.IsSubscription.Should().BeTrue();
        row.AuthenticationOptions.Should().ContainSingle().Which.ProviderId.Should()
            .Be(LlmAuthenticationModes.Subscription);
        await row.LoginSubscriptionCommand.ExecuteAsync();
        browserOpened.Should().BeTrue();
        row.Model.Should().Be("subscription-model-a");
        section.Providers.Single(item => item.ProviderId == "openai").BaseUrl = "https://api.openai.com";
        section.ChatProviderOptions.Select(option => option.ProviderId).Should()
            .Equal("openai", "openai-subscription");
        section.OcrProviderOptions.Should().ContainSingle().Which.ProviderId.Should().Be("openai");
        store.SavedSecrets["openai"].Should().Be("api-secret");
        section.ChatProviderId = row.ProviderId;
        section.ChatModel = row.Model;
        await section.SaveAsync();
        PatchouliAppSettings loaded = PatchouliAppSettings.Load(_settings.Path);
        loaded.Llm.ChatSelection.Should().Be(("openai-subscription", "subscription-model-a"));
        loaded.Llm.FindProvider("openai-subscription")!.AuthenticationMode.Should()
            .Be(LlmAuthenticationModes.Subscription);
        File.ReadAllText(_settings.Path).Should().NotContain("subscription-access").And
            .NotContain("subscription-refresh");

        service.Models = [new LlmSubscriptionModel("new-upstream-model", "New", true)];
        await row.RefreshSubscriptionModelsCommand.ExecuteAsync();
        row.SubscriptionModels.Should().Equal("new-upstream-model");
        section.ChatModel.Should().Be("new-upstream-model");
        await row.LogoutSubscriptionCommand.ExecuteAsync();
        row.HasCredential.Should().BeFalse();
        section.ChatProviderOptions.Should().ContainSingle().Which.ProviderId.Should().Be("openai");
        store.SavedSecrets.Should().ContainKey("openai").And
            .NotContainKey(LlmSubscriptionCatalog.CredentialProviderId(row.ProviderId));
    }

    [Fact]
    public async Task Cancelling_subscription_login_leaves_the_connection_card_editable()
    {
        RecordingCredentialStore store = new();
        store.SeedSecret(LlmSubscriptionCatalog.CredentialProviderId("openai-subscription"),
            """{"AccessToken":"old","RefreshToken":"old","AccountId":"account"}""");
        (_, LlmSettingsViewModel section) = CreateSection(store);
        FakeSubscriptionService service = new(store) { DelayLogin = true };
        section.UseSubscriptionService(service, _ => { });
        await section.LoadAsync();
        LlmProviderSettingsRowViewModel row = section.VisibleProviders.Single();
        row.ProviderId.Should().Be("openai-subscription");
        row.LoginSubscriptionCommand.Execute(null);
        await service.LoginStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        row.IsSubscriptionBusy.Should().BeTrue();
        row.CancelSubscriptionLoginCommand.Execute(null);
        await row.LoginSubscriptionCommand.ExecutionTask!;
        row.IsSubscriptionBusy.Should().BeFalse();
        row.SubscriptionStatusText.Should().Be("已取消订阅操作");
        section.VisibleProviders.Should().ContainSingle().Which.Should().BeSameAs(row,
            "a credential-only connection card stays editable after a cancelled login");
        section.HasPendingProvider.Should().BeFalse();
    }

    [Fact]
    public async Task Saving_the_subscription_selection_updates_an_already_open_runtime_host()
    {
        (MainWindowViewModel vm, LlmSettingsViewModel section) = CreateSection(new RecordingCredentialStore());
        Host.Composition.HostServices services = await vm.ServicesAsync();
        try
        {
            section.ChatProviderId = "openai-subscription";
            section.ChatModel = "new-live-model";
            await section.SaveAsync();
            services.LlmSettings.ChatSelection.Should().Be(("openai-subscription", "new-live-model"));
            services.LlmSettings.Should().BeSameAs(vm.AppOptions.Llm);
        }
        finally
        {
            vm.Dispose();
            await services.ShutdownAsync();
        }
    }

    private sealed class FakeSubscriptionService(RecordingCredentialStore store) : ILlmSubscriptionService
    {
        private bool _signedIn;
        public bool DelayLogin { get; init; }
        public TaskCompletionSource LoginStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<LlmSubscriptionModel> Models { get; set; } = [new("subscription-model-a", "A", true)];

        public Task<LlmSubscriptionAccount?> GetAccountAsync(string providerId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_signedIn ? new LlmSubscriptionAccount("test@example.com", "pro") : null);
        }

        public async Task LoginAsync(string providerId, Action<Uri> openBrowser,
            CancellationToken cancellationToken = default)
        {
            openBrowser(new Uri("https://auth.example/login"));
            LoginStarted.TrySetResult();
            if (DelayLogin)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            await new LlmCodexCredentialStore(store, providerId).SaveAsync(new LlmCodexCredentials
            {
                AccessToken = "subscription-access", RefreshToken = "subscription-refresh", AccountId = "test-account"
            }, cancellationToken);
            _signedIn = true;
        }

        public Task<IReadOnlyList<LlmSubscriptionModel>> ListModelsAsync(string providerId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Models);
        }

        public async Task LogoutAsync(string providerId, CancellationToken cancellationToken = default)
        {
            await new LlmCodexCredentialStore(store, providerId).ClearAsync(cancellationToken);
            _signedIn = false;
        }
    }

    [Fact]
    public async Task Settings_round_trip_through_the_app_settings_file()
    {
        RecordingCredentialStore store = new();
        (PatchouliAppSettings.Default() with
        {
            Llm = LlmAppSettings.Default().WithProvider(
                new LlmProviderAppSettings("azure-openai", "Azure OpenAI") { IsAdded = true })
        }).Save(_settings.Path);
        (MainWindowViewModel vm, LlmSettingsViewModel section) = CreateSection(store);
        await section.LoadAsync();

        section.ChatProviderId = "anthropic";
        section.ChatModel = "claude-sonnet";
        section.OcrProviderId = "azure-openai";
        section.OcrModel = "gpt-4o";
        LlmProviderSettingsRowViewModel azure =
            section.Providers.Single(row => row.ProviderId == "azure-openai");
        azure.Model = "gpt-4o";
        azure.Subscription = "patchouli-resource";
        azure.Deployment = "gpt-4o-deployment";
        azure.ApiVersion = "2024-10-21";
        section.IsDirty.Should().BeTrue();

        await section.SaveAsync();

        section.SaveState.Should().Be(SettingsSaveState.Saved, $"status: {section.Status}");
        section.IsDirty.Should().BeFalse();

        vm.AppOptions.Llm.ChatSelection.Should().Be(("anthropic", "claude-sonnet"));
        vm.AppOptions.Llm.OcrSelection.Should().Be(("azure-openai", "gpt-4o"));
        vm.AppOptions.Llm.FindProvider("azure-openai")!.Deployment.Should().Be("gpt-4o-deployment");

        PatchouliAppSettings reloaded = PatchouliAppSettings.Load(_settings.Path);
        reloaded.Llm.ChatSelection.Should().Be(("anthropic", "claude-sonnet"));
        reloaded.Llm.OcrSelection.Should().Be(("azure-openai", "gpt-4o"));
        LlmProviderAppSettings reloadedAzure = reloaded.Llm.FindProvider("azure-openai")!;
        reloadedAzure.Subscription.Should().Be("patchouli-resource");
        reloadedAzure.Deployment.Should().Be("gpt-4o-deployment");
        reloadedAzure.ApiVersion.Should().Be("2024-10-21");
        reloadedAzure.Model.Should().Be("gpt-4o");
    }

    [Fact]
    public async Task A_fresh_section_loads_the_values_written_by_the_previous_save()
    {
        RecordingCredentialStore store = new();
        (_, LlmSettingsViewModel section) = CreateSection(store);
        await section.LoadAsync();
        section.ChatProviderId = "deepseek";
        section.ChatModel = "deepseek-chat";
        await section.SaveAsync();

        (_, LlmSettingsViewModel reloadedSection) = CreateSection(store);
        await reloadedSection.LoadAsync();

        reloadedSection.IsDirty.Should().BeFalse();
        reloadedSection.ChatProviderId.Should().Be("deepseek");
        reloadedSection.ChatModel.Should().Be("deepseek-chat");
    }

    [Fact]
    public async Task Api_keys_are_written_through_the_credential_store_and_never_to_the_settings_file()
    {
        const string secret = "sk-llm-settings-view-model-secret";
        RecordingCredentialStore store = new();
        (_, LlmSettingsViewModel section) = CreateSection(store);
        await section.LoadAsync();

        section.SelectedProviderToAdd =
            section.AvailableProviderOptions.Single(option => option.ProviderId == "openai");
        section.AddProviderCommand.Execute(null);
        LlmProviderSettingsRowViewModel openAi = section.Providers.Single(row => row.ProviderId == "openai");
        openAi.BaseUrl = "https://proxy.example";
        openAi.Model = "gpt-4o";
        openAi.ApiKeyInput = secret;

        await section.SaveAsync();

        store.SavedSecrets.Should().ContainKey("openai").WhoseValue.Should().Be(secret);
        openAi.ApiKeyInput.Should().Be(secret, "the saved baseline stays in the masked input");
        openAi.HasCredential.Should().BeTrue();
        openAi.ApiKeyPlaceholder.Should().NotContain("•");
        openAi.CredentialStatusText.Should().Be("已配置 API key");

        string json = File.ReadAllText(_settings.Path);
        json.Should().NotContain(secret);
        json.Should().NotContain("ApiKey");
        json.Should().NotContain("SecretValue");
        json.Should().Contain("https://proxy.example");
        using JsonDocument document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("Llm").GetProperty("Providers").GetArrayLength()
            .Should().Be(1, "only the configured connection is stored");
    }

    [Fact]
    public async Task Credential_status_only_reports_configured_or_not_never_the_secret()
    {
        RecordingCredentialStore store = new();
        store.SeedSecret("anthropic", "sk-anthropic-secret");
        (_, LlmSettingsViewModel section) = CreateSection(store);

        section.Providers.Should().BeEmpty("readiness has not been inspected yet");

        await section.LoadAsync();

        LlmProviderSettingsRowViewModel anthropic =
            section.Providers.Single(row => row.ProviderId == "anthropic");

        anthropic.HasCredential.Should().BeTrue();
        anthropic.CredentialStatusText.Should().Be("已配置 API key");
        anthropic.ApiKeyInput.Should().Be("sk-anthropic-secret");
        anthropic.ApiKeyPlaceholder.Should().NotContain("•");
        section.Providers.Where(row => row.ProviderId != "anthropic" && !row.IsSubscription).Should()
            .OnlyContain(row => !row.HasCredential && row.CredentialStatusText == "未配置 API key");
        section.Providers.Where(row => row.IsSubscription).Should()
            .OnlyContain(row => !row.HasCredential && row.CredentialStatusText == "尚未登录订阅");

        anthropic.ApiKeyInput = "sk-replacement";
        anthropic.CredentialStatusText.Should().Be("保存后更新 API key");
        await section.DiscardAsync();
        anthropic.ApiKeyInput.Should().Be("sk-anthropic-secret");
        anthropic.ApiKeyPlaceholder.Should().NotContain("•");
        await anthropic.RemoveApiKeyCommand.ExecuteAsync();
        anthropic.HasCredential.Should().BeFalse();
        anthropic.ApiKeyPlaceholder.Should().NotContain("•");
    }

    [Fact]
    public void Shared_model_settings_have_no_workflow_business_fields()
    {
        typeof(LlmSettingsViewModel).GetProperties().Select(property => property.Name)
            .Should().NotContain(new[]
                { "TranslationWindowRadius", "TargetLanguage", "BackfillPreviousWindowTranslation" });
    }

    [Fact]
    public async Task Selecting_a_translation_engine_updates_the_saved_translation_options()
    {
        (MainWindowViewModel main, LlmSettingsViewModel section) = CreateSection(new RecordingCredentialStore());
        await using (main)
        {
            await section.LoadAsync();

            section.TranslationEngineOptions.Select(option => option.Id).Should().Equal(
                "translation.llm", "translation.google-free", "translation.deepl-free",
                "translation.microsoft-free", "translation.tencent-free", "translation.deeplx");
            section.SelectedTranslationEngine = section.TranslationEngineOptions.Single(option =>
                option.Id == "translation.deeplx");
            section.TranslationEngineId.Should().Be("translation.deeplx");
            section.IsDirty.Should().BeTrue();

            await section.SaveAsync();

            PatchouliAppSettings.Load(_settings.Path).Llm.Translation.EngineId.Should().Be("translation.deeplx");
            section.IsDirty.Should().BeFalse();
        }
    }

    [Fact]
    public async Task Discard_reverts_the_llm_draft_without_writing_the_settings_file()
    {
        RecordingCredentialStore store = new();
        (MainWindowViewModel vm, LlmSettingsViewModel section) = CreateSection(store);
        await section.LoadAsync();

        section.ChatProviderId = "groq";
        section.IsDirty.Should().BeTrue();

        await section.DiscardAsync();

        section.IsDirty.Should().BeFalse();
        section.ChatProviderId.Should().Be(LlmAppSettings.DefaultProviderId);
        PatchouliAppSettings reloaded = PatchouliAppSettings.Load(_settings.Path);
    }

    [Fact]
    public async Task A_keyed_provider_without_a_model_is_ready_and_selectable()
    {
        RecordingCredentialStore store = new();
        store.SeedSecret("deepseek", "test-secret");
        (_, LlmSettingsViewModel section) = CreateSection(store);
        await section.LoadAsync();

        LlmProviderSettingsRowViewModel row = section.Providers.Single(item => item.ProviderId == "deepseek");
        row.HasCredential.Should().BeTrue();
        row.Readiness.Should().NotBeNull();
        row.Readiness!.HasModel.Should().BeFalse();
        row.Readiness.IsConfigured.Should().BeTrue(
            "connection readiness is credential + endpoint + supported transport, not a provider model");
        row.DiagnosticText.Should().Be("Ready.");
        section.ChatProviderOptions.Should().ContainSingle().Which.ProviderId.Should().Be("deepseek");
    }

    [Fact]
    public async Task An_unsupported_protocol_connection_reports_a_diagnostic_instead_of_readiness()
    {
        RecordingCredentialStore store = new();
        store.SeedSecret("cohere", "test-secret");
        (_, LlmSettingsViewModel section) = CreateSection(store);
        await section.LoadAsync();

        LlmProviderSettingsRowViewModel row = section.Providers.Single(item => item.ProviderId == "cohere");
        row.HasCredential.Should().BeTrue();
        row.Readiness!.IsConfigured.Should().BeFalse();
        row.DiagnosticText.Should().Contain("unknown");
        section.ChatProviderOptions.Should().BeEmpty(
            "an unimplemented protocol must not reach the model selectors");
    }

    [Fact]
    public async Task A_failed_credential_removal_keeps_the_connection_card()
    {
        RecordingCredentialStore store = new() { FailRemovals = true };
        store.SeedSecret("deepseek", "test-secret");
        (_, LlmSettingsViewModel section) = CreateSection(store);
        await section.LoadAsync();
        LlmProviderSettingsRowViewModel row = section.Providers.Single(item => item.ProviderId == "deepseek");

        await row.RemoveConnectionCommand.ExecuteAsync();

        section.Providers.Should().Contain(row, "a failed credential removal must not drop the card");
        section.VisibleProviders.Should().ContainSingle().Which.Should().BeSameAs(row);
        row.HasCredential.Should().BeTrue();
        section.SaveState.Should().Be(SettingsSaveState.Failed);
        store.SavedSecrets.Should().ContainKey("deepseek");
        PatchouliAppSettings.Load(_settings.Path).Llm.FindProvider("deepseek").Should().BeNull(
            "the failed removal must not silently persist a partial delete");
    }

    [Fact]
    public async Task Removing_a_connection_deletes_its_record_and_restores_the_add_option()
    {
        RecordingCredentialStore store = new();
        store.SeedSecret("deepseek", "test-secret");
        (_, LlmSettingsViewModel section) = CreateSection(store);
        await section.LoadAsync();
        LlmProviderSettingsRowViewModel row = section.Providers.Single(item => item.ProviderId == "deepseek");

        await row.RemoveConnectionCommand.ExecuteAsync();
        await section.SaveAsync();

        section.Providers.Should().BeEmpty();
        section.VisibleProviders.Should().BeEmpty();
        section.HasNoProviders.Should().BeTrue();
        section.AvailableProviderOptions.Should().Contain(option => option.ProviderId == "deepseek");
        PatchouliAppSettings.Load(_settings.Path).Llm.FindProvider("deepseek").Should().BeNull();
        store.SavedSecrets.Should().NotContainKey("deepseek");
    }

    [Theory]
    [InlineData("sync")]
    [InlineData("mcp")]
    public async Task The_saved_global_model_survives_section_switching_and_a_full_reload(string awaySection)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<bool>(async () =>
        {
            RecordingCredentialStore store = new();
            store.SeedSecret("deepseek", "test-secret");
            (MainWindowViewModel main, LlmSettingsViewModel section) = CreateSection(store);
            await using (main)
            {
                await section.LoadAsync();
                section.ChatProviderId = "deepseek";
                section.ChatModel = "deepseek-chat";
                await section.SaveAsync();
                section.IsDirty.Should().BeFalse();
                PatchouliAppSettings.Load(_settings.Path).Llm.FindProvider("deepseek")
                    .Should().BeNull("the provider row needs no stored model");

                SettingsPage page = new() { DataContext = main.Settings };
                Window window = new() { Width = 1200, Height = 900, Content = page };
                window.Show();
                try
                {
                    await main.Settings.SelectSectionAsync("llm");
                    Dispatcher.UIThread.RunJobs();
                    ComboBox model = page.GetVisualDescendants().OfType<ComboBox>()
                        .Single(combo => ReferenceEquals(combo.ItemsSource, section.ChatModelOptions));
                    model.SelectedItem.Should().Be("deepseek-chat");
                    model.Text.Should().Be("deepseek-chat");

                    await main.Settings.SelectSectionAsync(awaySection);
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(600);
                    Dispatcher.UIThread.RunJobs();
                    await main.Settings.SelectSectionAsync("llm");
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(600);
                    Dispatcher.UIThread.RunJobs();

                    model.SelectedItem.Should().Be("deepseek-chat",
                        "leaving and re-entering the section must not clear the saved model");
                    model.Text.Should().Be("deepseek-chat");
                    section.ChatModel.Should().Be("deepseek-chat");
                    section.IsDirty.Should().BeFalse();
                    store.SaveCount.Should().Be(0, "re-entering must not rewrite credentials");
                }
                finally
                {
                    window.Close();
                }
            }

            await using MainWindowViewModel reopened = new(settingsPath: _settings.Path);
            reopened.Settings.LlmSettings.UseCredentialStore(store);
            await reopened.Settings.SelectSectionAsync("llm");
            await reopened.Settings.WaitForActiveSectionLoadAsync();

            SettingsPage reopenedPage = new() { DataContext = reopened.Settings };
            Window reopenedWindow = new() { Width = 1200, Height = 900, Content = reopenedPage };
            reopenedWindow.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                ComboBox model = reopenedPage.GetVisualDescendants().OfType<ComboBox>()
                    .Single(combo =>
                        ReferenceEquals(combo.ItemsSource, reopened.Settings.LlmSettings.ChatModelOptions));
                model.SelectedItem.Should().Be("deepseek-chat");
                model.Text.Should().Be("deepseek-chat");
                reopened.Settings.LlmSettings.ChatModel.Should().Be("deepseek-chat");
                reopened.Settings.LlmSettings.IsDirty.Should().BeFalse();
            }
            finally
            {
                reopenedWindow.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_saved_model_absent_from_the_snapshot_stays_selectable()
    {
        RecordingCredentialStore store = new();
        store.SeedSecret("deepseek", "test-secret");
        (PatchouliAppSettings.Default() with
        {
            Llm = LlmAppSettings.Default() with
            {
                ChatProviderId = "deepseek", ChatModel = "unlisted-custom-model"
            }
        }).Save(_settings.Path);
        (_, LlmSettingsViewModel section) = CreateSection(store);
        await section.LoadAsync();

        section.ChatModel.Should().Be("unlisted-custom-model");
        section.SelectedChatModel.Should().Be("unlisted-custom-model",
            "a legacy model id keeps an explicit option even when the snapshot lacks it");
        section.ChatModelOptions.Should().Contain("unlisted-custom-model");
        section.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task Selected_chat_model_ignores_transient_clearing_and_accepts_real_selections()
    {
        RecordingCredentialStore store = new();
        store.SeedSecret("deepseek", "test-secret");
        (_, LlmSettingsViewModel section) = CreateSection(store);
        await section.LoadAsync();
        section.ChatProviderId = "deepseek";
        section.ChatModel = "deepseek-chat";
        await section.SaveAsync();

        section.SelectedChatModel = null;
        section.SelectedChatModel = "   ";
        section.ChatModel.Should().Be("deepseek-chat");
        section.IsDirty.Should().BeFalse("a transient null or blank item is not an edit");

        section.SelectedChatModel = "deepseek-reasoner";
        section.ChatModel.Should().Be("deepseek-reasoner");
        await section.SaveAsync();
        PatchouliAppSettings.Load(_settings.Path).Llm.ChatModel.Should().Be("deepseek-reasoner");
    }

    [Fact]
    public async Task Legacy_default_rows_stay_loadable_and_keyed_ones_keep_working()
    {
        string legacyJson = """
                            {
                              "Llm": {
                                "Providers": [
                                  { "ProviderId": "openai", "DisplayName": "OpenAI", "BaseUrl": "https://api.openai.com",
                                    "Model": "gpt-4o-mini" },
                                  { "ProviderId": "anthropic", "DisplayName": "Anthropic",
                                    "BaseUrl": "https://api.anthropic.com", "Model": "" },
                                  { "ProviderId": "groq", "DisplayName": "Groq", "BaseUrl": "https://api.groq.com",
                                    "Model": "" },
                                  { "ProviderId": "deepseek", "DisplayName": "DeepSeek",
                                    "BaseUrl": "https://api.deepseek.com", "Model": "" }
                                ],
                                "ChatProviderId": "openai",
                                "ChatModel": "gpt-4o-mini"
                              }
                            }
                            """;
        File.WriteAllText(_settings.Path, legacyJson);

        PatchouliAppSettings loaded = PatchouliAppSettings.Load(_settings.Path);
        loaded.Llm.FindProvider("openai")!.BaseUrl.Should().Be("https://api.openai.com");
        loaded.Llm.FindProvider("openai")!.Model.Should().Be("gpt-4o-mini",
            "a saved baseline model must not be eaten by migration");

        RecordingCredentialStore store = new();
        store.SeedSecret("openai", "openai-key");
        store.SeedSecret("anthropic", "anthropic-key");
        Result<LlmProviderRuntimeSettings> openAi =
            await LlmProviderClientFactory.ResolveAsync(loaded.Llm, "openai", null, store);
        Result<LlmProviderRuntimeSettings> anthropic =
            await LlmProviderClientFactory.ResolveAsync(loaded.Llm, "anthropic", "claude-x", store);
        openAi.IsSuccess.Should().BeTrue(openAi.ErrorMessage);
        openAi.Value.BaseUrl.Should().Be("https://api.openai.com/{0}/{1}");
        openAi.Value.Model.Should().Be("gpt-4o-mini");
        anthropic.IsSuccess.Should().BeTrue(anthropic.ErrorMessage);

        (_, LlmSettingsViewModel section) = CreateSection(store);
        DateTime unchangedTime = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_settings.Path, unchangedTime);
        await section.LoadAsync();

        string[] expectedVisible = new[] { "openai", "anthropic" }
            .OrderBy(LlmProviderCatalog.CatalogOrder).ToArray();
        section.VisibleProviders.Select(row => row.ProviderId).Should()
            .Equal(expectedVisible,
                "keyed legacy rows render; unkeyed legacy defaults stay out of the UI");
        section.Providers.Single(row => row.ProviderId == "openai").BaseUrl.Should()
            .Be("https://api.openai.com");
        section.IsDirty.Should().BeFalse("opening the page never auto-writes");
        store.SaveCount.Should().Be(0);
        File.GetLastWriteTimeUtc(_settings.Path).Should().Be(unchangedTime);

        section.ChatModel = "gpt-4o";
        await section.SaveAsync();
        PatchouliAppSettings saved = PatchouliAppSettings.Load(_settings.Path);
        saved.Llm.FindProvider("openai")!.BaseUrl.Should().Be("https://api.openai.com");
        saved.Llm.FindProvider("anthropic")!.BaseUrl.Should().Be("https://api.anthropic.com");
        saved.Llm.FindProvider("deepseek").Should().NotBeNull(
            "unkeyed legacy rows keep their stored data until the user removes them");
    }

    public void Dispose()
    {
        _settings.Dispose();
    }

    private (MainWindowViewModel ViewModel, LlmSettingsViewModel Section) CreateSection(
        RecordingCredentialStore store)
    {
        MainWindowViewModel vm = new(settingsPath: _settings.Path);
        LlmSettingsViewModel section = vm.Settings.LlmSettings;

        vm.Settings.SectionEntries.Should()
            .ContainSingle(entry => ReferenceEquals(entry.Content, section))
            .Which.Title.Should().Be("模型连接");
        section.UseCredentialStore(store);
        return (vm, section);
    }

    /// <summary>Records every secret write and reports the same active-provider metadata the real store does.</summary>
    private sealed class RecordingCredentialStore : ICredentialStore
    {
        public int SaveCount { get; private set; }
        public bool PauseWrites { get; set; }
        public bool FailRemovals { get; init; }
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Dictionary<string, string> SavedSecrets { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void SeedSecret(string providerId, string secret)
        {
            SavedSecrets[providerId] = secret;
        }

        public async Task<Result<ProviderCredentialMetadata>> SaveAsync(string providerId, string displayName,
            string secretValue, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            if (PauseWrites)
            {
                WriteStarted.TrySetResult();
                await ReleaseWrite.Task.WaitAsync(cancellationToken);
            }

            SavedSecrets[providerId] = secretValue;
            return Result<ProviderCredentialMetadata>.Success(
                new ProviderCredentialMetadata(CredentialId.New(), providerId, displayName,
                    ProviderCredentialStatus.Active, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        }

        public Task<Result<string>> GetActiveSecretForProviderAsync(string providerId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SavedSecrets.TryGetValue(providerId, out string? secret)
                ? Result<string>.Success(secret)
                : Result<string>.Failure(AppErrorCodes.NotFound, "Credential was not found."));
        }

        public Task<Result> RemoveAsync(string providerId, CancellationToken cancellationToken = default)
        {
            if (FailRemovals)
            {
                return Task.FromResult(Result.Failure(AppErrorCodes.InvalidState, "removal refused"));
            }

            SavedSecrets.Remove(providerId);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<IReadOnlyList<ProviderCredentialMetadata>>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ProviderCredentialMetadata> metadata = SavedSecrets.Keys
                .Select(providerId => new ProviderCredentialMetadata(CredentialId.New(), providerId, providerId,
                    ProviderCredentialStatus.Active, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch))
                .ToArray();
            return Task.FromResult(Result<IReadOnlyList<ProviderCredentialMetadata>>.Success(metadata));
        }
    }
}
