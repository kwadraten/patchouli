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

/// <summary>The 「LLM 与翻译」settings section: the provider form is rendered from the catalog (D3), the
/// Azure-only fields follow the catalog flags, secrets travel through <see cref="ICredentialStore"/> only
/// and the translation window radius is clamped to [0, 5] (D5).</summary>
[Collection("Avalonia")]
public sealed class LlmSettingsViewModelTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    [Theory]
    [InlineData("sync")]
    [InlineData("mcp")]
    public async Task Rendering_an_api_provider_preserves_its_saved_model_and_reopened_form(string awaySection)
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
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
                List<string> modelChanges = [];
                row.PropertyChanged += (_, change) =>
                {
                    if (change.PropertyName is nameof(row.Model) or nameof(row.AuthenticationMode))
                    {
                        modelChanges.Add(
                            $"{change.PropertyName}: model={row.Model}, auth={row.AuthenticationMode}\n{Environment.StackTrace}");
                    }
                };
                row.ContextWindowTokens = 512000;
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
                    TextBox model = page.GetVisualDescendants().OfType<TextBox>().Single(input =>
                        ReferenceEquals(input.DataContext, row) && input.PlaceholderText == row.ModelWatermark);
                    model.Text.Should().Be("deepseek-flash");
                    section.IsDirty.Should().BeFalse("rendering saved controls is not a user edit");
                    model.Focus();
                    model.SelectAll();
                    window.KeyTextInput("deepseek-reasoner");
                    Dispatcher.UIThread.RunJobs();
                    model.Text.Should().Be("deepseek-reasoner");
                    row.Model.Should().Be("deepseek-reasoner",
                        "typing must update the provider draft before auto-save");
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
                    PatchouliAppSettings.Load(_settings.Path).Llm.FindProvider("deepseek")!.Model.Should()
                        .Be("deepseek-reasoner");
                    await main.Settings.SelectSectionAsync(awaySection);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    await Task.Delay(600);
                    Dispatcher.UIThread.RunJobs();
                    page.GetVisualDescendants().Should().Contain(model);
                    model.IsEffectivelyVisible.Should().BeFalse(
                        "the previous provider editor stays cached but is hidden while another section is active");
                    await main.Settings.SelectSectionAsync("llm");
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    await Task.Delay(1200);
                    Dispatcher.UIThread.RunJobs();
                    row.Model.Should().Be("deepseek-reasoner", string.Join("\n", modelChanges));
                    TextBox reopenedModel = page.GetVisualDescendants().OfType<TextBox>().Single(input =>
                        ReferenceEquals(input.DataContext, row) && input.PlaceholderText == row.ModelWatermark);
                    reopenedModel.Should().BeSameAs(model,
                        "returning to the provider section should reveal the cached editor instance");
                    reopenedModel.Text.Should().Be("deepseek-reasoner");
                    PatchouliAppSettings.Load(_settings.Path).Llm.FindProvider("deepseek")!.Model.Should()
                        .Be("deepseek-reasoner");
                    row.ContextWindowTokens.Should().Be(512000);
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
            savedRow.Model.Should().Be("deepseek-reasoner");
            savedRow.HasCredential.Should().BeTrue();
            savedRow.ApiKeyInput.Should().Be("replacement-secret");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Empty_provider_selectors_do_not_clear_defaults_or_prevent_saving_a_provider()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
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
                ComboBox translation = page.GetVisualDescendants().OfType<ComboBox>()
                    .Single(combo => ReferenceEquals(combo.ItemsSource, section.TranslationProviderOptions));
                translation.SelectedItem.Should().BeNull();

                translation.PlaceholderText.Should().Be("OpenAI（未就绪）");

                section.TranslationProviderId.Should().Be("openai");
                section.OcrProviderId.Should().Be("openai");
                section.IsDirty.Should().BeFalse();

                section.SelectedProviderToAdd = section.AvailableProviderOptions.Single(option =>
                    option.ProviderId == "openai");
                section.AddProviderCommand.Execute(null);
                section.VisibleProviders.Single().ApiKeyInput = "test-secret";
                (await vm.Settings.SaveAllDirtySectionsAsync()).Should().BeTrue();
                Dispatcher.UIThread.RunJobs();
                section.LastError.Should().BeNull();
                translation.SelectedItem.Should().BeSameAs(section.TranslationProviderOptions.Single());

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
                translation.SelectedItem.Should().BeNull();

                section.TranslationProviderId.Should().Be("openai");
                section.OcrProviderId.Should().Be("openai");
                section.IsDirty.Should().BeFalse("removing a credential already writes the credential store");
                await vm.Settings.SelectSectionAsync("ocr_model");
                Dispatcher.UIThread.RunJobs();
                ComboBox ocr = page.GetVisualDescendants().OfType<ComboBox>()
                    .Single(combo => ReferenceEquals(combo.ItemsSource, section.OcrProviderOptions));
                ocr.SelectedItem.Should().BeNull();
                ocr.PlaceholderText.Should().Be("OpenAI（未就绪）");
                ocr.SelectedItem = null;
                section.OcrProviderId.Should().Be("openai");
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
        (_, LlmSettingsViewModel section) = CreateSection(store);
        await section.LoadAsync();
        DateTime unchangedTime = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_settings.Path, unchangedTime);
        section.SelectedTranslationProvider = null;
        section.SelectedOcrProvider = null;
        section.TranslationWindowRadius = 3;
        section.TranslationWindowRadius = 1;
        LlmProviderSettingsRowViewModel openAi = section.Providers.Single(row => row.ProviderId == "openai");
        openAi.Model = "  gpt-4o-mini  ";
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
        (_, LlmSettingsViewModel section) = CreateSection(store);
        await section.LoadAsync();
        section.TranslationProviderId = null!;
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
        (MainWindowViewModel vm, LlmSettingsViewModel section) = CreateSection(new RecordingCredentialStore());
        Host.Composition.HostServices services = await vm.ServicesAsync();
        try
        {
            LlmAppSettings original = services.LlmSettings;
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

        section.Providers.Should().HaveCount(LlmProviderCatalog.All.Count);
        section.Providers.Select(row => row.ProviderId).Should()
            .Equal((IEnumerable<string>)LlmProviderCatalog.ProviderIds);
        section.Providers.Select(row => row.DisplayName).Should()
            .Equal(LlmProviderCatalog.All.Select(entry => entry.DisplayName));
        section.Providers.Should().OnlyContain(row => row.ApiKeyInput.Length == 0,
            "a fresh store has no saved keys to load");
        section.VisibleProviders.Should().BeEmpty();
        section.HasNoProviders.Should().BeTrue();
        section.AvailableProviderOptions.Select(option => option.ProviderId).Should()
            .Equal((IEnumerable<string>)LlmProviderCatalog.ProviderIds);
        section.TranslationProviderOptions.Should().BeEmpty();
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
        section.TranslationProviderOptions.Should().BeEmpty();

        row.Model = "claude-sonnet";
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
        section.TranslationProviderOptions.Should().ContainSingle().Which.ProviderId.Should().Be("anthropic");
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
        section.TranslationProviderOptions.Should().BeEmpty();
        row.Model = "claude-sonnet";
        await section.SaveAsync();
        section.TranslationProviderOptions.Should().ContainSingle();
        await row.RemoveApiKeyCommand.ExecuteAsync(null);
        section.VisibleProviders.Should().ContainSingle().Which.Should().BeSameAs(row);
        section.TranslationProviderOptions.Should().BeEmpty();
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
            section.AvailableProviderOptions.Single(option => option.ProviderId == "custom");
        section.AddProviderCommand.Execute(null);
        await section.SaveAsync();
        section.IsDirty.Should().BeFalse();
        section.LastError.Should().NotBeNullOrWhiteSpace();
        (_, LlmSettingsViewModel reopened) = CreateSection(store);
        await reopened.LoadAsync();
        reopened.VisibleProviders.Should().ContainSingle().Which.ProviderId.Should().Be("custom");
        PatchouliAppSettings.Load(_settings.Path).Llm.FindProvider("custom")!.IsAdded.Should().BeTrue();
        store.SaveCount.Should().Be(0);
    }

    [Fact]
    public void Azure_only_fields_are_shown_for_exactly_the_catalog_entries_that_need_them()
    {
        RecordingCredentialStore store = new();
        (_, LlmSettingsViewModel section) = CreateSection(store);

        LlmProviderSettingsRowViewModel azure =
            section.Providers.Single(row => row.ProviderId == "azure-openai");
        azure.IsAzure.Should().BeTrue();
        azure.IsAzureSubscriptionRequired.Should().BeTrue();
        azure.IsAzureDeploymentRequired.Should().BeTrue();
        azure.DefaultBaseUrl.Should().Be("https://{resource}.openai.azure.com/openai/deployments/{deployment}");

        section.Providers.Where(row => row.ProviderId != "azure-openai").Should()
            .OnlyContain(row => !row.IsAzure && !row.IsAzureSubscriptionRequired && !row.IsAzureDeploymentRequired);

        // A provider without a catalog default endpoint must ask for one; the rest show the default.
        section.Providers.Single(row => row.ProviderId == "custom").RequiresBaseUrl.Should().BeTrue();
        section.Providers.Where(row => row.ProviderId != "custom").Should()
            .OnlyContain(row => !row.RequiresBaseUrl && row.DefaultBaseUrl.Length > 0);
    }

    [Fact]
    public async Task Subscription_login_and_live_models_coexist_with_api_credentials_and_are_excluded_from_ocr()
    {
        RecordingCredentialStore store = new();
        store.SeedSecret("openai", "api-secret");
        (_, LlmSettingsViewModel section) = CreateSection(store);
        FakeSubscriptionService service = new(store);
        bool browserOpened = false;
        section.UseSubscriptionService(service, _ => browserOpened = true);
        await section.LoadAsync();
        section.SelectedProviderToAdd =
            section.AvailableProviderOptions.Single(option => option.ProviderId == "openai-subscription");
        section.AddProviderCommand.Execute(null);
        LlmProviderSettingsRowViewModel row =
            section.VisibleProviders.Single(item => item.ProviderId == "openai-subscription");
        row.IsSubscription.Should().BeTrue();
        row.AuthenticationOptions.Should().ContainSingle().Which.ProviderId.Should()
            .Be(LlmAuthenticationModes.Subscription);
        await row.LoginSubscriptionCommand.ExecuteAsync();
        browserOpened.Should().BeTrue();
        row.Model.Should().Be("subscription-model-a");
        section.TranslationProviderOptions.Select(option => option.ProviderId).Should()
            .Equal("openai", "openai-subscription");
        section.OcrProviderOptions.Should().ContainSingle().Which.ProviderId.Should().Be("openai");
        store.SavedSecrets["openai"].Should().Be("api-secret");
        section.TranslationProviderId = row.ProviderId;
        section.TranslationModel = row.Model;
        await section.SaveAsync();
        PatchouliAppSettings loaded = PatchouliAppSettings.Load(_settings.Path);
        loaded.Llm.TranslationSelection.Should().Be(("openai-subscription", "subscription-model-a"));
        loaded.Llm.FindProvider("openai-subscription")!.AuthenticationMode.Should()
            .Be(LlmAuthenticationModes.Subscription);
        File.ReadAllText(_settings.Path).Should().NotContain("subscription-access").And
            .NotContain("subscription-refresh");

        service.Models = [new LlmSubscriptionModel("new-upstream-model", "New", true)];
        await row.RefreshSubscriptionModelsCommand.ExecuteAsync();
        row.SubscriptionModels.Should().Equal("new-upstream-model");
        section.TranslationModel.Should().Be("new-upstream-model");
        await row.LogoutSubscriptionCommand.ExecuteAsync();
        row.HasCredential.Should().BeFalse();
        section.TranslationProviderOptions.Should().ContainSingle().Which.ProviderId.Should().Be("openai");
        store.SavedSecrets.Should().ContainKey("openai").And
            .NotContainKey(LlmSubscriptionCatalog.CredentialProviderId(row.ProviderId));
    }

    [Fact]
    public async Task Cancelling_subscription_login_leaves_no_credential_and_keeps_the_draft_editable()
    {
        RecordingCredentialStore store = new();
        (_, LlmSettingsViewModel section) = CreateSection(store);
        FakeSubscriptionService service = new(store) { DelayLogin = true };
        section.UseSubscriptionService(service, _ => { });
        await section.LoadAsync();
        section.SelectedProviderToAdd =
            section.AvailableProviderOptions.Single(option => option.ProviderId == "openai-subscription");
        section.AddProviderCommand.Execute(null);
        LlmProviderSettingsRowViewModel row = section.VisibleProviders.Single();
        row.LoginSubscriptionCommand.Execute(null);
        await service.LoginStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        row.IsSubscriptionBusy.Should().BeTrue();
        row.CancelSubscriptionLoginCommand.Execute(null);
        await row.LoginSubscriptionCommand.ExecutionTask!;
        row.IsSubscriptionBusy.Should().BeFalse();
        row.SubscriptionStatusText.Should().Be("已取消订阅操作");
        store.SavedSecrets.Should().BeEmpty();
        section.HasPendingProvider.Should().BeTrue();
    }

    [Fact]
    public async Task Saving_the_subscription_selection_updates_an_already_open_runtime_host()
    {
        (MainWindowViewModel vm, LlmSettingsViewModel section) = CreateSection(new RecordingCredentialStore());
        Host.Composition.HostServices services = await vm.ServicesAsync();
        try
        {
            section.TranslationProviderId = "openai-subscription";
            section.TranslationModel = "new-live-model";
            await section.SaveAsync();
            services.LlmSettings.TranslationSelection.Should().Be(("openai-subscription", "new-live-model"));
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

            await new LlmCodexCredentialStore(store, providerId).SaveAsync(new LlmTornado.Codex.CodexOAuthCredentials
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
        (MainWindowViewModel vm, LlmSettingsViewModel section) = CreateSection(store);
        await section.LoadAsync();

        section.TranslationWindowRadius = 4;
        section.BackfillPreviousWindowTranslation = false;
        section.TargetLanguage = "ja";
        section.TranslationProviderId = "anthropic";
        section.TranslationModel = "claude-sonnet";
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

        // The in-memory settings the rest of the app reads already carry the new values.
        vm.AppOptions.Llm.TranslationSelection.Should().Be(("anthropic", "claude-sonnet"));
        vm.AppOptions.Llm.OcrSelection.Should().Be(("azure-openai", "gpt-4o"));
        vm.AppOptions.Llm.EffectiveTargetLanguage.Should().Be("ja");
        vm.AppOptions.Llm.EffectiveTranslationWindowRadius.Should().Be(4);
        vm.AppOptions.Llm.BackfillPreviousWindowTranslation.Should().BeFalse();
        vm.AppOptions.Llm.FindProvider("azure-openai")!.Deployment.Should().Be("gpt-4o-deployment");

        // The same values round-trip through the settings file.
        PatchouliAppSettings reloaded = PatchouliAppSettings.Load(_settings.Path);
        reloaded.Llm.TranslationSelection.Should().Be(("anthropic", "claude-sonnet"));
        reloaded.Llm.OcrSelection.Should().Be(("azure-openai", "gpt-4o"));
        reloaded.Llm.EffectiveTargetLanguage.Should().Be("ja");
        reloaded.Llm.EffectiveTranslationWindowRadius.Should().Be(4);
        reloaded.Llm.BackfillPreviousWindowTranslation.Should().BeFalse();
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
        section.TranslationWindowRadius = 2;
        section.TargetLanguage = "zh-Hans";
        section.TranslationProviderId = "deepseek";
        section.TranslationModel = "deepseek-chat";
        section.BackfillPreviousWindowTranslation = false;
        await section.SaveAsync();

        (_, LlmSettingsViewModel reloadedSection) = CreateSection(store);
        await reloadedSection.LoadAsync();

        reloadedSection.IsDirty.Should().BeFalse();
        reloadedSection.TranslationWindowRadius.Should().Be(2);
        reloadedSection.TargetLanguage.Should().Be("zh-Hans");
        reloadedSection.TranslationProviderId.Should().Be("deepseek");
        reloadedSection.TranslationModel.Should().Be("deepseek-chat");
        reloadedSection.BackfillPreviousWindowTranslation.Should().BeFalse();
    }

    [Fact]
    public async Task Api_keys_are_written_through_the_credential_store_and_never_to_the_settings_file()
    {
        const string secret = "sk-llm-settings-view-model-secret";
        RecordingCredentialStore store = new();
        (_, LlmSettingsViewModel section) = CreateSection(store);
        await section.LoadAsync();

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
            .Should().Be(LlmProviderCatalog.All.Count);
    }

    [Fact]
    public async Task Credential_status_only_reports_configured_or_not_never_the_secret()
    {
        RecordingCredentialStore store = new();
        store.SeedSecret("anthropic", "sk-anthropic-secret");
        (_, LlmSettingsViewModel section) = CreateSection(store);

        LlmProviderSettingsRowViewModel anthropic =
            section.Providers.Single(row => row.ProviderId == "anthropic");
        anthropic.CredentialStatusText.Should().Be("未配置 API key");
        anthropic.ApiKeyPlaceholder.Should().NotContain("•");

        await section.LoadAsync();

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
    public void Translation_window_radius_is_clamped_to_the_supported_range()
    {
        RecordingCredentialStore store = new();
        (_, LlmSettingsViewModel section) = CreateSection(store);

        section.TranslationWindowRadius.Should().Be(1, "D5 keeps the default window radius at 1");

        section.TranslationWindowRadius = 9;
        section.TranslationWindowRadius.Should().Be(LlmAppSettings.MaxTranslationWindowRadius);

        section.TranslationWindowRadius = -3;
        section.TranslationWindowRadius.Should().Be(0);

        section.TranslationWindowRadius = 3;
        section.TranslationWindowRadius.Should().Be(3);
    }

    [Fact]
    public async Task Discard_reverts_the_llm_draft_without_writing_the_settings_file()
    {
        RecordingCredentialStore store = new();
        (MainWindowViewModel vm, LlmSettingsViewModel section) = CreateSection(store);
        await section.LoadAsync();

        section.TranslationProviderId = "groq";
        section.TranslationWindowRadius = 5;
        section.BackfillPreviousWindowTranslation = false;
        section.IsDirty.Should().BeTrue();

        await section.DiscardAsync();

        section.IsDirty.Should().BeFalse();
        section.TranslationProviderId.Should().Be(LlmAppSettings.DefaultProviderId);
        section.TranslationWindowRadius.Should().Be(1);
        section.BackfillPreviousWindowTranslation.Should().BeTrue();
        PatchouliAppSettings reloaded = PatchouliAppSettings.Load(_settings.Path);
        reloaded.Llm.EffectiveTranslationWindowRadius.Should().Be(1);
        vm.AppOptions.Llm.EffectiveTranslationWindowRadius.Should().Be(1);
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

        // The page binds exactly this section, registered under the LLM category.
        vm.Settings.SectionEntries.Should()
            .ContainSingle(entry => ReferenceEquals(entry.Content, section))
            .Which.Title.Should().Be("模型与翻译");
        section.UseCredentialStore(store);
        return (vm, section);
    }

    /// <summary>Records every secret write and reports the same active-provider metadata the real store does.</summary>
    private sealed class RecordingCredentialStore : ICredentialStore
    {
        public int SaveCount { get; private set; }
        public bool PauseWrites { get; set; }
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
