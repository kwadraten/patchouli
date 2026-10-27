using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Host;
using Patchouli.Host.Composition;
using Patchouli.Ocr;
using Patchouli.UI.ViewModels;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>
///     "OCR 预设编排" settings tab: per-scope preset selection (document/page/region)
///     plus the inline composition table. Rows combine registry components — an image OCR engine
///     and an independent text-layer provider — and save through
///     <see cref="IOcrPresetService.SaveCompositionAsync" /> so metadata + the new immutable
///     version move atomically. A component change freezes the components' current resolved
///     configuration via <see cref="OcrPresetCompositionService" />; a rename-only save never
///     touches the frozen snapshot. Engine/provider choices come from
///     <see cref="OcrExtensionEngineRuntime" /> registry metadata — never <c>OcrAdapters</c>,
///     which only represents the MinerU compatibility path now. Component configuration stays in
///     the component catalog dialogs; there is no preset modal.
/// </summary>
public sealed partial class OcrProviderSettingsViewModel : SettingsSectionViewModelBase
{
    private readonly MainWindowViewModel _main;
    private long _editRevision;
    private bool _isDirty;
    private bool _isConstructing;
    private bool _isSyncing;
    private IReadOnlyList<OcrEngineOption> _engineOptions = [];

    private IReadOnlyList<OcrTextLayerProviderOption> _providerOptions =
        [OcrTextLayerProviderOption.Disabled];

    private List<OcrPresetOption> _activePresetOptions = [];
    private OcrPresetSelections _persistedSelections = OcrPresetSelections.Empty;

