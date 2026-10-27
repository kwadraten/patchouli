using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using FluentAssertions;
using Patchouli.Core.Credentials;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Composition;
using Patchouli.Llm;
using Patchouli.UI;
using Patchouli.UI.Controls;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.UI.ViewModels.Settings;
using Patchouli.UI.Views;
using Patchouli.UI.Views.Settings;

namespace Patchouli.Tests;

/// <remarks>
/// Component dialogs (screenshot revision): the MinerU row owns a real native dialog (model
/// version, polling timeout, masked credential) with no FSX surface, while every package
/// component reuses the attributed-FSX editor — now including editable path fields with
/// managed-file defaults and the one-time ocr.multimodal legacy seed.
/// </remarks>
[Collection("RetainedTabUI")]
[Trait("Category", "ui")]
public sealed class OcrComponentDialogTests(AvaloniaSharedSessionFixture headless)
{
    [Fact]
    public async Task MinerU_dialog_loads_and_saves_real_settings()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();

            MinerUConfigurationDialogViewModel dialog = new(main);
            await dialog.LoadAsync();
            dialog.MinerUModelVersionOptions.Should().BeEquivalentTo("vlm", "pipeline");
            dialog.MinerUCredentialStatus.Should().Contain("未配置");
            dialog.HasPersistedCredential.Should().BeFalse();
            dialog.IsDirty.Should().BeFalse();
            dialog.CanSave.Should().BeFalse();

            bool? closed = null;
            dialog.RequestClose = result => closed = result;
            dialog.MinerUTokenInput = "tok-secret";
            dialog.MinerUModelVersion = "pipeline";
            dialog.MinerUPollingTimeoutSeconds = 120;
            dialog.CanSave.Should().BeTrue();
            await dialog.SaveCommand.ExecuteAsync();

