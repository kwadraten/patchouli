using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using FluentAssertions;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Composition;
using Patchouli.UI;
using Patchouli.UI.Themes;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

/// <remarks>
/// R9 classification: these cases keep the real path on purpose — compile-save, invalid-source
/// rejection, fingerprint stability and unlisted-config preservation all require a real check and a
/// real activated generation. These custom-engine cases save their fixture source and activate that
/// package directly; activating every bundled package is not part of their coverage.
/// Only assertions that are purely about dialog layout could move to the lightweight descriptor
/// boundary; none of the current assertions is layout-only.
/// R13 shared session: each case still owns its source snapshots, config, real extension
/// manager/compiled generation and windows inside the dispatch; <see cref="ThemePaletteApplier"/> mutations are
/// restored per case. ThemePaletteApplierTests mutates the same statics and shares this session.
/// </remarks>
[Collection("RetainedTabUI")]
[Trait("Category", "ui")]
[Trait("Category", "compiled-extension")]
public sealed class ExtensionConfigurationDialogTests(AvaloniaSharedSessionFixture headless)
{
    private const string EngineId = "translation.ui-dialog-test";

    [Theory]
    [InlineData("translation.google-free")]
    [InlineData("metadata.crossref")]
    public async Task Cold_bundled_editor_shows_entry_source_and_schema_without_activating_packages(string id)
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            services.TranslationExtensions.Manager.List().Should().BeEmpty();

            using ExtensionSettingsViewModel editor = main.Settings.CreateExtensionEditor();
            await editor.OpenAsync(id);
            await editor.LoadAsync();

            editor.SourceFiles.Should().Contain("main.fsx").And.NotContain("extension.yml");
            editor.SourceText.Should().Contain("<patchouli-extension");
            editor.Fields.Should().Contain(field => field.Descriptor.Key == "endpoint");
            editor.IsConfigurationSchemaAvailable.Should().BeTrue();
            services.TranslationExtensions.Manager.List().Should().BeEmpty("viewing configuration is not activation");
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Stable_parameters_change_only_rate_fields_in_the_unsaved_draft()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            using ExtensionSettingsViewModel editor = main.Settings.CreateExtensionEditor();
            editor.SelectedExtension = new ExtensionListItem("translation.google-free", "Google", "source");
            editor.Fields.Add(Field("concurrency", "4"));
            editor.Fields.Add(Field("request-interval", "0.25"));
            editor.Fields.Add(Field("endpoint", "https://example.invalid"));

            editor.ApplyStableParametersCommand.Execute(null);

            editor.Fields.Single(field => field.Descriptor.Key == "concurrency").Value.Should().Be("1");
            editor.Fields.Single(field => field.Descriptor.Key == "request-interval").Value.Should().Be("1");
            editor.Fields.Single(field => field.Descriptor.Key == "endpoint").Value.Should()
                .Be("https://example.invalid");
            editor.Status.Should().Contain("点击保存");
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Missing_schema_does_not_save_an_empty_configuration()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            await services.TranslationExtensions.Manager.SaveSourceAsync(EngineId, Package(16).Sources);

            using ExtensionSettingsViewModel editor = main.Settings.CreateExtensionEditor();
            await editor.OpenAsync(EngineId);
            await editor.LoadAsync();

            editor.IsConfigurationSchemaAvailable.Should().BeFalse();
            editor.Fields.Should().BeEmpty();
            ExtensionConfigSnapshot before = await services.TranslationExtensions.Manager.ReadConfigAsync(EngineId);
            await editor.SaveConfigurationCommand.ExecuteAsync();
            ExtensionConfigSnapshot after = await services.TranslationExtensions.Manager.ReadConfigAsync(EngineId);

            after.Revision.Should().Be(before.Revision);
            editor.LastError.Should().Contain("可信配置描述");
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Engine_gear_opens_a_form_and_expands_a_highlighted_multifile_editor_without_losing_drafts()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            await services.TranslationExtensions.Manager.SaveSourceAsync(EngineId, Package(16).Sources);
            ExtensionCheckResult activation = await services.TranslationExtensions.ActivateAsync(Package(16));
            activation.Succeeded.Should()
                .BeTrue(string.Join("; ", activation.Diagnostics.Select(item => item.Message)));

