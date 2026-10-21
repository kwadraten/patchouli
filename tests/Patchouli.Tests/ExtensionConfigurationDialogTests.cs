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

[Collection("Avalonia")]
public sealed class ExtensionConfigurationDialogTests
{
    private const string EngineId = "translation.ui-dialog-test";

    [Fact]
    public async Task Engine_gear_opens_a_form_and_expands_a_highlighted_multifile_editor_without_losing_drafts()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            await services.TranslationExtensions.InitializeAsync();
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
                    source.Text.Should().Contain("Config.bind");
                    source.Text += "\n// unsaved source draft";
                    editor.SourceText.Should().EndWith("// unsaved source draft");
                    ComboBox files = dialog.FindControl<ComboBox>("SourceFileSelector")!;
                    files.SelectedItem = "extension.yml";
                    Pump(dialog);
                    source.Text.Should().Contain("host_api:");
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
                    editor.AllowHttp = true;
                    Click(dialog, dialog.FindControl<Button>("DiscardConfigurationButton")!);
                    dialog.IsVisible.Should().BeFalse();
                    (await services.TranslationExtensions.Manager.ReadConfigAsync(EngineId)).Values
                        .Should().NotContainKey("limit");
                    services.TranslationExtensions.Authorization.Get(EngineId).Http.Should().BeFalse();
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
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            await services.TranslationExtensions.InitializeAsync();
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
            editor.AllowHttp = true;
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
            services.TranslationExtensions.Authorization.Get(EngineId).Http.Should().BeTrue();
            services.TranslationExtensions.Authorization.Get(copyId).Http.Should().BeFalse();
            return 0;
        }, CancellationToken.None);
    }

    private static ExtensionPackageSnapshot Package(int maximum)
    {
        string source = $$"""
                          open System.Threading.Tasks
                          open Patchouli.Extensions.Contracts
                          open Patchouli.Extensions.Sdk
                          let limit = Config.bind "limit" (Field.integer "并发项数" 4 |> Field.range 1 {{maximum}})
                          let fields = Config.fields [ Config.asField limit ]
                          let extension : ITranslationExtension =
                              { new ITranslationExtension with
                                  member _.Descriptor = ExtensionDescriptor("{{EngineId}}", "界面测试引擎", "1.0", "用于验证表单与源码编辑。")
                                  member _.ConfigFields = fields
                                  member _.ClearDerivedStateAsync(_) = Task.CompletedTask
                                  member _.TranslateAsync(context, _, _) =
                                      Task.FromResult(TranslationExtensionResult([||])) }
                          """;
        return new ExtensionPackageSnapshot(EngineId, new Dictionary<string, string>
        {
            ["extension.yml"] =
                $"id: {EngineId}\nversion: 1.0\nhost_api: patchouli.extensions/1\nkind: translation\nentry: main.fsx",
            ["main.fsx"] = source
        });
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
