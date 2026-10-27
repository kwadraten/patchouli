using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Translation;
using Patchouli.Extensions.Contracts;
using Patchouli.Host;
using Patchouli.Host.Composition;
using Patchouli.Ocr;

namespace Patchouli.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private OcrPresetSelections _presetMenuSelections = OcrPresetSelections.Empty;
    private int _presetMenuEpoch;
    private int _presetMenuSelectionsGeneration = -1;
    private int _presetMenuEntriesGeneration = -1;

    /// <summary>
    ///     Test seam: awaited inside <see cref="RefreshOcrPresetMenuAsync" /> after the
    ///     background reads complete but before any menu state is applied, so an older refresh
    ///     can be held past a newer one's epoch.
    /// </summary>
    internal Func<Task>? OcrMenuRefreshApplyGate { get; set; }

    [ExcludeFromDerivedGeneration]
    public ObservableCollection<OcrPresetMenuEntryViewModel> DocumentOcrPresetMenuEntries { get; } =
        new();

    [ExcludeFromDerivedGeneration]
    public ObservableCollection<OcrPresetMenuEntryViewModel> PageOcrPresetMenuEntries { get; } =
        new();

    [ExcludeFromDerivedGeneration]
    public ObservableCollection<OcrPresetMenuEntryViewModel> RegionOcrPresetMenuEntries { get; } =
        new();

    [ExcludeFromDerivedGeneration]
    public ObservableCollection<TranslationEngineMenuEntryViewModel> TranslationEngineMenuEntries { get; } = new();

    /// <summary>
    ///     The stored engine id resolved the same way the runtime does: a blank/missing value
    ///     normalizes to <see cref="TranslationSettings.DefaultEngineId" />, an explicit
    ///     unavailable id stays itself — never remapped.
    /// </summary>
    [ExcludeFromDerivedGeneration]
    internal string EffectiveTranslationEngineId
    {
        get
        {
            string? raw = AppOptions.Llm.Translation.EngineId;
            return string.IsNullOrWhiteSpace(raw) ? TranslationSettings.DefaultEngineId : raw.Trim();
        }
    }

    /// <summary>
    ///     Rebuilds the 文献加工 submenu: real per-library presets for each scope (from the preset
    ///     table, never guessed engine choices) plus the registered translation engines. Only the
    ///     latest refresh may apply — a slower overlapping read for the same host can never
    ///     overwrite newer choices. Read failures stay visible: a stored-selection failure never
    ///     reuses the previous library's ids, and a preset-list failure never wipes this
    ///     library's current entries.
    /// </summary>
    public async Task RefreshOcrPresetMenuAsync()
    {
        int epoch = ++_presetMenuEpoch;
        HostServices services = await ServicesAsync();
        int generation = LibraryGeneration;
        // Presets are the menu's only selectable unit: materialize each registered engine's
        // managed default before listing so an unused engine (e.g. RapidOCR) stays reachable.
        try
        {
            await Task.Run(() => services.OcrDefaults.EnsureDefaultsAsync());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportError($"无法初始化 OCR 引擎默认预设：{exception.Message}");
        }

        Result<OcrPresetSelections> stored =
            await Task.Run(() => services.OcrSelections.ReadStoredSelectionsAsync());
        Result<IReadOnlyList<OcrPreset>> presets =
            await Task.Run(() => services.OcrPresets.ListPresetsAsync(false));
        IReadOnlyList<TranslationEngineMenuItem> engines =
            await Task.Run(() => ListTranslationEnginesAsync(services));
        if (OcrMenuRefreshApplyGate is { } applyGate)
        {
            await applyGate();
        }

        if (epoch != _presetMenuEpoch || !IsCurrentLibraryContext(services, generation))
        {
            return;
        }

        if (stored.IsSuccess)
        {
            _presetMenuSelections = stored.Value;
            _presetMenuSelectionsGeneration = generation;
        }
        else
        {
            ReportError(stored.ErrorMessage ?? "无法读取当前文献库的 OCR 预设选择。");
            if (generation != _presetMenuSelectionsGeneration)
            {
                // The previous library's ids must never present as this library's choice.
                _presetMenuSelections = OcrPresetSelections.Empty;
            }
        }

        if (presets.IsFailure)
        {
            ReportError(presets.ErrorMessage ?? "无法列出当前文献库的 OCR 预设。");
            if (generation != _presetMenuEntriesGeneration)
            {
                ClearPresetMenuEntries();
            }
        }
        else
        {
            PopulatePresetMenu(DocumentOcrPresetMenuEntries, OcrScope.Document, presets.Value,
                _presetMenuSelections.DocumentPresetId, generation);
            PopulatePresetMenu(PageOcrPresetMenuEntries, OcrScope.Page, presets.Value,
                _presetMenuSelections.PagePresetId, generation);
            PopulatePresetMenu(RegionOcrPresetMenuEntries, OcrScope.Region, presets.Value,
                _presetMenuSelections.RegionPresetId, generation);
            _presetMenuEntriesGeneration = generation;
        }

        PopulateTranslationEngineMenu(engines, generation);
    }

    /// <summary>
    ///     Persists one scope's preset choice for the active library through the validating
    ///     selection service — a missing/archived/cross-library id fails visibly instead of
    ///     silently remapping. Null restores the implicit engine-default resolution. Menu
    ///     entries carry the library generation they were created for: a stale entry after a
    ///     library switch refuses to write rather than mutating the new host.
    /// </summary>
    internal async Task SelectOcrPresetAsync(OcrScope scope, string? presetId,
        int? expectedGeneration = null)
    {
        if (expectedGeneration is { } expected && expected != LibraryGeneration)
        {
            ReportError("文献库已切换；请重新打开文献加工菜单选择 OCR 预设。");
            return;
        }

        OcrPresetId? id;
        if (string.IsNullOrWhiteSpace(presetId))
        {
            id = null;
        }
        else if (Guid.TryParse(presetId, out Guid guid))
        {
            id = new OcrPresetId(guid);
        }
        else
        {
            ReportError($"预设标识 '{presetId}' 无效；请在设置中重新选择预设。");
            return;
        }

        HostServices services = await ServicesAsync();
        int generation = LibraryGeneration;
        if (!IsCurrentLibraryContext(services, generation))
        {
            return;
        }

        Result saved = await Task.Run(() => services.OcrSelections.SaveSelectionAsync(scope, id));
        if (!IsCurrentLibraryContext(services, generation))
        {
            // The save targeted the captured host; the new context owns its own UI/report state.
            return;
        }

        if (saved.IsFailure)
        {
            ReportError(saved.ErrorMessage ?? "无法保存 OCR 预设选择。");
            return;
        }

        _presetMenuSelections = _presetMenuSelections.WithPreset(scope, id?.ToString());
        _presetMenuSelectionsGeneration = generation;
        RefreshOcrPresetMenuChecks();
        Report("OCR 预设选择已保存。");
    }

    /// <summary>
    ///     Persists the translation engine choice. Before writing, the id is revalidated
    ///     against the same metadata-only catalog the menu listed — a stale entry (package
    ///     edited, removed, or re-kinded since the menu opened) or an unavailable marker fails
    ///     instead of storing an id that can never activate. Only
    ///     <see cref="Patchouli.Llm.TranslationAppSettings.EngineId" /> changes; every other
    ///     translation/model preference is read from the live settings record at merge time.
    /// </summary>
    internal async Task SelectTranslationEngineAsync(string engineId,
        int? expectedGeneration = null)
    {
        if (string.IsNullOrWhiteSpace(engineId))
        {
            ReportError("翻译引擎标识为空；请重新选择。");
            return;
        }

        if (expectedGeneration is { } expected && expected != LibraryGeneration)
        {
            ReportError("文献库已切换；请重新打开文献加工菜单选择翻译引擎。");
            return;
        }

        HostServices services = await ServicesAsync();
        int generation = LibraryGeneration;
        if (!IsCurrentLibraryContext(services, generation))
        {
            return;
        }

        IReadOnlyList<TranslationEngineMenuItem> engines =
            await Task.Run(() => ListTranslationEnginesAsync(services));
        TranslationEngineMenuItem? match = engines.FirstOrDefault(item =>
            string.Equals(item.Id, engineId, StringComparison.Ordinal));
        if (match is null || !match.Selectable)
        {
            ReportError(match is null
                ? $"翻译引擎 '{engineId}' 已不可用；请重新选择。"
                : $"翻译引擎 '{engineId}' 当前不可作为翻译引擎使用。");
            return;
        }

        // Field-level save: only the Llm.Translation.EngineId member changes — the record is
        // rebuilt from the live store so newer edits made elsewhere survive this write.
        PatchouliAppSettings current = _settingsStore.Current;
        SettingsSaveResult saved = await UpdateAppOptionsAsync(current with
        {
            Llm = current.Llm with
            {
                Translation = current.Llm.Translation with { EngineId = engineId }
            }
        }, "Llm");
        if (!IsCurrentLibraryContext(services, generation))
        {
            return;
        }

        if (!saved.IsSuccess)
        {
            ReportError(saved.ErrorMessage ?? "无法保存翻译引擎设置。");
            return;
        }

        RefreshTranslationEngineMenuChecks();
        Report("翻译引擎已切换。");
    }

    internal string? PresetMenuSelectionFor(OcrScope scope)
    {
        return _presetMenuSelections.PresetIdFor(scope);
    }

    internal void RefreshOcrPresetMenuChecks()
    {
        foreach (OcrPresetMenuEntryViewModel entry in DocumentOcrPresetMenuEntries
                     .Concat(PageOcrPresetMenuEntries)
                     .Concat(RegionOcrPresetMenuEntries))
        {
            entry.RefreshSelection();
        }
    }

    internal void RefreshTranslationEngineMenuChecks()
    {
        foreach (TranslationEngineMenuEntryViewModel entry in TranslationEngineMenuEntries)
        {
            entry.RefreshSelection();
        }
    }

    private void ClearPresetMenuEntries()
    {
        DocumentOcrPresetMenuEntries.Clear();
        PageOcrPresetMenuEntries.Clear();
        RegionOcrPresetMenuEntries.Clear();
    }

    private void PopulatePresetMenu(ObservableCollection<OcrPresetMenuEntryViewModel> entries,
        OcrScope scope, IReadOnlyList<OcrPreset> presets, string? stored, int generation)
    {
        entries.Clear();
        entries.Add(new OcrPresetMenuEntryViewModel(this, scope, null, "自动", generation));
        bool storedListed = string.IsNullOrWhiteSpace(stored);
        foreach (OcrPreset preset in presets)
        {
            string id = preset.PresetId.ToString();
            storedListed |= string.Equals(id, stored, StringComparison.Ordinal);
            entries.Add(new OcrPresetMenuEntryViewModel(this, scope, id, preset.Name, generation));
        }

        if (!storedListed)
        {
            // A stored choice that no longer resolves stays visible so the check mark is honest;
            // selecting it surfaces the validation error rather than remapping to another preset.
            entries.Add(new OcrPresetMenuEntryViewModel(this, scope, stored,
                $"已选项不可用 ({stored})", generation));
        }
    }

    /// <summary>
    ///     Rebuilds the translation engine submenu from discovered entries only. An explicit
    ///     stored id that no longer resolves is appended as a checkable unavailable marker —
    ///     honest about the persisted choice instead of silently checking the default.
    /// </summary>
    private void PopulateTranslationEngineMenu(IReadOnlyList<TranslationEngineMenuItem> engines,
        int generation)
    {
        string effectiveId = EffectiveTranslationEngineId;
        bool effectiveListed = false;
        TranslationEngineMenuEntries.Clear();
        foreach (TranslationEngineMenuItem engine in engines)
        {
            effectiveListed |= string.Equals(engine.Id, effectiveId, StringComparison.Ordinal);
            TranslationEngineMenuEntries.Add(new TranslationEngineMenuEntryViewModel(this,
                engine.Id, engine.Selectable ? engine.Name : $"{engine.Name}（不可用）", generation));
        }

        if (!effectiveListed)
        {
            // The stored id resolves to nothing the catalog offers — show it truthfully checked.
            TranslationEngineMenuEntries.Add(new TranslationEngineMenuEntryViewModel(this,
                effectiveId, $"已选项不可用 ({effectiveId})", generation));
        }
    }

    /// <summary>
    ///     Translation engine entries derived from real sources only: bundled package manifests,
    ///     working package sources, and already-active generations whose descriptors cover
    ///     source-broken translators. No package is checked, compiled, activated or run for the
    ///     menu — a broken draft that was never activated stays visible as an unavailable entry
    ///     and an id that merely resembles an engine is never offered as ready. Two states are
    ///     not translation-engine candidates at all: a working directory that carries no package
    ///     payload (a config/state overlay such as a seed written before first activation), and
    ///     an id whose metadata already declared another kind — a corrupt OCR package is a
    ///     broken OCR source, never a translation engine.
    /// </summary>
    private static async Task<IReadOnlyList<TranslationEngineMenuItem>> ListTranslationEnginesAsync(
        HostServices services)
    {
        Dictionary<string, TranslationEngineMenuItem> items = new(StringComparer.Ordinal);
        HashSet<string> brokenSources = new(StringComparer.Ordinal);
        HashSet<string> nonTranslationIds = new(StringComparer.Ordinal);

        void ApplyManifest(ExtensionPackageSnapshot package)
        {
            ExtensionEntryMetadata metadata;
            try
            {
                metadata = ExtensionEntryMetadata.Parse(package);
            }
            catch (ExtensionMetadataException)
            {
                items.Remove(package.Id);
                if (!nonTranslationIds.Contains(package.Id))
                {
                    brokenSources.Add(package.Id);
                }

                return;
            }

            if (metadata.Kind == ExtensionKind.Translation)
            {
                items[package.Id] = new TranslationEngineMenuItem(package.Id, metadata.Name, true);
                brokenSources.Remove(package.Id);
                nonTranslationIds.Remove(package.Id);
            }
            else
            {
                items.Remove(package.Id);
                brokenSources.Remove(package.Id);
                nonTranslationIds.Add(package.Id);
            }
        }

        foreach (ExtensionPackageSnapshot package in services.TranslationExtensions
                     .BundledPackageSnapshots)
        {
            ApplyManifest(package);
        }

        foreach (string id in services.TranslationExtensions.ListWorkingPackages())
        {
            ExtensionPackageSnapshot package;
            try
            {
                package = await services.TranslationExtensions.Manager.ReadSourceAsync(id);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A source being edited can be temporarily unreadable; bundled discovery stays.
                continue;
            }

            // Configuration and activation state live under the same working directory: a
            // directory with no package payload is an overlay, not a broken draft — it neither
            // shadows the bundled manifest nor enters the menu.
            if (package.Sources.Count == 0 && package.BinaryFiles.Count == 0)
            {
                continue;
            }

            ApplyManifest(package);
        }

        // An already-active generation is the authoritative identity for a translator whose
        // working source no longer parses — acquiring it compiles nothing, it only reuses the
        // pinned generation.
        foreach (ExtensionGenerationInfo generation in services.TranslationExtensions.Manager
                     .List())
        {
            if (!generation.IsActive || items.ContainsKey(generation.Id))
            {
                continue;
            }

            await using IExtensionLease? lease =
                await services.TranslationExtensions.Manager
                    .AcquireAsync(generation.Id, CancellationToken.None);
            if (lease?.Descriptor is { } descriptor)
            {
                if (descriptor.Kind == ExtensionKind.Translation)
                {
                    items[generation.Id] =
                        new TranslationEngineMenuItem(descriptor.Id, descriptor.Name, true);
                    nonTranslationIds.Remove(generation.Id);
                }
                else
                {
                    items.Remove(generation.Id);
                    nonTranslationIds.Add(generation.Id);
                }
            }
        }

        foreach (string id in brokenSources)
        {
            // Never-activated drafts that cannot be parsed stay listed as unavailable — the
            // stored selection may still point at them, and nothing else tells the truth. An id
            // already proven to be another kind can never be selected as a translator.
            if (!nonTranslationIds.Contains(id))
            {
                items.TryAdd(id, new TranslationEngineMenuItem(id, id, false));
            }
        }

        return items.Values.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
    }

    private sealed record TranslationEngineMenuItem(string Id, string Name, bool Selectable);
}

