using Avalonia.Controls;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Csl;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Editor;
using Patchouli.UI.ViewModels.Settings;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class CslLanguageSettingsUiTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    [Fact]
    public async Task Language_picker_preserves_legacy_values_and_round_trips_selected_code_and_clear()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(async () =>
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"csl-language-ui-{Guid.NewGuid():N}.sqlite");
            MainWindowViewModel main = new(settingsPath: _settings.Path)
            {
                RuntimeDatabasePath = databasePath
            };
            try
            {
                await main.OpenDatabaseCommand.ExecuteAsync();
                await main.Library.CreateCommand.ExecuteAsync();
                HostServices services = await main.ServicesAsync();
                Result<ItemMetadata> created = await services.Items.CreateItemAsync(
                    new CreateItemRequest("webpage", "Language UI", Language: "unknown legacy language"));
                created.IsSuccess.Should().BeTrue(created.ErrorMessage);

                await main.EditItemByIdAsync(created.Value.ItemId.ToString());
                ActivateExtendedSection(main.ItemEditor);
                ItemFieldDescriptor language = main.ItemEditor.Fields.Concat(main.ItemEditor.MoreFields)
                    .Single(field => field.Key == "Language");
                Window window = new() { Content = new ItemEditorPage { DataContext = main.ItemEditor } };
                window.Show();
                try
                {
                    Dispatcher.UIThread.RunJobs();
                    language.IsLanguage.Should().BeTrue();
                    language.Value.Should().Be("unknown legacy language");
                    language.SelectedLanguageOption!.Code.Should().Be("unknown legacy language");
                    language.SelectedLanguageOption.DisplayName.Should().Contain("unknown legacy language");
                    ComboBox picker = window.GetVisualDescendants().OfType<ComboBox>()
                        .Single(combo => ReferenceEquals(combo.DataContext, language));
                    picker.SelectedItem.Should().Be(language.SelectedLanguageOption);
                    picker.SelectedItem = null;
                    language.Value.Should().Be("unknown legacy language",
                        "a transient null selection during detachment must not clear historical metadata");
                    language.SelectedLanguageOption!.Code.Should().Be("unknown legacy language");
                    language.Value = "English";
                    language.SelectedLanguageOption!.Code.Should().Be("English");
                    language.SelectedLanguageOption.DisplayName.Should().Contain("英语");
                    language.Value = "zho";
                    language.SelectedLanguageOption!.DisplayName.Should().Contain("中文");
                    language.SelectedLanguageOption.Code.Should().Be("zho");
                    language.Value = "jpn";
                    language.SelectedLanguageOption!.DisplayName.Should().Contain("日语");
                    language.SelectedLanguageOption.Code.Should().Be("jpn");
                    language.Value = "zh-Hant-TW";
                    language.SelectedLanguageOption!.DisplayName.Should().Contain("中文");
                    language.SelectedLanguageOption.DisplayName.Should().Contain("zh-Hant-TW");
                    language.Value.Should().Be("zh-Hant-TW");
                    language.Value = "unknown legacy language";

                    main.ItemEditor.Title = "Edited without touching language";
                    await main.ItemEditor.SaveCommand.ExecuteAsync();
                    Result<ItemMetadata> unchanged = await services.Items.GetItemAsync(created.Value.ItemId);
                    unchanged.Value.Language.Should().Be("unknown legacy language");

                    await main.EditItemByIdAsync(created.Value.ItemId.ToString());
                    ActivateExtendedSection(main.ItemEditor);
                    language = main.ItemEditor.Fields.Concat(main.ItemEditor.MoreFields)
                        .Single(field => field.Key == "Language");
                    window.Content = new ItemEditorPage { DataContext = main.ItemEditor };
                    Dispatcher.UIThread.RunJobs();
                    picker = window.GetVisualDescendants().OfType<ComboBox>()
                        .Single(combo => combo.DataContext is ItemFieldDescriptor descriptor && descriptor.IsLanguage);


                    picker.SelectedItem = picker.Items.OfType<LanguageOption>().Single(option => option.Code == "zh");
                    language.Value.Should().Be("zh");
                    await main.ItemEditor.SaveCommand.ExecuteAsync();

                    await main.EditItemByIdAsync(created.Value.ItemId.ToString());
                    ActivateExtendedSection(main.ItemEditor);
                    language = main.ItemEditor.Fields.Concat(main.ItemEditor.MoreFields)
                        .Single(field => field.Key == "Language");
                    language.SelectedLanguageOption!.Code.Should().Be("zh");
                    window.Content = new ItemEditorPage { DataContext = main.ItemEditor };
                    Dispatcher.UIThread.RunJobs();
                    picker = window.GetVisualDescendants().OfType<ComboBox>()
                        .Single(combo => combo.DataContext is ItemFieldDescriptor descriptor && descriptor.IsLanguage);
                    picker.SelectedItem = picker.Items.OfType<LanguageOption>().Single(option => option.Code == "en");
                    language.Value.Should().Be("en");
                    await main.ItemEditor.SaveCommand.ExecuteAsync();

                    await main.EditItemByIdAsync(created.Value.ItemId.ToString());
                    ActivateExtendedSection(main.ItemEditor);
                    language = main.ItemEditor.Fields.Concat(main.ItemEditor.MoreFields)
                        .Single(field => field.Key == "Language");
                    language.Value.Should().Be("en");
                    language.SelectedLanguageOption!.Code.Should().Be("en");

                    window.Content = new ItemEditorPage { DataContext = main.ItemEditor };
                    Dispatcher.UIThread.RunJobs();
                    picker = window.GetVisualDescendants().OfType<ComboBox>()
                        .Single(combo => combo.DataContext is ItemFieldDescriptor descriptor && descriptor.IsLanguage);
                    picker.SelectedItem =
                        picker.Items.OfType<LanguageOption>().Single(option => option.Code.Length == 0);
                    language.Value.Should().BeEmpty("the explicit 未指定 option is the user-requested clear action");
                    await main.ItemEditor.SaveCommand.ExecuteAsync();
                    Result<ItemMetadata> cleared = await services.Items.GetItemAsync(created.Value.ItemId);
                    cleared.Value.Language.Should().BeNullOrEmpty();
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                await main.DisposeAsync();
                SqliteTestCleanup.ReleasePools(databasePath);
                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }

            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Csl_localization_settings_are_bound_editable_and_persisted()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(async () =>
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"csl-locale-settings-ui-{Guid.NewGuid():N}.sqlite");
            MainWindowViewModel main = new(settingsPath: _settings.Path)
            {
                RuntimeDatabasePath = databasePath
            };
            try
            {
                await main.OpenDatabaseCommand.ExecuteAsync();
                await main.Library.CreateCommand.ExecuteAsync();
                HostServices services = await main.ServicesAsync();
                Dictionary<string, string> initialMappings = new(CslSettings.DefaultLanguageLocaleMappings)
                {
                    ["zh"] = "zh-CN",
                    ["de"] = "de-DE",
                    ["zz"] = "zz-ZZ"
                };
                (await services.CslStore.SaveSettingsAsync(null, "fr-FR", initialMappings)).IsSuccess.Should().BeTrue();
                CslLocalizationSettingsViewModel section = main.Settings.CslLocalizationSettings;
                Window window = new()
                {
                    Width = 1120, Height = 850,
                    Content = new SettingsPage { DataContext = main.Settings }
                };
                window.Show();
                try
                {
                    await main.Settings.SelectSectionAsync("csl_localization");
                    Dispatcher.UIThread.RunJobs();
                    section.Rows.Should().HaveCount(4);
                    section.Rows.Should().NotContain(row => row.SelectedLanguage!.Code == "zh");
                    section.IsDirty.Should().BeFalse();
                    window.UpdateLayout();
                    ComboBox[] languageEditors = window.GetVisualDescendants().OfType<ComboBox>()
                        .Where(combo => combo.Name == "MappingLanguageEditor").ToArray();
                    languageEditors.Should().HaveCount(4);
                    double[] rowTops = languageEditors.Select(editor => editor.TranslatePoint(default, window)!.Value.Y)
                        .OrderBy(top => top).ToArray();
                    for (int index = 1; index < rowTops.Length; index++)
                    {
                        (rowTops[index] - rowTops[index - 1]).Should().BeGreaterThanOrEqualTo(
                            languageEditors[0].Bounds.Height, "mapping rows must not overlap");
                    }

                    CslLanguageLocaleMappingRowViewModel korean =
                        section.Rows.Single(row => row.SelectedLanguage!.Code == "ko");
                    ComboBox source = languageEditors.Single(editor => ReferenceEquals(editor.DataContext, korean));
                    ComboBox target = window.GetVisualDescendants().OfType<ComboBox>().Single(editor =>
                        editor.Name == "MappingLocaleEditor" && ReferenceEquals(editor.DataContext, korean));
                    source.Bounds.Width.Should().BeGreaterThan(100);
                    target.Bounds.Width.Should().BeGreaterThan(100);
                    target.TranslatePoint(default, window)!.Value.X.Should().BeGreaterThanOrEqualTo(
                        source.TranslatePoint(default, window)!.Value.X + source.Bounds.Width);
                    source.SelectedItem =
                        source.Items.OfType<CslLanguageOption>().Single(option => option.Code == "en");
                    target.SelectedItem = target.Items.OfType<CslLocaleOption>()
                        .Single(option => option.Locale == "fr-FR");
                    korean.SelectedLanguage!.Code.Should().Be("en");
                    korean.SelectedLocale!.Locale.Should().Be("fr-FR");
                    source.SelectedItem = null;
                    target.SelectedItem = null;
                    korean.SelectedLanguage!.Code.Should().Be("en", "transient selection resets must not erase drafts");
                    korean.SelectedLocale!.Locale.Should().Be("fr-FR");

                    Button add = window.GetVisualDescendants().OfType<Button>()
                        .Single(button => button.Name == "AddMappingButton");
                    add.Command!.Execute(add.CommandParameter);
                    CslLanguageLocaleMappingRowViewModel added = section.Rows.Last();
                    added.SelectedLanguage = added.LanguageOptions.Single(option => option.Code == "pt");
                    added.SelectedLocale = added.LocaleOptions.Single(option => option.Locale == "zh-CN");
                    CslLanguageLocaleMappingRowViewModel japanese =
                        section.Rows.Single(row => row.SelectedLanguage!.Code == "ja");
                    Button remove = window.GetVisualDescendants().OfType<Button>().Single(button =>
                        button.Name == "RemoveMappingButton" && ReferenceEquals(button.DataContext, japanese));
                    remove.Command!.Execute(remove.CommandParameter);
                    section.Rows.Should().HaveCount(4);
                    section.IsDirty.Should().BeTrue();

                    (await main.Settings.SaveAllDirtySectionsAsync()).Should().BeTrue();
                    main.Status.Should().Contain("已保存").And.Contain("题录本地化");
                    window.GetVisualDescendants().OfType<TextBlock>()
                        .Should().NotContain(block => block.Text != null && block.Text.Contains("规则已保存"));
                    Result<CslSettings> persisted = await services.CslStore.GetSettingsAsync();
                    persisted.IsSuccess.Should().BeTrue();
                    persisted.Value.LanguageLocaleMappings.Should().BeEquivalentTo(new Dictionary<string, string>
                    {
                        ["en"] = "fr-FR",
                        ["pt"] = "zh-CN",
                        ["de"] = "de-DE",
                        ["zz"] = "zz-ZZ"
                    });
                    persisted.Value.Locale.Should().Be("fr-FR",
                        "editing language mappings must preserve the CSL-wide locale override");

                    added.SelectedLocale = added.LocaleOptions.Single(option => option.Locale == "en-US");
                    await section.DiscardAsync();
                    section.Rows.Single(row => row.SelectedLanguage!.Code == "pt").SelectedLocale!.Locale
                        .Should().Be("zh-CN");
                    section.IsDirty.Should().BeFalse();
                    window.UpdateLayout();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    using WriteableBitmap? frame = window.CaptureRenderedFrame();
                    frame.Should().NotBeNull();
                    string previewPath =
                        TestPaths.FromRepositoryRoot(".tmp", "bug9-12", "localization-mapping-form.png");
                    Directory.CreateDirectory(Path.GetDirectoryName(previewPath)!);
                    frame!.Save(previewPath);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                await main.DisposeAsync();
                SqliteTestCleanup.ReleasePools(databasePath);
                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }

            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Mapping_rows_reject_duplicate_sources_and_can_persist_an_empty_mapping()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<int>(async () =>
        {
            string databasePath = Path.Combine(Path.GetTempPath(), $"csl-mapping-validation-{Guid.NewGuid():N}.sqlite");
            MainWindowViewModel main = new(settingsPath: _settings.Path) { RuntimeDatabasePath = databasePath };
            try
            {
                await main.OpenDatabaseCommand.ExecuteAsync();
                await main.Library.CreateCommand.ExecuteAsync();
                HostServices services = await main.ServicesAsync();
                CslLocalizationSettingsViewModel section = main.Settings.CslLocalizationSettings;
                await section.LoadAsync();
                section.Rows.Select(row => row.SelectedLanguage!.Code).Should().BeEquivalentTo(new[] { "ja", "ko" });
                section.Rows.Should().OnlyContain(row => row.SelectedLocale!.Locale == "zh-CN");
                section.AddMappingCommand.Execute(null);
                CslLanguageLocaleMappingRowViewModel added = section.Rows.Last();
                added.SelectedLanguage = added.LanguageOptions.Single(option => option.Code == "ko");
                section.IsDirty.Should().BeTrue();
                section.CanSave.Should().BeFalse();
                section.ValidationState.Should().Be(SettingsValidationState.Invalid);
                section.Status.Should().Contain("重复");
                await section.SaveAsync();
                (await services.CslStore.GetSettingsAsync()).Value.EffectiveLanguageLocaleMappings
                    .Should().BeEquivalentTo(CslSettings.DefaultLanguageLocaleMappings);

                added.SelectedLanguage = added.LanguageOptions.Single(option => option.Code == "fr");
                section.ValidationState.Should().Be(SettingsValidationState.Valid);
                section.CanSave.Should().BeTrue();
                foreach (CslLanguageLocaleMappingRowViewModel row in section.Rows.ToArray())
                {
                    row.RemoveCommand.Execute(null);
                }

                section.Rows.Should().BeEmpty();
                section.IsDirty.Should().BeTrue();
                await section.SaveAsync();
                (await services.CslStore.GetSettingsAsync()).Value.EffectiveLanguageLocaleMappings.Should().BeEmpty();
                await section.LoadAsync();
                section.Rows.Should()
                    .BeEmpty("removing every row must persist the style-default fallback without resetting mappings");
                section.IsDirty.Should().BeFalse();
                added.SelectedLocale = added.LocaleOptions.Single(option => option.Locale == "fr-FR");
                section.IsDirty.Should().BeFalse("removed rows must no longer affect the settings draft");
                await services.CslStore.SaveLanguageLocaleMappingsAsync(CslSettings.DefaultLanguageLocaleMappings);
                await main.Settings.SelectSectionAsync("csl_localization");
                Window preview = new()
                    { Width = 1120, Height = 850, Content = new SettingsPage { DataContext = main.Settings } };
                preview.Show();
                try
                {
                    Dispatcher.UIThread.RunJobs();
                    preview.UpdateLayout();
                    section.Rows.Select(row => row.SelectedLanguage!.Code).Should()
                        .BeEquivalentTo(new[] { "ja", "ko" });
                    preview.GetVisualDescendants().OfType<TextBlock>().Should()
                        .NotContain(block => block.Text != null && block.Text.Contains("规则已保存"));
                    using WriteableBitmap? frame = preview.CaptureRenderedFrame();
                    frame.Should().NotBeNull();
                    string path = TestPaths.FromRepositoryRoot(".tmp", "bug9-12", "localization-fallback-rules.png");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    frame!.Save(path);
                }
                finally
                {
                    preview.Close();
                }
            }
            finally
            {
                await main.DisposeAsync();
                SqliteTestCleanup.ReleasePools(databasePath);
                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }
            }

            return 0;
        }, CancellationToken.None);
    }

    private static void ActivateExtendedSection(ItemEditorViewModel editor)
    {
        editor.ActiveNavSection = editor.NavSections.Single(section =>
            section.Content is ItemEditorSection.ExtendedInformation);
    }

    public void Dispose()
    {
        _settings.Dispose();
    }
}
