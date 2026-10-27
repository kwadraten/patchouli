using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Translation;
using Patchouli.Extensions.Contracts;
using Patchouli.Host;
using Patchouli.Host.Composition;
using Patchouli.Ocr;
using Patchouli.Performance;
using Patchouli.UI;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

/// <summary>
///     The 文献加工 menu surfaces real per-library presets per scope — never guessed engine
///     choices — keeps a stale stored selection visible and checked rather than remapping, and
///     routes explicit choices through the validating selection service. Translation engine
///     entries come from real package metadata only (bundled manifests, working sources, active
///     generations); a stored id that resolves to nothing stays listed as unavailable.
/// </summary>
[Collection("Avalonia")]
public sealed class OcrPresetMenuTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    private async Task<MainWindowViewModel> OpenMainAsync(string tag)
    {
        MainWindowViewModel main = new(settingsPath: _settings.Path)
        {
            RuntimeDatabasePath = _settings.CreateDatabasePath($"ocr-menu-{tag}")
        };
        await main.OpenDatabaseCommand.ExecuteAsync(null);
        await main.Library.CreateCommand.ExecuteAsync(null);
        return main;
    }

    public void Dispose()
    {
        _settings.Dispose();
    }

    [Fact]
    public async Task Menu_lists_automatic_plus_active_presets_for_each_scope()
    {
        MainWindowViewModel main = await OpenMainAsync(nameof(Menu_lists_automatic_plus_active_presets_for_each_scope));
        HostServices services = await main.ServicesAsync();
        OcrPreset preset = (await services.OcrPresets.CreatePresetAsync("扫描文献预设", null,
            OcrEngineIds.MinerU, OcrModelIds.MinerUDefault, null, "{}", true)).Value;
        (await services.OcrPresets.ArchivePresetAsync(
            (await services.OcrPresets.CreatePresetAsync("已归档预设", null,
                OcrEngineIds.MinerU, OcrModelIds.MinerUDefault, null, "{}", true))
            .Value.PresetId)).IsSuccess.Should().BeTrue();

        await main.RefreshOcrPresetMenuAsync();

        ObservableCollection<OcrPresetMenuEntryViewModel>[] menus =
        [
            main.DocumentOcrPresetMenuEntries, main.PageOcrPresetMenuEntries,
            main.RegionOcrPresetMenuEntries
        ];
        foreach (ObservableCollection<OcrPresetMenuEntryViewModel> entries in menus)
        {
            entries[0].Header.Should().Be("自动");
            entries[0].IsSelected.Should().BeTrue("nothing is stored yet");
            entries.Select(entry => entry.Header).Should().Contain("扫描文献预设");
            entries.Select(entry => entry.Header).Should().Contain("RapidOCR",
                "registered engines materialize a managed default preset at the list boundary");
            entries.Should().NotContain(entry => entry.Header == "已归档预设",
                "archived presets are not offered as new choices");
        }

        main.TranslationEngineMenuEntries.Should().NotBeEmpty();
        main.TranslationEngineMenuEntries.Select(entry => entry.Header).Should()
            .Contain("Llm");
    }

    [Fact]
    public async Task Refresh_materializes_managed_presets_for_registered_engines()
    {
        MainWindowViewModel main = await OpenMainAsync(
            nameof(Refresh_materializes_managed_presets_for_registered_engines));
        HostServices services = await main.ServicesAsync();

        await main.RefreshOcrPresetMenuAsync();

        // Presets are the pickers' only selectable unit: every shipped engine gets a managed
        // default even when its implicit-resolution path never ran (RapidOCR was unreachable).
        main.DocumentOcrPresetMenuEntries.Select(entry => entry.Header).Should()
            .Contain("RapidOCR");
        OcrPreset rapid = (await services.OcrPresets.ListPresetsAsync(false)).Value
            .Should().ContainSingle(preset => preset.Name == "RapidOCR").Subject;
        OcrPresetVersion version =
            (await services.OcrPresets.GetCurrentVersionAsync(rapid.PresetId)).Value;
        version.EngineId.Should().Be(OcrEngineIds.RapidOcr);
        version.ModelId.Should().Be(OcrModelIds.RapidOcrDefault);

        // Idempotent: a second refresh reuses the managed default instead of duplicating it.
        await main.RefreshOcrPresetMenuAsync();
        (await services.OcrPresets.ListPresetsAsync(false)).Value
            .Should().ContainSingle(preset => preset.Name == "RapidOCR");
    }

    [Fact]
    public async Task Selecting_a_preset_persists_the_scope_choice_and_checks_the_entry()
    {
        MainWindowViewModel main =
            await OpenMainAsync(nameof(Selecting_a_preset_persists_the_scope_choice_and_checks_the_entry));
        HostServices services = await main.ServicesAsync();
        OcrPreset preset = (await services.OcrPresets.CreatePresetAsync("Preset A", null,
            OcrEngineIds.MinerU, OcrModelIds.MinerUDefault, null, "{}", true)).Value;
        string id = preset.PresetId.ToString();
        await main.RefreshOcrPresetMenuAsync();

        await main.SelectOcrPresetAsync(OcrScope.Document, id);

        main.StatusIsError.Should().BeFalse();
        main.DocumentOcrPresetMenuEntries.Single(entry => entry.Header == "Preset A")
            .IsSelected.Should().BeTrue();
        main.DocumentOcrPresetMenuEntries[0].IsSelected.Should().BeFalse();
        main.PresetMenuSelectionFor(OcrScope.Document).Should().Be(id);
        // The choice is scoped: other scopes remain on 自动.
        main.PageOcrPresetMenuEntries[0].IsSelected.Should().BeTrue();
    }

    [Fact]
    public async Task Selecting_automatic_clears_the_stored_scope_choice()
    {
        MainWindowViewModel main = await OpenMainAsync(nameof(Selecting_automatic_clears_the_stored_scope_choice));
        HostServices services = await main.ServicesAsync();
        OcrPreset preset = (await services.OcrPresets.CreatePresetAsync("Preset A", null,
            OcrEngineIds.MinerU, OcrModelIds.MinerUDefault, null, "{}", true)).Value;
        await main.RefreshOcrPresetMenuAsync();
        await main.SelectOcrPresetAsync(OcrScope.Page, preset.PresetId.ToString());

        await main.SelectOcrPresetAsync(OcrScope.Page, null);

        main.PresetMenuSelectionFor(OcrScope.Page).Should().BeNull();
        main.PageOcrPresetMenuEntries[0].IsSelected.Should().BeTrue();
    }

    [Fact]
    public async Task Stale_stored_selection_stays_visible_and_selected_instead_of_remapping()
    {
        MainWindowViewModel main =
            await OpenMainAsync(nameof(Stale_stored_selection_stays_visible_and_selected_instead_of_remapping));
        HostServices services = await main.ServicesAsync();
        OcrPreset preset = (await services.OcrPresets.CreatePresetAsync("Preset A", null,
            OcrEngineIds.MinerU, OcrModelIds.MinerUDefault, null, "{}", true)).Value;
        string id = preset.PresetId.ToString();
        await main.RefreshOcrPresetMenuAsync();
        await main.SelectOcrPresetAsync(OcrScope.Region, id);

        // The preset disappears from the active listing while the sidecar still points at it.
        (await services.OcrPresets.ArchivePresetAsync(preset.PresetId)).IsSuccess.Should().BeTrue();
        await main.RefreshOcrPresetMenuAsync();

        OcrPresetMenuEntryViewModel stale = main.RegionOcrPresetMenuEntries.Should()
            .ContainSingle(entry => entry.Header.Contains(id)).Subject;
        stale.IsSelected.Should().BeTrue("the stored choice is honest about being unavailable");
        main.RegionOcrPresetMenuEntries[0].IsSelected.Should().BeFalse();
    }

    [Fact]
    public async Task Selecting_an_archived_preset_fails_visibly_and_keeps_the_previous_choice()
    {
        MainWindowViewModel main =
            await OpenMainAsync(nameof(Selecting_an_archived_preset_fails_visibly_and_keeps_the_previous_choice));
        HostServices services = await main.ServicesAsync();
        OcrPreset preset = (await services.OcrPresets.CreatePresetAsync("Preset A", null,
            OcrEngineIds.MinerU, OcrModelIds.MinerUDefault, null, "{}", true)).Value;
        string id = preset.PresetId.ToString();
        (await services.OcrPresets.ArchivePresetAsync(preset.PresetId)).IsSuccess.Should().BeTrue();
        await main.RefreshOcrPresetMenuAsync();

        await main.SelectOcrPresetAsync(OcrScope.Document, id);

        main.StatusIsError.Should().BeTrue();
        main.PresetMenuSelectionFor(OcrScope.Document).Should().BeNull();
    }

    [Fact]
    public async Task An_older_overlapping_refresh_can_never_overwrite_the_newer_result()
    {
        MainWindowViewModel main =
            await OpenMainAsync(nameof(An_older_overlapping_refresh_can_never_overwrite_the_newer_result));
        HostServices services = await main.ServicesAsync();

        TaskCompletionSource entered = new(), release = new();
        main.OcrMenuRefreshApplyGate = () =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        Task stale = main.RefreshOcrPresetMenuAsync();
        await entered.Task;

        // The older refresh holds a snapshot taken before this preset existed; the newer refresh
        // must win the apply epoch even though it completes first.
        OcrPreset preset = (await services.OcrPresets.CreatePresetAsync("后建预设", null,
            OcrEngineIds.MinerU, OcrModelIds.MinerUDefault, null, "{}", true)).Value;
        main.OcrMenuRefreshApplyGate = null;
        await main.RefreshOcrPresetMenuAsync();
        release.SetResult();
        await stale;

        main.DocumentOcrPresetMenuEntries.Select(entry => entry.Header).Should()
            .Contain("后建预设", "the later refresh's entries survive the stale completion");
    }

    [Fact]
    public async Task A_stale_entry_refuses_to_write_after_a_library_switch()
    {
        MainWindowViewModel main = await OpenMainAsync(nameof(A_stale_entry_refuses_to_write_after_a_library_switch));
        HostServices services = await main.ServicesAsync();
        OcrPreset preset = (await services.OcrPresets.CreatePresetAsync("Preset A", null,
            OcrEngineIds.MinerU, OcrModelIds.MinerUDefault, null, "{}", true)).Value;
        string id = preset.PresetId.ToString();
        await main.RefreshOcrPresetMenuAsync();

        // The entry was created under the current generation; a menu still open from the
        // previous library generation carries the old epoch and must not write.
        await main.SelectOcrPresetAsync(OcrScope.Document, id, main.LibraryGeneration - 1);

        main.StatusIsError.Should().BeTrue();
        main.PresetMenuSelectionFor(OcrScope.Document).Should().BeNull(
            "a stale entry never mutates the new library context");
    }

    [Fact]
    public async Task Translation_engine_selection_persists_and_marks_the_entry()
    {
        MainWindowViewModel main =
            await OpenMainAsync(nameof(Translation_engine_selection_persists_and_marks_the_entry));
        await main.RefreshOcrPresetMenuAsync();
        TranslationEngineMenuEntryViewModel entry =
            main.TranslationEngineMenuEntries.Should().ContainSingle(e => e.Header == "Deeplx")
                .Subject;

        await main.SelectTranslationEngineAsync("translation.deeplx");

        main.StatusIsError.Should().BeFalse();
        main.AppOptions.Llm.Translation.EngineId.Should().Be("translation.deeplx");
        entry.IsSelected.Should().BeTrue();
    }

    [Fact]
    public async Task Translation_engine_selection_keeps_every_other_translation_preference()
    {
        MainWindowViewModel main =
            await OpenMainAsync(nameof(Translation_engine_selection_keeps_every_other_translation_preference));
        PatchouliAppSettings updated = main.AppOptions with
        {
            Llm = main.AppOptions.Llm with
            {
                Translation = main.AppOptions.Llm.Translation with
                {
                    TargetLanguage = "zh-Hans",
                    Concurrency = 7,
                    Model = "model-keep"
                }
            }
        };
        (await main.UpdateAppOptionsAsync(updated, "Llm")).IsSuccess.Should().BeTrue();

        await main.SelectTranslationEngineAsync("translation.google-free");

        main.StatusIsError.Should().BeFalse();
        Llm.TranslationAppSettings translation = main.AppOptions.Llm.Translation;
        translation.EngineId.Should().Be("translation.google-free");
        translation.TargetLanguage.Should().Be("zh-Hans");
        translation.Concurrency.Should().Be(7);
        translation.Model.Should().Be("model-keep");
    }

    [Fact]
    public async Task An_unavailable_or_unknown_translation_id_fails_visibly()
    {
        MainWindowViewModel main = await OpenMainAsync(nameof(An_unavailable_or_unknown_translation_id_fails_visibly));
        string before = main.AppOptions.Llm.Translation.EngineId;

        await main.SelectTranslationEngineAsync("translation.ghost");

        main.StatusIsError.Should().BeTrue();
        main.AppOptions.Llm.Translation.EngineId.Should().Be(before,
            "an id the catalog cannot offer is never stored");
    }

    [Fact]
    public async Task A_blank_stored_engine_checks_the_effective_default_entry()
    {
        MainWindowViewModel main =
            await OpenMainAsync(nameof(A_blank_stored_engine_checks_the_effective_default_entry));
        await main.UpdateAppOptionsAsync(main.AppOptions with
        {
            Llm = main.AppOptions.Llm with
            {
                Translation = main.AppOptions.Llm.Translation with { EngineId = "" }
            }
        }, "Llm");

        await main.RefreshOcrPresetMenuAsync();

        main.EffectiveTranslationEngineId.Should().Be(TranslationSettings.DefaultEngineId);
        main.TranslationEngineMenuEntries.Single(entry =>
                entry.Header == "Microsoft Edge (free web)")
            .IsSelected.Should().BeTrue("blank resolves to the effective default, not a phantom");
        main.TranslationEngineMenuEntries.Should().NotContain(entry =>
                entry.Header.Contains("已选项不可用"),
            "the default engine id is a real catalog entry");
    }

    [Fact]
    public async Task Config_only_extension_directories_never_enter_the_translation_engine_menu()
    {
        MainWindowViewModel main = await OpenMainAsync(
            nameof(Config_only_extension_directories_never_enter_the_translation_engine_menu));
        HostServices services = await main.ServicesAsync();

        // A config/state overlay is not a source draft: SaveConfigAsync alone creates
        // extensions/{id}/ with no package payload — the translation.llm and ocr.multimodal
        // legacy seeds both leave directories shaped exactly like this.
        ExtensionConfigSnapshot seeded =
            new(new Dictionary<string, string> { ["seeded"] = "1" }, 1);
        await services.TranslationExtensions.Manager.SaveConfigAsync("ocr.multimodal", seeded);
        await services.TranslationExtensions.Manager.SaveConfigAsync("translation.llm", seeded);

        await main.RefreshOcrPresetMenuAsync();

        main.TranslationEngineMenuEntries.Should().NotContain(
            entry => entry.Header.Contains("ocr.multimodal"),
            "an OCR package is never a translation-engine candidate");
        main.TranslationEngineMenuEntries.Should().ContainSingle(entry => entry.Header == "Llm",
            "a config overlay must not shadow the bundled manifest as an unavailable entry");
    }

    [Fact]
    public async Task A_broken_working_copy_of_an_ocr_package_is_not_a_translation_engine()
    {
        MainWindowViewModel main = await OpenMainAsync(
            nameof(A_broken_working_copy_of_an_ocr_package_is_not_a_translation_engine));
        HostServices services = await main.ServicesAsync();

        // A corrupt edit under a bundled OCR id is a broken OCR source, not a broken translator.
        await services.TranslationExtensions.Manager.SavePackageAsync(new ExtensionPackageSnapshot(
            "ocr.multimodal", new Dictionary<string, string> { ["main.fsx"] = "let extension = 1" }));

        await main.RefreshOcrPresetMenuAsync();

        main.TranslationEngineMenuEntries.Should().NotContain(entry => entry.Header.Contains("ocr.multimodal"));
    }

    [Fact]
    public async Task A_broken_working_copy_of_an_unknown_draft_stays_listed_as_unavailable()
    {
        MainWindowViewModel main = await OpenMainAsync(
            nameof(A_broken_working_copy_of_an_unknown_draft_stays_listed_as_unavailable));
        HostServices services = await main.ServicesAsync();

        await services.TranslationExtensions.Manager.SavePackageAsync(new ExtensionPackageSnapshot(
            "translation.broken-draft",
            new Dictionary<string, string> { ["main.fsx"] = "let extension = 1" }));

        await main.RefreshOcrPresetMenuAsync();

        TranslationEngineMenuEntryViewModel broken = main.TranslationEngineMenuEntries.Should()
            .ContainSingle(entry => entry.Header.Contains("translation.broken-draft")).Subject;
        broken.Header.Should().Contain("不可用",
            "an unparseable draft whose kind is unknown stays honest about being unavailable");
    }

    [Fact]
    public async Task An_explicit_unavailable_stored_engine_stays_visible_and_checked()
    {
        MainWindowViewModel main =
            await OpenMainAsync(nameof(An_explicit_unavailable_stored_engine_stays_visible_and_checked));
        await main.UpdateAppOptionsAsync(main.AppOptions with
        {
            Llm = main.AppOptions.Llm with
            {
                Translation = main.AppOptions.Llm.Translation with { EngineId = "translation.gone" }
            }
        }, "Llm");

        await main.RefreshOcrPresetMenuAsync();

        TranslationEngineMenuEntryViewModel unavailable = main.TranslationEngineMenuEntries
            .Should().ContainSingle(entry => entry.Header.Contains("translation.gone")).Subject;
        unavailable.Header.Should().Contain("已选项不可用");
        unavailable.IsSelected.Should().BeTrue(
            "the explicit stored id stays truthfully checked, not remapped");
        main.TranslationEngineMenuEntries.Single(entry =>
                entry.Header == "Microsoft Edge (free web)")
            .IsSelected.Should().BeFalse();
    }

    [Fact]
    public async Task The_processing_menu_renders_the_screenshot_order_with_live_entries()
    {
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch(async () =>
        {
            MainWindowViewModel main = new(settingsPath: _settings.Path)
            {
                RuntimeDatabasePath = _settings.CreateDatabasePath("ocr-menu-tree")
            };
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            OcrPreset preset = (await services.OcrPresets.CreatePresetAsync("菜单预设", null,
                OcrEngineIds.MinerU, OcrModelIds.MinerUDefault, null, "{}", true)).Value;

            MainWindow window = new(main);
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();

                Menu topMenu = window.GetVisualDescendants().OfType<Menu>().Single();
                MenuItem processing = topMenu.Items.OfType<MenuItem>()
                    .Single(item => Equals(item.Header, "文献加工"));
                object?[] children = processing.Items.Cast<object?>().ToArray();

                children.Should().HaveCount(9);
                (children[0] as MenuItem)!.Header.Should().Be("运行 OCR");
                (children[1] as MenuItem)!.Header.Should().Be("快速补全 OCR");
                (children[2] as MenuItem)!.Header.Should().Be("选择 OCR 预设");
                children[3].Should().BeOfType<Separator>();
                (children[4] as MenuItem)!.Header.Should().Be("全文翻译");
                (children[5] as MenuItem)!.Header.Should().Be("切换翻译引擎");
                children[6].Should().BeOfType<Separator>();
                (children[7] as MenuItem)!.Header.Should().Be("任务队列");
                (children[8] as MenuItem)!.Header.Should().Be("文献加工设置");
                children.Count(child => child is Separator).Should().Be(2);

                MenuItem presetMenu = (MenuItem)children[2]!;
                presetMenu.Items.OfType<MenuItem>().Select(item => item.Header)
                    .Should().Equal("文档级", "页面级", "区域级");

                // Action bindings still resolve to real commands on the view model.
                ((MenuItem)children[0]!).Command.Should().NotBeNull();
                ((MenuItem)children[4]!).Command.Should().NotBeNull();
                ((MenuItem)children[7]!).Command.Should().NotBeNull();

                await main.RefreshOcrPresetMenuAsync();
                Dispatcher.UIThread.RunJobs();

                MenuItem documentScope = presetMenu.Items.OfType<MenuItem>()
                    .Single(item => Equals(item.Header, "文档级"));
                documentScope.Items.OfType<MenuItem>().Select(item => item.Header)
                    .Should().Contain("菜单预设").And.Contain("自动");
                ((OcrPresetMenuEntryViewModel)documentScope.Items[0]!).Header.Should().Be("自动");
                ((OcrPresetMenuEntryViewModel)documentScope.Items[0]!).IsSelected.Should().BeTrue();
                OcrPresetMenuEntryViewModel menuPreset = documentScope.Items
                    .OfType<OcrPresetMenuEntryViewModel>()
                    .Single(entry => Equals(entry.Header, "菜单预设"));

                // Invoking the bound command performs the real selection path.
                await menuPreset.SelectCommand.ExecuteAsync();
                main.PresetMenuSelectionFor(OcrScope.Document).Should()
                    .Be(preset.PresetId.ToString());

                MenuItem engineMenu = (MenuItem)children[5]!;
                engineMenu.Items.Count.Should().BeGreaterThan(0);
                engineMenu.Items.OfType<TranslationEngineMenuEntryViewModel>()
                    .Count(entry => entry.IsSelected).Should().Be(1);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }
}