    public OcrProviderSettingsViewModel(MainWindowViewModel main)
    {
        _isConstructing = true;
        _main = main;
        AddPresetRowCommand = new AsyncCommand(AddPresetRow);
        RefreshPresetRowsCommand = new AsyncCommand(async () =>
        {
            HostServices services = await _main.ServicesAsync();
            await RefreshPresetRowsAsync(services, CancellationToken.None);
        });
        DocumentPresetPicker = new OcrPresetScopePickerViewModel(OcrScope.Document, "文档级 OCR");
        PagePresetPicker = new OcrPresetScopePickerViewModel(OcrScope.Page, "页面级 OCR");
        RegionPresetPicker = new OcrPresetScopePickerViewModel(OcrScope.Region, "区域级 OCR");
        foreach (OcrPresetScopePickerViewModel picker in
                 new[] { DocumentPresetPicker, PagePresetPicker, RegionPresetPicker })
        {
            picker.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(OcrPresetScopePickerViewModel.SelectedOption) &&
                    !_isConstructing && !_isSyncing)
                {
                    UpdateDirtyState();
                    MarkDirty("有未保存的更改");
                }
            };
        }

        _isConstructing = false;
    }

    /// <summary>Header plus button — appends one unsaved composition row.</summary>
    public AsyncCommand AddPresetRowCommand { get; }

    /// <summary>Header refresh — reloads preset rows and scope selections from storage.</summary>
    public AsyncCommand RefreshPresetRowsCommand { get; }

    public OcrPresetScopePickerViewModel DocumentPresetPicker { get; }
    public OcrPresetScopePickerViewModel PagePresetPicker { get; }
    public OcrPresetScopePickerViewModel RegionPresetPicker { get; }

    /// <summary>The inline composition rows; unsaved rows survive refreshes.</summary>
    [ExcludeFromDerivedGeneration]
    public ObservableCollection<OcrPresetCompositionRowViewModel> PresetRows { get; } = new();

    [ObservableProperty] public partial string? PresetStatus { get; set; }

    public override bool SupportsEditing => true;

    [ExcludeFromDerivedGeneration] public override bool IsDirty => _isDirty;

    [ExcludeFromDerivedGeneration] public override bool CanSave => _isDirty;

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        long revision = _editRevision;
        try
        {
            HostServices services = await _main.ServicesAsync();
            int generation = _main.LibraryGeneration;
            // Registry metadata only — listing engines/providers never activates a package or
            // opens a session, so it is safe to read on the dispatcher.
            _engineOptions = services.OcrEngineRuntime.ListDefinitions()
                .Where(definition => !definition.IsDevelopmentOnly)
                .Select(definition => new OcrEngineOption(definition.EngineId,
                    definition.DisplayName))
                .ToArray();
            _providerOptions =
            [
                OcrTextLayerProviderOption.Disabled,
                ..services.OcrEngineRuntime.ListTextLayerDefinitions()
                    .Select(provider => new OcrTextLayerProviderOption(provider.ProviderId,
                        provider.DisplayName))
            ];
            await RefreshPresetRowsAsync(services, generation, cancellationToken);
            if (cancellationToken.IsCancellationRequested ||
                !_main.IsCurrentLibraryContext(services, generation) || revision != _editRevision)
            {
                return;
            }

            if (IsDirty)
            {
                Status = "OCR 设置有未保存的更改，已保留当前草稿。";
                return;
            }

            LastError = null;
            ValidationState = SettingsValidationState.Valid;
            Status = PresetStatus ?? "OCR 预设已加载。";
        }
        catch (Exception exception)
        {
            Status = $"加载 OCR 预设失败：{exception.Message}";
        }
    }

    public override Task DiscardAsync()
    {
        _isSyncing = true;
        try
        {
            DocumentPresetPicker.Apply(_activePresetOptions, _persistedSelections.DocumentPresetId);
            PagePresetPicker.Apply(_activePresetOptions, _persistedSelections.PagePresetId);
            RegionPresetPicker.Apply(_activePresetOptions, _persistedSelections.RegionPresetId);
        }
        finally
        {
            _isSyncing = false;
        }

        _isDirty = false;
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
        SaveState = SettingsSaveState.Clean;
        Status = "已放弃更改";
        return Task.CompletedTask;
    }

    /// <summary>Persists the three scope selections; composition rows save through their own commands.</summary>
    public override async Task SaveAsync()
    {
        SaveState = SettingsSaveState.Saving;
        Status = "正在保存...";

        HostServices services = await _main.ServicesAsync();
        OcrPresetSelections selections = new(
            SelectedPresetId(DocumentPresetPicker),
            SelectedPresetId(PagePresetPicker),
            SelectedPresetId(RegionPresetPicker));
        // Scopes still showing an "unavailable" stored id are left out of the write entirely —
        // an unrelated settings save must never clear a stale explicit choice or reassign it to
        // Automatic / the latest preset behind the user's back.
        Dictionary<OcrScope, OcrPresetId?> selectionWrites = new();
        if (!DocumentPresetPicker.SelectedOption.IsUnavailable)
        {
            selectionWrites[OcrScope.Document] = ParsePresetId(selections.DocumentPresetId);
        }

        if (!PagePresetPicker.SelectedOption.IsUnavailable)
        {
            selectionWrites[OcrScope.Page] = ParsePresetId(selections.PagePresetId);
        }

        if (!RegionPresetPicker.SelectedOption.IsUnavailable)
        {
            selectionWrites[OcrScope.Region] = ParsePresetId(selections.RegionPresetId);
        }

        // ADO.NET/file IO off the dispatcher — SQLite async often runs synchronously.
        Result selectionsSaved =
            await Task.Run(() => services.OcrSelections.SaveSelectionsAsync(selectionWrites));
        if (selectionsSaved.IsFailure)
        {
            LastError = selectionsSaved.ErrorMessage;
            SaveState = SettingsSaveState.Failed;
            Status = "预设选择保存失败";
            Raise(nameof(IsDirty));
            Raise(nameof(CanSave));
            return;
        }

        _persistedSelections = new OcrPresetSelections(
            DocumentPresetPicker.SelectedOption.IsUnavailable
                ? _persistedSelections.DocumentPresetId
                : selections.DocumentPresetId,
            PagePresetPicker.SelectedOption.IsUnavailable
                ? _persistedSelections.PagePresetId
                : selections.PagePresetId,
            RegionPresetPicker.SelectedOption.IsUnavailable
                ? _persistedSelections.RegionPresetId
                : selections.RegionPresetId);
        _editRevision++;
        UpdateDirtyState();
        LastError = null;
        SaveState = _isDirty ? SettingsSaveState.Dirty : SettingsSaveState.Saved;
        ValidationState = SettingsValidationState.Valid;
        Status = "已保存";
    }

    /// <summary>
    ///     Saves one composition row. A name-only change persists metadata and keeps the frozen
    ///     version; an engine/provider change captures the components' current resolved
    ///     configuration into a new immutable version via the composition service, then commits
    ///     metadata + version pointer atomically through <see cref="IOcrPresetService.SaveCompositionAsync" />.
    /// </summary>
    internal async Task SavePresetRowAsync(OcrPresetCompositionRowViewModel row)
    {
        await SavePresetRowAsync(row, false);
    }

    /// <summary>
    ///     Explicit row refresh: always recaptures the selected components into a NEW immutable
    ///     version — the documented way to fold edited component configuration into a preset.
    /// </summary>
    internal async Task RefreshPresetRowConfigAsync(OcrPresetCompositionRowViewModel row)
    {
        await SavePresetRowAsync(row, true);
    }

    private async Task SavePresetRowAsync(OcrPresetCompositionRowViewModel row, bool forceNewVersion)
    {
        if (string.IsNullOrWhiteSpace(row.Name))
        {
            row.MarkFailed("预设名称不能为空。");
            return;
        }

        if (row.SelectedEngine is null || string.IsNullOrWhiteSpace(row.SelectedEngineId))
        {
            row.MarkFailed("请选择 OCR 引擎。");
            return;
        }

        if (row.SelectedEngine.IsUnavailable)
        {
            row.MarkFailed($"引擎 '{row.SelectedEngineId}' 当前不可用；请重新选择可用的引擎后再保存。");
            return;
        }

        if (row.SelectedProvider is { IsUnavailable: true })
        {
            row.MarkFailed($"文本层提供器 '{row.SelectedProviderId}' 当前不可用；请重新选择后再保存。");
            return;
        }

        HostServices services = await _main.ServicesAsync();
        int generation = _main.LibraryGeneration;
        int rowRevision = row.EditRevision;
        // Capture UI state + app-level MinerU options on the dispatcher before background IO.
        MinerUAppSettings minerU = _main.AppOptions.MinerU;
        string name = row.Name.Trim();
        string engineId = row.SelectedEngineId;
        string? providerId = row.SelectedProviderId;
        OcrPresetVersion? baseline = row.BaselineVersion;
        bool applyOnSuccess = row.IsNew || row.PersistedApplyOnSuccess;
        string missingTextPolicy = row.SelectedMissingTextPolicy;
        bool componentsChanged = row.IsNew || forceNewVersion ||
                                 !string.Equals(engineId, row.PersistedEngineId ?? "",
                                     StringComparison.Ordinal) ||
                                 !string.Equals(
                                     OcrPresetCompositionRowViewModel.NormalizeProviderId(providerId),
                                     OcrPresetCompositionRowViewModel.NormalizeProviderId(
                                         row.PersistedProviderId),
                                     StringComparison.Ordinal) ||
                                 !string.Equals(missingTextPolicy,
                                     row.PersistedMissingTextPolicy, StringComparison.Ordinal);

        row.IsBusy = true;
        try
        {
            OcrPresetCompositionService composition = new(services, minerU);
            OcrPresetVersionWrite? write = null;
            if (componentsChanged)
            {
                Result<OcrPresetVersionWrite> captured = await Task.Run(() => composition.CaptureVersionAsync(engineId,
                    providerId, baseline,
                    applyOnSuccess, missingTextPolicy));
                if (captured.IsFailure)
                {
                    if (rowRevision == row.EditRevision)
                    {
                        row.MarkFailed($"组件配置捕获失败:{captured.ErrorMessage}");
                    }

                    return;
                }

                write = captured.Value;
            }

            Result<OcrPreset> saved;
            if (row.IsNew)
            {
                // First save creates the preset with the already-frozen version payload.
                saved = await Task.Run(() => services.OcrPresets.CreatePresetAsync(name, null,
                    write!.EngineId, write.ModelId, write.ModelPath, write.ParametersJson,
                    write.ApplyOnSuccess));
            }
            else
            {
                saved = await Task.Run(() => services.OcrPresets.SaveCompositionAsync(
                    row.PresetId!.Value, name, row.PersistedDescription,
                    row.BaselineVersionId, write));
            }

            if (!_main.IsCurrentLibraryContext(services, generation))
            {
                return;
            }

            if (saved.IsFailure)
            {
                if (rowRevision == row.EditRevision)
                {
                    if (saved.ErrorCode == AppErrorCodes.Conflict)
                    {
                        row.MarkConflict(
                            $"预设在编辑期间已被更新:{saved.ErrorMessage}（请刷新后重试）");
                    }
                    else
                    {
                        row.MarkFailed($"保存失败:{saved.ErrorMessage}");
                    }
                }

                return;
            }

            // Re-read the persisted state so the row's baseline matches the committed row —
            // including the version the atomic save just pointed the preset at.
            Result<OcrPresetVersion> version = saved.Value.CurrentVersionId is { } versionId
                ? await Task.Run(() => services.OcrPresets.GetPresetVersionAsync(versionId))
                : Result<OcrPresetVersion>.Failure(AppErrorCodes.NotFound, "Preset has no version.");
            Result<OcrPresetComposition> readback = version.IsSuccess
                ? await Task.Run(() => composition.ReadCompositionAsync(version.Value))
                : Result<OcrPresetComposition>.Failure(AppErrorCodes.NotFound, "No version.");
            if (rowRevision != row.EditRevision)
            {
                // The user kept editing while the save was in flight: the persisted baseline
                // still moves forward, but the row stays dirty with the newer edits visible.
                row.ApplyPersisted(saved.Value, version.IsSuccess ? version.Value : row.BaselineVersion,
                    readback.IsSuccess ? readback.Value : null, _engineOptions, _providerOptions,
                    "已保存（行内容随后又被修改）");
                return;
            }

            row.ApplyPersisted(saved.Value, version.IsSuccess ? version.Value : null,
                readback.IsSuccess ? readback.Value : null, _engineOptions, _providerOptions);
            PresetStatus = $"已保存预设「{saved.Value.Name}」。";
            await RefreshScopeSelectionsAsync(services, CancellationToken.None);
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    /// <summary>Archives a persisted row; an unsaved row is simply removed from the table.</summary>
    internal async Task ArchivePresetRowAsync(OcrPresetCompositionRowViewModel row)
    {
        if (row.IsNew)
        {
            PresetRows.Remove(row);
            return;
        }

        HostServices services = await _main.ServicesAsync();
        int generation = _main.LibraryGeneration;
        row.IsBusy = true;
        try
        {
            Result archived = await Task.Run(() =>
                services.OcrPresets.ArchivePresetAsync(row.PresetId!.Value));
            if (!_main.IsCurrentLibraryContext(services, generation))
            {
                return;
            }

            if (archived.IsFailure)
            {
                row.MarkFailed($"归档失败:{archived.ErrorMessage}");
                return;
            }

            row.MarkArchived();
            PresetStatus = "已归档;历史版本保留。";
            await RefreshScopeSelectionsAsync(services, CancellationToken.None);
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    private Task AddPresetRow()
    {
        PresetRows.Add(new OcrPresetCompositionRowViewModel(this, _engineOptions, _providerOptions));
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Compatibility surface for existing MainWindow call-sites: MinerU credential state now
    ///     lives on the MinerU component dialog; engine-token notifications no longer own state
    ///     here. Kept as thin no-ops so notification paths compile unchanged.
    /// </summary>
    internal void LoadPersistedToken(string token)
    {
    }

    internal void LoadPersistedCredential(string token)
    {
    }

    /// <summary>
    ///     Reloads preset rows, scope selections, and registry options. Clean rows refresh in
    ///     place; dirty/new rows are never rewritten; presets that vanished stay visible through
    ///     <see cref="OcrPresetCompositionRowViewModel.MarkRemoved" />.
    /// </summary>
    private async Task RefreshPresetRowsAsync(HostServices services, int generation,
        CancellationToken cancellationToken)
    {
        MinerUAppSettings minerU = _main.AppOptions.MinerU;
        // Presets are the only selectable unit: materialize each registered engine's managed
        // default before listing so an unused engine (e.g. RapidOCR) stays reachable here.
        try
        {
            await Task.Run(() => services.OcrDefaults.EnsureDefaultsAsync(cancellationToken),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            PresetStatus = $"预设默认值初始化失败:{exception.Message}";
        }

        Result<IReadOnlyList<OcrPreset>> presets = await Task.Run(
            () => services.OcrPresets.ListPresetsAsync(true, cancellationToken),
            cancellationToken);
        if (presets.IsFailure)
        {
            PresetStatus = $"预设列表不可用:{presets.ErrorMessage}";
            return;
        }

        // Resolve each preset's pinned version + composition readback off the dispatcher; a
        // preset whose version is unreadable still lists and reports its failure on the row.
        OcrPresetCompositionService composition = new(services, minerU);
        RowSnapshot[] snapshots = await Task.Run(async () =>
        {
            RowSnapshot[] resolved = new RowSnapshot[presets.Value.Count];
            for (int i = 0; i < presets.Value.Count; i++)
            {
                OcrPreset preset = presets.Value[i];
                OcrPresetVersion? version = null;
                OcrPresetComposition? readback = null;
                string? statusOverride = null;
                if (preset.CurrentVersionId is { } versionId)
                {
                    Result<OcrPresetVersion> current =
                        await services.OcrPresets.GetPresetVersionAsync(versionId, cancellationToken);
                    if (current.IsSuccess)
                    {
                        version = current.Value;
                        Result<OcrPresetComposition> compositionReadback =
                            await composition.ReadCompositionAsync(version, cancellationToken);
                        if (compositionReadback.IsSuccess)
                        {
                            readback = compositionReadback.Value;
                        }
                    }
                    else
                    {
                        statusOverride = $"版本读取失败:{current.ErrorMessage}";
                    }
                }
                else
                {
                    statusOverride = "无可执行版本";
                }

                resolved[i] = new RowSnapshot(preset, version, readback, statusOverride);
            }

            return resolved;
        }, cancellationToken);

        Result<OcrPresetSelections> stored = await Task.Run(
            () => services.OcrSelections.ReadStoredSelectionsAsync(cancellationToken),
            cancellationToken);
        if (cancellationToken.IsCancellationRequested ||
            !_main.IsCurrentLibraryContext(services, generation))
        {
            return;
        }

        ApplyPresetSnapshots(snapshots);
        OcrPresetSelections selections = stored.IsSuccess ? stored.Value : OcrPresetSelections.Empty;
        _persistedSelections = selections;
        _activePresetOptions = snapshots
            .Where(snapshot => !snapshot.Preset.Archived && snapshot.Version is not null)
            .Select(snapshot => new OcrPresetOption(
                snapshot.Preset.PresetId.ToString(),
                $"{snapshot.Preset.Name} ({snapshot.Version!.EngineId})",
                snapshot.Version!.EngineId))
            .ToList();

        _isSyncing = true;
        try
        {
            DocumentPresetPicker.Apply(_activePresetOptions, selections.DocumentPresetId);
            PagePresetPicker.Apply(_activePresetOptions, selections.PagePresetId);
            RegionPresetPicker.Apply(_activePresetOptions, selections.RegionPresetId);
        }
        finally
        {
            _isSyncing = false;
        }

        PresetStatus = stored.IsFailure ? $"预设选择读取失败:{stored.ErrorMessage}" : null;
        UpdateDirtyState();
    }

    private async Task RefreshPresetRowsAsync(HostServices services, CancellationToken cancellationToken)
    {
        await RefreshPresetRowsAsync(services, _main.LibraryGeneration, cancellationToken);
    }

    /// <summary>
    ///     Merge pass: clean rows update in place, dirty/unsaved rows are untouched, and rows
    ///     whose preset disappeared are marked removed rather than silently dropped.
    /// </summary>
    private void ApplyPresetSnapshots(RowSnapshot[] snapshots)
    {
        HashSet<OcrPresetId> seen = [];
        foreach (RowSnapshot snapshot in snapshots)
        {
            seen.Add(snapshot.Preset.PresetId);
            OcrPresetCompositionRowViewModel? row = PresetRows.FirstOrDefault(candidate =>
                candidate.PresetId == snapshot.Preset.PresetId);
            if (row is null)
            {
                PresetRows.Add(new OcrPresetCompositionRowViewModel(this, _engineOptions,
                    _providerOptions, snapshot.Preset, snapshot.Version, snapshot.Composition,
                    snapshot.StatusOverride));
            }
            else if (!row.IsDirty && !row.IsNew)
            {
                row.ApplyPersisted(snapshot.Preset, snapshot.Version, snapshot.Composition,
                    _engineOptions, _providerOptions, snapshot.StatusOverride);
            }
        }

        foreach (OcrPresetCompositionRowViewModel row in PresetRows)
        {
            if (row is { IsNew: false, IsDirty: false } && !seen.Contains(row.PresetId!.Value))
            {
                row.MarkRemoved();
            }
        }
    }

    /// <summary>
    ///     After a row save/archive the scope pickers may need the new/updated preset option;
    ///     selections are re-read, never guessed.
    /// </summary>
    private async Task RefreshScopeSelectionsAsync(HostServices services,
        CancellationToken cancellationToken)
    {
        await RefreshPresetRowsAsync(services, cancellationToken);
    }

    private static string? SelectedPresetId(OcrPresetScopePickerViewModel picker)
    {
        return string.IsNullOrWhiteSpace(picker.SelectedOption.PresetId)
            ? null
            : picker.SelectedOption.PresetId;
    }

    private static OcrPresetId? ParsePresetId(string? value)
    {
        return Guid.TryParse(value, out Guid guid) ? new OcrPresetId(guid) : null;
    }

    private void UpdateDirtyState()
    {
        _isDirty = SelectedPresetId(DocumentPresetPicker) != _persistedSelections.DocumentPresetId ||
                   SelectedPresetId(PagePresetPicker) != _persistedSelections.PagePresetId ||
                   SelectedPresetId(RegionPresetPicker) != _persistedSelections.RegionPresetId;
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
    }

    private void MarkDirty(string message)
    {
        _editRevision++;
        SaveState = SettingsSaveState.Dirty;
        Status = message;
    }

    /// <summary>One resolved row payload used by the off-dispatcher refresh pass.</summary>
    private sealed record RowSnapshot(
        OcrPreset Preset,
        OcrPresetVersion? Version,
        OcrPresetComposition? Composition,
        string? StatusOverride);
}