            await main.Settings.SelectSectionAsync("translation");
            main.Settings.Categories.Should().NotContain(category => category.Title == "扩展");
            SettingsPage page = new() { DataContext = main.Settings };
            Window owner = new() { Width = 1280, Height = 960, Content = page };
            owner.Show();
            try
            {
                Pump(owner);
                Button[] gears = owner.GetVisualDescendants().OfType<Button>()
                    .Where(button => button.Name == "ConfigureExtensionButton" && button.IsEffectivelyVisible)
                    .ToArray();
                gears.Should().HaveCount(main.Settings.LlmSettings.TranslationEngineOptions.Count);
                Avalonia.Platform.AssetLoader.Exists(new Uri("avares://Patchouli.UI/Assets/Lucide/settings.svg"))
                    .Should().BeTrue("engine settings must use a bundled gear icon");
                owner.GetVisualDescendants().OfType<Button>()
                    .Should().NotContain(button => Equals(button.Content, "配置与源码"));
                SavePreview(owner, "translation-engines.png");
                Button gear = gears.Single(button => Equals(button.Tag, EngineId));
                Click(owner, gear);
                ExtensionConfigurationDialog dialog = await WaitForDialogAsync(owner);
                try
                {
                    ExtensionSettingsViewModel editor = (ExtensionSettingsViewModel)dialog.DataContext!;
                    await editor.LoadAsync();
                    editor.SelectedExtension!.Id.Should().Be(EngineId);
                    dialog.IsSourceVisible.Should().BeFalse();
                    double compactWidth = dialog.Width;
                    editor.Fields.Single().Value = "6";
                    Pump(dialog);
                    SavePreview(dialog, "extension-form.png");

                    Click(dialog, dialog.FindControl<Button>("ToggleSourceButton")!);
                    dialog.IsSourceVisible.Should().BeTrue();
                    dialog.Width.Should().BeGreaterThan(compactWidth);
                    TextEditor source = dialog.FindControl<TextEditor>("SourceEditor")!;
                    source.ShowLineNumbers.Should().BeTrue();
                    source.SyntaxHighlighting.Should().NotBeNull();
                    source.Text.Should().Contain("[<Settings>]");
                    source.Text += "\n// unsaved source draft";
                    editor.SourceText.Should().EndWith("// unsaved source draft");
                    ComboBox files = dialog.FindControl<ComboBox>("SourceFileSelector")!;
                    files.SelectedItem = "main.fsx";
                    Pump(dialog);
                    source.Text.Should().Contain("<patchouli-extension");
                    files.SelectedItem = "main.fsx";
                    Pump(dialog);
                    source.Text.Should().EndWith("// unsaved source draft");
                    editor.Fields.Single().Value.Should().Be("6");
                    SavePreview(dialog, "extension-source.png");
                    string paletteId = ThemePaletteApplier.CurrentPaletteId;
                    string themeMode = ThemePaletteApplier.CurrentThemeMode;
                    try
                    {
                        ThemePaletteApplier.Apply(paletteId, UiPreferences.ThemeModeDark);
                        ThemePaletteApplier.CurrentEffectiveIsDark.Should().BeTrue();
                        Pump(dialog);
                        SavePreview(dialog, "extension-source-dark.png");
                    }
                    finally
                    {
                        ThemePaletteApplier.Apply(paletteId, themeMode);
                    }

                    Click(dialog, dialog.FindControl<Button>("ToggleSourceButton")!);
                    dialog.IsSourceVisible.Should().BeFalse();
                    dialog.Width.Should().BeApproximately(compactWidth, 1);
                    editor.Fields.Single().Value.Should().Be("6");
                    Click(dialog, dialog.FindControl<Button>("DiscardConfigurationButton")!);
                    dialog.IsVisible.Should().BeFalse();
                    (await services.TranslationExtensions.Manager.ReadConfigAsync(EngineId)).Values
                        .Should().NotContainKey("limit");
                    (await services.TranslationExtensions.Manager.ReadSourceAsync(EngineId)).Sources["main.fsx"]
                        .Should().NotContain("unsaved source draft");
                }
                finally
                {
                    dialog.Close();
                }
            }
            finally
            {
                owner.Close();
            }

            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Checking_source_updates_only_valid_forms_and_saving_config_preserves_unlisted_values()
    {
        HeadlessUnitTestSession session = headless.Session;
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            await services.TranslationExtensions.Manager.SaveSourceAsync(EngineId, Package(16).Sources);
            ExtensionCheckResult activation = await services.TranslationExtensions.ActivateAsync(Package(16));
            activation.Succeeded.Should()
                .BeTrue(string.Join("; ", activation.Diagnostics.Select(item => item.Message)));
            IExtensionManager manager = services.TranslationExtensions.Manager;
            await manager.SaveConfigAsync(EngineId,
                new ExtensionConfigSnapshot(new Dictionary<string, string> { ["reserved-option"] = "retain-me" }, 1));
            string activeFingerprint = manager.List().Single(item => item.Id == EngineId && item.IsActive).Fingerprint;

            using ExtensionSettingsViewModel editor = main.Settings.CreateExtensionEditor();
            await editor.OpenAsync(EngineId);
            await editor.LoadAsync();
            editor.Fields.Single().Value = "6";
            editor.SourceText = Package(8).Sources["main.fsx"];
            await editor.CheckAndSaveSourceCommand.ExecuteAsync();
            editor.ValidationState.Should().Be(SettingsValidationState.Valid);
            editor.Fields.Single().Value.Should().Be("6", "the existing value satisfies the new range");
            string savedSource = (await manager.ReadSourceAsync(EngineId)).Sources["main.fsx"];
            savedSource.Should().Be(editor.SourceText);

            editor.SourceText = "let broken = (";
            await editor.CheckAndSaveSourceCommand.ExecuteAsync();
            editor.ValidationState.Should().Be(SettingsValidationState.Invalid);
            editor.Diagnostics.Should().NotBeEmpty();
            editor.Fields.Single().Value.Should().Be("6");
            (await manager.ReadSourceAsync(EngineId)).Sources["main.fsx"].Should().Be(savedSource);
            manager.List().Single(item => item.Id == EngineId && item.IsActive).Fingerprint.Should()
                .Be(activeFingerprint);

            editor.SourceText = Package(4).Sources["main.fsx"];
            await editor.CheckAndSaveSourceCommand.ExecuteAsync();
            editor.Fields.Single().Value.Should().Be("4", "the old value no longer satisfies the new range");
            editor.Fields.Single().Value = "0";
            (await editor.SaveDialogAsync()).Should().BeFalse();
            (await manager.ReadConfigAsync(EngineId)).Values.Should().NotContainKey("limit");
            editor.Fields.Single().Value = "3";
            (await editor.SaveDialogAsync()).Should().BeTrue();
            ExtensionConfigSnapshot config = await manager.ReadConfigAsync(EngineId);
            config.Values["limit"].Should().Be("3");
            config.Values["reserved-option"].Should().Be("retain-me");
            manager.List().Single(item => item.Id == EngineId && item.IsActive).Fingerprint.Should()
                .Be(activeFingerprint);

            string copyId = EngineId + ".copy";
            editor.CopyId = copyId;
            await editor.CopyCommand.ExecuteAsync();
            editor.SelectedExtension!.Id.Should().Be(copyId);
            editor.Fields.Single().Value.Should().Be("3");
            editor.IsEditorBusy.Should().BeFalse();
            (await manager.ReadConfigAsync(copyId)).Values["reserved-option"].Should().Be("retain-me");
            return 0;
        }, CancellationToken.None);
    }

    private static ExtensionPackageSnapshot Package(int maximum)
    {
        string source = $$"""
                          open System.Threading.Tasks
                          open Patchouli.Extensions.Contracts
                          open Patchouli.Extensions.Sdk
                          [<Settings>]
                          type Settings = {
                              [<Field("并发项数", Kind = ConfigValueKind.Integer)>]
                              [<Range(1, {{maximum}})>]
                              [<Default(4)>]
                              Limit: int
                          }

                          /// <summary>用于验证表单与源码编辑。</summary>
                          /// <patchouli-extension id="{{EngineId}}" kind="translation" version="1.0.0" host-api="patchouli.extensions/3"><name>界面测试引擎</name></patchouli-extension>
                          let extension = Extension.createTranslation<Settings> (fun _ _ _ _ -> Task.CompletedTask)
                          """;
        return new ExtensionPackageSnapshot(EngineId, new Dictionary<string, string>
        {
            ["main.fsx"] = source
        });
    }

    private static ExtensionConfigFieldViewModel Field(string key, string value)
    {
        ConfigFieldDescriptor descriptor = new(key, key, ConfigValueKind.Text, false, "", value);
        return new ExtensionConfigFieldViewModel(descriptor, value, []);
    }

    private static async Task<ExtensionConfigurationDialog> WaitForDialogAsync(Window owner)
    {
        Stopwatch deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            Pump(owner);
            ExtensionConfigurationDialog? dialog = owner.OwnedWindows.OfType<ExtensionConfigurationDialog>()
                .FirstOrDefault();
            if (dialog is not null)
            {
                Pump(dialog);
                return dialog;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("The engine settings button did not open its configuration dialog.");
    }

    private static void Click(Window window, Control control)
    {
        control.BringIntoView();
        Pump(window);
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!
            .Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Pump(window);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void SavePreview(Window window, string name)
    {
        using WriteableBitmap? frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull();
        string path = TestPaths.FromRepositoryRoot(".tmp", "ui", "extension-dialog", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        frame!.Save(path);
    }
}