            closed.Should().BeTrue("a successful save closes the dialog");
            dialog.LastError.Should().BeNull();
            dialog.IsDirty.Should().BeFalse();
            main.AppOptions.MinerU.ModelVersion.Should().Be("pipeline");
            main.AppOptions.MinerU.PollingTimeoutSeconds.Should().Be(120);
            Result<string> secret =
                await services.Credentials.GetActiveSecretForProviderAsync(ProviderIds.MinerU);
            secret.IsSuccess.Should().BeTrue();
            secret.Value.Should().Be("tok-secret");
            dialog.HasPersistedCredential.Should().BeTrue();
            dialog.MinerUCredentialStatus.Should().Contain("已配置");
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MinerU_dialog_remove_credential_goes_through_the_host_path()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            RecordingDialogService dialogs = new() { Result = ConfirmDialogResult.Confirm };
            await using MainWindowViewModel main =
                new(dialogs: dialogs, settingsPath: settings.Path,
                    appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            (await main.SaveMinerUSettingsAsync("tok", "vlm", 120)).Should().BeTrue();

            MinerUConfigurationDialogViewModel dialog = new(main);
            await dialog.LoadAsync();
            dialog.HasPersistedCredential.Should().BeTrue();
            dialog.MinerUTokenInput.Should().Be("tok");

            await dialog.RemoveCredentialCommand.ExecuteAsync();
            dialogs.Shown.OfType<ConfirmDialogViewModel>().Should().ContainSingle(
                "the host removal path owns its own confirmation");
            Result<string> secret =
                await services.Credentials.GetActiveSecretForProviderAsync(ProviderIds.MinerU);
            secret.IsSuccess.Should().BeFalse();
            dialog.HasPersistedCredential.Should().BeFalse();
            dialog.MinerUTokenInput.Should().BeEmpty();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MinerU_dialog_has_no_source_or_fsx_surface()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            MinerUConfigurationDialogViewModel viewModel = new(main);
            MinerUConfigurationDialog dialog = new(viewModel);
            dialog.Show();
            try
            {
                dialog.GetVisualDescendants().OfType<FSharpSourceEditor>().Should().BeEmpty();
                dialog.GetVisualDescendants().OfType<PathPickerTextBox>().Should().BeEmpty();
                dialog.GetVisualDescendants().OfType<Control>()
                    .Where(control => control.Name is "SourcePanel" or "SourceFileSelector" or
                        "SourceEditor" or "ToggleSourceButton")
                    .Should().BeEmpty();
            }
            finally
            {
                dialog.Close();
            }

            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Empty_token_save_keeps_the_persisted_secret_and_restores_the_masked_input()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            (await main.SaveMinerUSettingsAsync("tok-persisted", "vlm", 120)).Should().BeTrue();

            MinerUConfigurationDialogViewModel dialog = new(main);
            await dialog.LoadAsync();
            dialog.MinerUTokenInput.Should().Be("tok-persisted",
                "the masked input mirrors the persisted credential");
            dialog.MinerUCredentialStatus.Should().Contain("已配置");

            bool? closed = null;
            dialog.RequestClose = result => closed = result;
            dialog.MinerUTokenInput = "";
            dialog.MinerUModelVersion = "pipeline";
            dialog.MinerUCredentialStatus.Should().Contain("已配置",
                "credential status reflects the persisted fact, not the cleared input");
            await dialog.SaveCommand.ExecuteAsync();

            closed.Should().BeTrue();
            Result<string> secret =
                await services.Credentials.GetActiveSecretForProviderAsync(ProviderIds.MinerU);
            secret.Value.Should().Be("tok-persisted",
                "a model-only save never removes or replaces the stored secret");
            dialog.MinerUTokenInput.Should().Be("tok-persisted",
                "the input re-mirrors the still-persisted credential after a model-only save");
            dialog.HasPersistedCredential.Should().BeTrue();
            dialog.IsDirty.Should().BeFalse();
            main.AppOptions.MinerU.ModelVersion.Should().Be("pipeline");
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MinerU_dialog_reports_a_stale_library_context_instead_of_saving()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            (await main.SaveMinerUSettingsAsync("tok", "vlm", 120)).Should().BeTrue();

            MinerUConfigurationDialogViewModel dialog = new(main);
            await dialog.LoadAsync();
            dialog.MinerUTokenInput = "tok-new";

            // Switching the library swaps the HostServices instance and bumps the generation.
            main.RuntimeDatabasePath = settings.CreateDatabasePath("switched");
            await main.OpenDatabaseCommand.ExecuteAsync();
            HostServices switched = await main.ServicesAsync();
            switched.Should().NotBeSameAs(services);

            await dialog.SaveCommand.ExecuteAsync();
            dialog.LastError.Should().Contain("书库已切换");
            (await services.Credentials.GetActiveSecretForProviderAsync(ProviderIds.MinerU))
                .Value.Should().Be("tok", "a stale dialog must not write through old context");
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Path_fields_carry_descriptor_kinds_and_managed_file_metadata()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            ExtensionManagedFile managed = new("inference.onnx",
                "https://example.invalid/model.onnx", 10);
            ExtensionConfigFieldViewModel file = new(
                new ConfigFieldDescriptor("model-path", "Layout model file",
                    ConfigValueKind.FilePath, false, "", "")
                {
                    ManagedFiles = [managed]
                }, "", []);
            file.IsFilePath.Should().BeTrue();
            file.IsPath.Should().BeTrue();
            file.IsText.Should().BeFalse();
            file.IsDirectoryPath.Should().BeFalse();
            file.HasManagedFiles.Should().BeTrue();
            file.ManagedFiles.Should().ContainSingle();

            ExtensionConfigFieldViewModel directory = new(
                new ConfigFieldDescriptor("models-dir", "Models directory",
                    ConfigValueKind.DirectoryPath, false, "", ""), "", []);
            directory.IsDirectoryPath.Should().BeTrue();
            directory.IsPath.Should().BeTrue();
            directory.IsText.Should().BeFalse();
            directory.HasManagedFiles.Should().BeFalse();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Multimodal_editor_seeds_legacy_ocr_settings_once_and_keeps_them()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            PatchouliAppSettings customized = PatchouliAppSettings.Load(settings.Path, _ => { }) with
            {
                Llm = LlmAppSettings.Default() with
                {
                    OcrProviderId = "anthropic",
                    OcrModel = "claude-sonnet-4",
                    OcrUseLocalLayout = true,
                    OcrLayoutModel = "doclayout-yolo",
                    OcrConcurrency = 7,
                    OcrReasoningEffort = "high"
                }
            };
            customized.Save(settings.Path).IsSuccess.Should().BeTrue();

            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            IExtensionManager manager = services.TranslationExtensions.Manager;
            ExtensionConfigSnapshot untouched = await manager.ReadConfigAsync("ocr.multimodal");
            OcrMultimodalLegacySeed.NeedsSeed("ocr.multimodal", untouched).Should().BeTrue();

            using ExtensionSettingsViewModel editor = main.Settings.CreateExtensionEditor();
            await editor.OpenAsync("ocr.multimodal");
            await editor.LoadAsync();

            ExtensionConfigSnapshot seeded = await manager.ReadConfigAsync("ocr.multimodal");
            seeded.Revision.Should().Be(1, "the one-time migration writes exactly one snapshot");
            seeded.Values["model-id"].Should().Be("claude-sonnet-4");
            seeded.Values["model"].Should().Contain("anthropic");
            seeded.Values["use-local-layout"].Should().Be("true");
            seeded.Values["layout-model"].Should().Be("doclayout-yolo");
            seeded.Values["concurrency"].Should().Be("7");
            seeded.Values["reasoning-effort"].Should().Be("high");
            seeded.Values["model-path"].Should().Be(
                services.OcrStorage.GetLayoutModelPath("doclayout-yolo"));

            ExtensionConfigFieldViewModel modelPath =
                editor.Fields.Single(field => field.Descriptor.Key == "model-path");
            modelPath.IsFilePath.Should().BeTrue();
            modelPath.HasManagedFiles.Should().BeTrue("the FSX declares the layout weights");
            modelPath.Value.Should().Be(seeded.Values["model-path"],
                "the explicit seeded path wins over managed defaults");

            // A second load never rewrites the component configuration.
            using ExtensionSettingsViewModel second = main.Settings.CreateExtensionEditor();
            await second.OpenAsync("ocr.multimodal");
            await second.LoadAsync();
            (await manager.ReadConfigAsync("ocr.multimodal")).Revision.Should().Be(1);
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Managed_default_injects_when_stored_path_is_empty_and_custom_wins_on_save()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            IExtensionManager manager = services.TranslationExtensions.Manager;
            await manager.SaveConfigAsync("ocr.multimodal", new ExtensionConfigSnapshot(
                new Dictionary<string, string>
                {
                    ["model-path"] = "",
                    ["layout-model"] = "doclayout-yolo",
                    ["use-local-layout"] = "true",
                    ["unlisted-key"] = "keep-me",
                    ["parameters-json"] = "{\"hidden\":true}"
                }, 3));

            using ExtensionSettingsViewModel editor = main.Settings.CreateExtensionEditor();
            await editor.OpenAsync("ocr.multimodal");
            await editor.LoadAsync();

            ExtensionConfigFieldViewModel modelPath =
                editor.Fields.Single(field => field.Descriptor.Key == "model-path");
            modelPath.IsFilePath.Should().BeTrue();
            modelPath.Value.Should().Be(services.OcrStorage.GetLayoutModelPath("doclayout-yolo"),
                "an empty stored path displays the managed default (pure path math, no IO)");
            modelPath.HasManagedFiles.Should().BeTrue();

            modelPath.Value = Path.Combine(settings.Path, "..", "custom-model.onnx");
            (await editor.SaveDialogAsync()).Should().BeTrue();
            ExtensionConfigSnapshot saved = await manager.ReadConfigAsync("ocr.multimodal");
            saved.Values["model-path"].Should().Be(modelPath.Value,
                "an explicit custom path is persisted, not the injected default");
            saved.Values["unlisted-key"].Should().Be("keep-me",
                "unknown saved keys survive a form save");
            saved.Values["parameters-json"].Should().Be("{\"hidden\":true}",
                "hidden saved values survive a form save");
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Opened_generic_dialog_populates_fields_and_expanded_source()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();

            using ExtensionSettingsViewModel editor = main.Settings.CreateExtensionEditor();
            await editor.OpenAsync("ocr.multimodal", true);
            ExtensionConfigurationDialog dialog = new(editor);
            dialog.Show();
            try
            {
                await WaitForAsync(() => editor.Fields.Count > 0 && editor.SourceText.Length > 0);
                dialog.IsSourceVisible.Should().BeTrue(
                    "openSource expands the FSX pane when the window opens");
                editor.Fields.Should().NotBeEmpty("the trusted schema supplies real fields");
                editor.Fields.Should().Contain(field => field.IsPath,
                    "path-kind fields render editable path pickers");
                dialog.GetVisualDescendants().OfType<PathPickerTextBox>().Should().NotBeEmpty();
                FSharpSourceEditor sourceEditor =
                    dialog.GetVisualDescendants().OfType<FSharpSourceEditor>().Single();
                sourceEditor.ContentText.Should().Contain("open Patchouli.Extensions.Sdk",
                    "the source pane shows the real working FSX");
            }
            finally
            {
                editor.CancelPendingLoad();
                dialog.Close();
            }

            return 0;
        }, CancellationToken.None);
    }

    private static async Task WaitForAsync(Func<bool> ready, int attempts = 200)
    {
        for (int attempt = 0; attempt < attempts && !ready(); attempt++)
        {
            await Task.Delay(25);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        ready().Should().BeTrue("timed out waiting for the dialog to finish loading");
    }

    [Fact]
    public async Task Broken_working_source_stays_visible_instead_of_bundled_fallback()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            await services.TranslationExtensions.Manager.SaveSourceAsync("ocr.rapidocr",
                new Dictionary<string, string>
                {
                    ["main.fsx"] = "let broken = (\n// user's non-empty draft must stay editable"
                });

            using ExtensionSettingsViewModel editor = main.Settings.CreateExtensionEditor();
            await editor.OpenAsync("ocr.rapidocr", true);
            editor.OpenWithSourceExpanded.Should().BeTrue();
            await editor.LoadAsync();

            editor.SourceFiles.Should().Contain("main.fsx");
            editor.SourceText.Should().Contain("let broken = (",
                "a non-empty broken draft is never replaced by the bundled source");
            editor.SourceText.Should().NotContain("NativeOcrSession");
            return 0;
        }, CancellationToken.None);
    }

    private sealed class RecordingDialogService : IDialogService
    {
        public List<object> Shown { get; } = [];
        public object? Result { get; set; }

        public Task ShowDialogAsync(object viewModel)
        {
            Shown.Add(viewModel);
            return Task.CompletedTask;
        }

        public Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            Shown.Add(viewModel);
            return Task.FromResult(Result is TResult typed ? typed : default);
        }
    }
}
