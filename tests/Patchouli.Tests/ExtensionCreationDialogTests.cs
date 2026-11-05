using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using AvaloniaEdit;
using FluentAssertions;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Composition;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

[Collection("RetainedTabUI")]
[Trait("Category", "ui")]
public sealed class ExtensionCreationDialogTests(AvaloniaSharedSessionFixture headless)
{
    [Theory]
    [InlineData(ExtensionKind.Translation)]
    [InlineData(ExtensionKind.Metadata)]
    [InlineData(ExtensionKind.Ocr)]
    [InlineData(ExtensionKind.PdfTextLayer)]
    public async Task Templates_are_kind_filtered_and_new_copies_are_editable_drafts(ExtensionKind kind)
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            ExtensionCreationViewModel model = main.Settings.CreateExtensionDraft(kind);
            await model.LoadAsync();
            model.Templates.Should().NotBeEmpty().And.OnlyContain(template => template.Kind == kind);
            model.SelectedTemplate.Should().NotBeNull();
            string id = (await model.CreateAsync())!;
            id.Should().NotBeNullOrEmpty(model.Error);

            HostServices services = await main.ServicesAsync();
            services.TranslationExtensions.Manager.List().Should().BeEmpty();
            using ExtensionSettingsViewModel editor = main.Settings.CreateExtensionEditor();
            await editor.OpenAsync(id, true);
            await editor.LoadAsync();
            editor.SelectedExtension!.Id.Should().Be(id);
            editor.IsSourceReadOnly.Should().BeFalse();
            editor.SourceText.Should().Contain($"id=\"{id}\"");
            services.TranslationExtensions.Manager.List().Should().BeEmpty();
            switch (kind)
            {
                case ExtensionKind.Translation:
                    await main.Settings.LlmSettings.RefreshTranslationEnginesCommand.ExecuteAsync();
                    main.Settings.LlmSettings.TranslationEngineOptions.Should().Contain(item => item.Id == id);
                    break;
                case ExtensionKind.Metadata:
                    await main.Settings.MetadataLookupSettings.RefreshExtensionsAsync();
                    main.Settings.MetadataLookupSettings.Sources.Should().Contain(item => item.ExtensionId == id);
                    break;
                default:
                    OcrComponentCatalogSettingsViewModel catalog = kind == ExtensionKind.Ocr
                        ? main.Settings.OcrEngineCatalog
                        : main.Settings.OcrTextLayerCatalog;
                    await catalog.LoadAsync();
                    catalog.Components.Should().Contain(item => item.ExtensionId == id);
                    break;
            }

            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Creation_dialog_can_be_cancelled_without_creating_a_package()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            ExtensionCreationViewModel model = main.Settings.CreateExtensionDraft(ExtensionKind.Translation);
            await model.LoadAsync();
            ExtensionCreationDialog dialog = new() { DataContext = model };
            dialog.Show();
            try
            {
                dialog.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                dialog.FindControl<ComboBox>("TemplateSelector")!.SelectedItem.Should().Be(model.SelectedTemplate);
                dialog.FindControl<TextBox>("NewExtensionId")!.Text.Should().Be(model.ExtensionId);
                using WriteableBitmap? frame = dialog.CaptureRenderedFrame();
                frame.Should().NotBeNull();
                string preview = TestPaths.FromRepositoryRoot(".tmp", "ui", "extension-dialog", "create-extension.png");
                Directory.CreateDirectory(Path.GetDirectoryName(preview)!);
                frame!.Save(preview);
            }
            finally
            {
                dialog.Close();
            }

            (await main.ServicesAsync()).TranslationExtensions.ListWorkingPackages()
                .Should().NotContain(model.ExtensionId);
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Built_in_source_is_read_only_in_the_actual_editor_control()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = new(settingsPath: settings.Path);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            using ExtensionSettingsViewModel editor = main.Settings.CreateExtensionEditor();
            await editor.OpenAsync("translation.google-free", true);
            ExtensionConfigurationDialog dialog = new(editor);
            dialog.Show();
            try
            {
                await editor.LoadAsync();
                dialog.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                dialog.FindControl<TextEditor>("SourceEditor")!.IsReadOnly.Should().BeTrue();
                dialog.FindControl<Button>("CheckAndSaveSourceButton")!.IsVisible.Should().BeFalse();
                dialog.FindControl<Button>("ActivateSourceButton")!.IsVisible.Should().BeFalse();
                dialog.FindControl<Button>("SaveConfigurationButton")!.IsEnabled.Should().BeTrue();
            }
            finally
            {
                dialog.Close();
            }

            return 0;
        }, CancellationToken.None);
    }
}
