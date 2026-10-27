using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Extensions.Contracts;
using Patchouli.Host;
using Patchouli.Ocr;
using Patchouli.UI;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.Tests;

/// <summary>
///     Inline preset-composition surface (the modal editor is gone): scope pickers keep stored
///     choices visible — including unresolvable ones — instead of silently resetting to
///     Automatic; composition rows freeze stored engine/provider choices as explicit
///     "unavailable" options rather than re-selecting another component; and row failure/conflict
///     marks preserve in-flight edits.
/// </summary>
public sealed class OcrPresetInlineEditorTests
{
    private static readonly IReadOnlyList<OcrEngineOption> Engines =
    [
        new("rapidocr", "RapidOCR"),
        new(OcrEngineIds.MinerU, "MinerU")
    ];

    private static readonly IReadOnlyList<OcrTextLayerProviderOption> Providers =
    [
        OcrTextLayerProviderOption.Disabled,
        new(PdfTextLayerProviderIds.VisualGuided, "视觉引导文本层")
    ];

    private static OcrPreset Preset(string name = "Preset", bool archived = false)
    {
        return new OcrPreset(OcrPresetId.New(), LibraryId.New(), name, null,
            OcrPresetVersionId.New(), archived, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    private static OcrPresetVersion Version(OcrPreset preset, string engineId = "rapidocr")
    {
        return new OcrPresetVersion(preset.CurrentVersionId ?? OcrPresetVersionId.New(),
            preset.PresetId, engineId, "model-x", null, "{}", true, DateTimeOffset.UtcNow);
    }

    private static OcrPresetCompositionRowViewModel PersistedRow(
        OcrProviderSettingsViewModel owner, OcrPreset preset, OcrPresetVersion version,
        OcrPresetComposition? composition = null, string? statusOverride = null)
    {
        return new OcrPresetCompositionRowViewModel(owner, Engines, Providers, preset, version,
            composition, statusOverride);
    }

    private static OcrProviderSettingsViewModel CreateOwner(TemporaryAppSettingsFile file)
    {
        return new OcrProviderSettingsViewModel(new MainWindowViewModel(settingsPath: file.Path));
    }

    [Fact]
    public void Scope_picker_keeps_resolvable_stored_selection()
    {
        OcrPresetScopePickerViewModel picker = new(OcrScope.Document, "文档级 OCR");
        OcrPresetOption option = new(OcrPresetId.New().ToString(), "Preset (rapidocr)", "rapidocr");
        picker.Apply([option], option.PresetId);
        picker.SelectedOption.Should().BeSameAs(option);
    }

    [Fact]
    public void Scope_picker_preserves_unresolvable_stored_id_as_unavailable_not_automatic()
    {
        OcrPresetScopePickerViewModel picker = new(OcrScope.Region, "区域级 OCR");
        string staleId = OcrPresetId.New().ToString();
        picker.Apply([], staleId);
        picker.SelectedOption.IsUnavailable.Should().BeTrue();
        picker.SelectedOption.PresetId.Should().Be(staleId);
        picker.SelectedOption.Should().NotBeSameAs(OcrPresetOption.Automatic);
    }

    [Fact]
    public void Scope_picker_falls_back_to_automatic_only_when_nothing_is_stored()
    {
        OcrPresetScopePickerViewModel picker = new(OcrScope.Region, "区域级 OCR");
        picker.Apply([], null);
        picker.SelectedOption.Should().BeSameAs(OcrPresetOption.Automatic);
        picker.SelectedOption.PresetId.Should().BeEmpty();
    }

    [Fact]
    public void New_composition_row_starts_dirty_and_editable()
    {
        using TemporaryAppSettingsFile file = new();
        OcrPresetCompositionRowViewModel row = new(CreateOwner(file), Engines, Providers);
        row.IsNew.Should().BeTrue();
        row.IsDirty.Should().BeTrue();
        row.CanEdit.Should().BeTrue();
        row.CanSave.Should().BeTrue();
        row.SelectedProvider.Should().BeSameAs(OcrTextLayerProviderOption.Disabled);
    }

    [Fact]
    public void Persisted_row_resolves_stored_engine_and_provider()
    {
        using TemporaryAppSettingsFile file = new();
        OcrPreset preset = Preset();
        OcrPresetVersion version = Version(preset);
        OcrPresetComposition composition = new("rapidocr", PdfTextLayerProviderIds.VisualGuided,
            true, true, true, null);

        OcrPresetCompositionRowViewModel row = PersistedRow(CreateOwner(file), preset, version,
            composition);

        row.IsDirty.Should().BeFalse();
        row.SelectedEngine!.EngineId.Should().Be("rapidocr");
        row.SelectedProvider!.ProviderId.Should().Be(PdfTextLayerProviderIds.VisualGuided);
    }

    [Fact]
    public void Stored_engine_missing_from_registry_stays_selected_as_unavailable()
    {
        using TemporaryAppSettingsFile file = new();
        OcrPreset preset = Preset();
        OcrPresetVersion version = Version(preset, "ndl-koten");
        OcrPresetComposition composition = new("ndl-koten", null, false,
            true, true, null);

        OcrPresetCompositionRowViewModel row = PersistedRow(CreateOwner(file), preset, version,
            composition);

        row.SelectedEngine!.EngineId.Should().Be("ndl-koten");
        row.SelectedEngine.IsUnavailable.Should().BeTrue();
        row.StatusText.Should().Contain("ndl-koten");
    }

    [Fact]
    public void Stored_provider_missing_from_registry_stays_selected_as_unavailable()
    {
        using TemporaryAppSettingsFile file = new();
        OcrPreset preset = Preset();
        OcrPresetVersion version = Version(preset);
        OcrPresetComposition composition = new("rapidocr", PdfTextLayerProviderIds.NativeXyCut,
            true, false, true, null);

        OcrPresetCompositionRowViewModel row = PersistedRow(CreateOwner(file), preset, version,
            composition);

        row.SelectedProvider!.ProviderId.Should().Be(PdfTextLayerProviderIds.NativeXyCut);
        row.SelectedProvider.IsUnavailable.Should().BeTrue();
        row.StatusText.Should().Contain(PdfTextLayerProviderIds.NativeXyCut);
    }

    [Fact]
    public void Malformed_composition_preserves_the_prior_provider_selection()
    {
        using TemporaryAppSettingsFile file = new();
        OcrProviderSettingsViewModel owner = CreateOwner(file);
        OcrPreset preset = Preset();
        OcrPresetVersion version = Version(preset);
        OcrPresetCompositionRowViewModel row = PersistedRow(owner, preset, version,
            new OcrPresetComposition("rapidocr", null, true, false, false, "broken envelope"));

        // Unreadable envelope: the provider field stays untouched rather than being blanked out.
        row.SelectedProvider.Should().BeSameAs(OcrTextLayerProviderOption.Disabled);
        row.StatusText.Should().Contain("broken");
    }

    [Fact]
    public void Rename_edit_flags_dirty_without_touching_the_frozen_baseline()
    {
        using TemporaryAppSettingsFile file = new();
        OcrPreset preset = Preset();
        OcrPresetVersion version = Version(preset);
        OcrPresetCompositionRowViewModel row = PersistedRow(CreateOwner(file), preset, version,
            new OcrPresetComposition("rapidocr", null, true, true, true, null));
        OcrPresetVersionId baselineId = row.BaselineVersionId!.Value;

        row.Name = "Renamed";
        row.IsDirty.Should().BeTrue();
        row.CanSave.Should().BeTrue();
        row.BaselineVersionId.Should().Be(baselineId);
    }

    [Fact]
    public void Failed_or_conflicting_save_preserves_the_row_edits()
    {
        using TemporaryAppSettingsFile file = new();
        OcrPreset preset = Preset();
        OcrPresetVersion version = Version(preset);
        OcrPresetCompositionRowViewModel row = PersistedRow(CreateOwner(file), preset, version, null);

        row.Name = "Kept edit";
        row.MarkFailed("保存失败:x");
        row.IsDirty.Should().BeTrue();
        row.Name.Should().Be("Kept edit");
        row.HasError.Should().BeTrue();

        row.MarkConflict("预设在编辑期间已被更新");
        row.IsDirty.Should().BeTrue();
        row.CanEdit.Should().BeTrue("a stale row stays actionable instead of being frozen");
    }

    [Fact]
    public void Removed_or_archived_rows_stay_visible_but_read_only()
    {
        using TemporaryAppSettingsFile file = new();
        OcrProviderSettingsViewModel owner = CreateOwner(file);
        OcrPresetCompositionRowViewModel removed = PersistedRow(owner, Preset(), Version(Preset()), null);
        removed.MarkRemoved();
        removed.IsRemoved.Should().BeTrue();
        removed.CanEdit.Should().BeFalse();
        removed.CanSave.Should().BeFalse();
        removed.StatusText.Should().NotBeNullOrWhiteSpace();

        OcrPresetCompositionRowViewModel archived = PersistedRow(owner, Preset(), Version(Preset()), null);
        archived.MarkArchived();
        archived.IsArchived.Should().BeTrue();
        archived.CanEdit.Should().BeFalse();
        archived.StatusText.Should().Contain("已归档");
    }

    [Fact]
    public void Provider_id_normalization_treats_empty_and_disabled_as_equivalent()
    {
        OcrPresetCompositionRowViewModel.NormalizeProviderId(null).Should().Be("disabled");
        OcrPresetCompositionRowViewModel.NormalizeProviderId("").Should().Be("disabled");
        OcrPresetCompositionRowViewModel.NormalizeProviderId(" disabled ").Should().Be("disabled");
        OcrPresetCompositionRowViewModel.NormalizeProviderId("native-xycut").Should()
            .Be("native-xycut");
    }
}