/// <summary>One checkable preset choice (or 自动) for a single OCR scope.</summary>
public sealed class OcrPresetMenuEntryViewModel : ViewModelBase
{
    private readonly int _generation;
    private readonly MainWindowViewModel _main;
    private readonly string? _presetId;
    private readonly OcrScope _scope;

    public OcrPresetMenuEntryViewModel(MainWindowViewModel main, OcrScope scope, string? presetId,
        string header, int generation)
    {
        _main = main;
        _scope = scope;
        _presetId = presetId;
        _generation = generation;
        Header = header;
        SelectCommand = new AsyncCommand(SelectAsync);
    }

    public string Header { get; }
    public AsyncCommand SelectCommand { get; }

    public bool IsSelected => _presetId is null
        ? string.IsNullOrWhiteSpace(_main.PresetMenuSelectionFor(_scope))
        : string.Equals(_presetId, _main.PresetMenuSelectionFor(_scope), StringComparison.Ordinal);

    internal void RefreshSelection()
    {
        Raise(nameof(IsSelected));
    }

    private Task SelectAsync()
    {
        // Entries created for a previous library context must never write the new host.
        return _main.SelectOcrPresetAsync(_scope, _presetId, _generation);
    }
}

/// <summary>One checkable translation engine entry in the 文献加工 menu.</summary>
public sealed class TranslationEngineMenuEntryViewModel : ViewModelBase
{
    private readonly string _engineId;
    private readonly int _generation;
    private readonly MainWindowViewModel _main;

    public TranslationEngineMenuEntryViewModel(MainWindowViewModel main, string engineId,
        string header, int generation)
    {
        _main = main;
        _engineId = engineId;
        _generation = generation;
        Header = header;
        SelectCommand = new AsyncCommand(SelectAsync);
    }

    public string Header { get; }
    public AsyncCommand SelectCommand { get; }

    public bool IsSelected =>
        string.Equals(_main.EffectiveTranslationEngineId, _engineId, StringComparison.Ordinal);

    internal void RefreshSelection()
    {
        Raise(nameof(IsSelected));
    }

    private Task SelectAsync()
    {
        return _main.SelectTranslationEngineAsync(_engineId, _generation);
    }
}
