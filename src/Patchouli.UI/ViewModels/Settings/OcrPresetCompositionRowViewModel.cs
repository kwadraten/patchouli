using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Host;
using Patchouli.Ocr;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>
///     One inline preset-composition row: preset name + image OCR engine + independent text-layer
///     provider, with icon-only save/archive/refresh actions. Rows only compose components —
///     component configuration itself stays in the component catalog's own dialogs. A save that
///     changes the engine/provider captures the components' current resolved configuration into a
///     NEW immutable <see cref="OcrPresetVersion" />; a rename-only save never touches the frozen
///     snapshot; the refresh action explicitly recaptures the selected components into a new
///     version. Unsaved edits survive list refreshes: the owner never rewrites a dirty row.
/// </summary>
public sealed partial class OcrPresetCompositionRowViewModel : ViewModelBase
{
    private readonly OcrProviderSettingsViewModel _owner;
    private int _editRevision;
    private bool _isApplying;

    private string _persistedName = "";
    private string? _persistedEngineId;
    private string? _persistedProviderId;
    private string? _persistedDescription;
    private string _persistedMissingTextPolicy = TextLayerMissingTextPolicies.PageOcr;
    private OcrPresetVersion? _baselineVersion;

    /// <summary>A freshly added row: not yet persisted; the first save creates the preset.</summary>
    internal OcrPresetCompositionRowViewModel(OcrProviderSettingsViewModel owner,
        IReadOnlyList<OcrEngineOption> engines, IReadOnlyList<OcrTextLayerProviderOption> providers)
        : this(owner)
    {
        RebuildEngineOptions(engines, null);
        RebuildProviderOptions(providers, null);
        Name = "";
        SelectedEngine = EngineOptions.FirstOrDefault();
        SelectedProvider = ProviderOptions.FirstOrDefault();
        IsDirty = true;
        StatusText = "新预设（尚未保存）";
    }

    /// <summary>A persisted row built from the preset, its current version and the readback.</summary>
    internal OcrPresetCompositionRowViewModel(OcrProviderSettingsViewModel owner,
        IReadOnlyList<OcrEngineOption> engines, IReadOnlyList<OcrTextLayerProviderOption> providers,
        OcrPreset preset, OcrPresetVersion? version, OcrPresetComposition? composition,
        string? statusOverride)
        : this(owner)
    {
        PresetId = preset.PresetId;
        ApplyPersisted(preset, version, composition, engines, providers, statusOverride);
        IsDirty = false;
    }

    private OcrPresetCompositionRowViewModel(OcrProviderSettingsViewModel owner)
    {
        _owner = owner;
        SaveCommand = new AsyncCommand(() => _owner.SavePresetRowAsync(this),
            () => CanSave && !IsBusy);
        RefreshConfigCommand = new AsyncCommand(() => _owner.RefreshPresetRowConfigAsync(this),
            () => CanEdit && !IsBusy);
        ArchiveCommand = new AsyncCommand(() => _owner.ArchivePresetRowAsync(this),
            () => !IsBusy && !IsArchived);
    }

    public OcrPresetId? PresetId { get; private set; }
    public bool IsNew => PresetId is null;

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial OcrEngineOption? SelectedEngine { get; set; }
    [ObservableProperty] public partial OcrTextLayerProviderOption? SelectedProvider { get; set; }

    /// <summary>
    ///     When a text-layer provider reports no usable native text, the page falls back to
    ///     whole-page image OCR by default; unchecking freezes the strict
    ///     <see cref="TextLayerMissingTextPolicies.Fail" /> policy into the next version.
    /// </summary>
    [ObservableProperty]
    public partial bool MissingTextFallback { get; set; } = true;

    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsDirty { get; private set; }
    [ObservableProperty] public partial bool IsArchived { get; private set; }
    [ObservableProperty] public partial bool IsRemoved { get; private set; }
    [ObservableProperty] public partial bool HasError { get; private set; }

    public ObservableCollection<OcrEngineOption> EngineOptions { get; } = new();
    public ObservableCollection<OcrTextLayerProviderOption> ProviderOptions { get; } = new();

    public AsyncCommand SaveCommand { get; }
    public AsyncCommand RefreshConfigCommand { get; }
    public AsyncCommand ArchiveCommand { get; }

    /// <summary>Archived or externally removed rows stay visible but stop editing.</summary>
    public bool CanEdit => !IsArchived && !IsRemoved;

    /// <summary>The fallback toggle is meaningful only while a real provider is selected.</summary>
    [ExcludeFromDerivedGeneration]
    public bool CanChooseMissingTextFallback => CanEdit && SelectedProvider is { IsUnavailable: false }
                                                        && !string.Equals(NormalizeProviderId(SelectedProviderId),
                                                            OcrTextLayerProviderOption.Disabled.ProviderId,
                                                            StringComparison.Ordinal);

    public bool CanSave => CanEdit && IsDirty;

    /// <summary>Bumped on every user edit; the owner re-checks it before applying async results.</summary>
    [ExcludeFromDerivedGeneration]
    internal int EditRevision => _editRevision;

    [ExcludeFromDerivedGeneration] internal OcrPresetVersion? BaselineVersion => _baselineVersion;

    [ExcludeFromDerivedGeneration] internal OcrPresetVersionId? BaselineVersionId => _baselineVersion?.PresetVersionId;

    [ExcludeFromDerivedGeneration] internal string? PersistedDescription => _persistedDescription;

    [ExcludeFromDerivedGeneration] internal bool PersistedApplyOnSuccess => _baselineVersion?.ApplyOnSuccess ?? true;

    [ExcludeFromDerivedGeneration] internal string? PersistedEngineId => _persistedEngineId;
    [ExcludeFromDerivedGeneration] internal string? PersistedProviderId => _persistedProviderId;
    internal string SelectedEngineId => SelectedEngine?.EngineId ?? "";
    internal string? SelectedProviderId => SelectedProvider?.ProviderId;

    internal string SelectedMissingTextPolicy => MissingTextFallback
        ? TextLayerMissingTextPolicies.PageOcr
        : TextLayerMissingTextPolicies.Fail;

    [ExcludeFromDerivedGeneration]
    internal string PersistedMissingTextPolicy =>
        _persistedMissingTextPolicy;

    /// <summary>null and the explicit host-only "disabled" marker describe the same routing.</summary>
    internal static string NormalizeProviderId(string? providerId)
    {
        return string.IsNullOrWhiteSpace(providerId)
            ? OcrTextLayerProviderOption.Disabled.ProviderId
            : providerId.Trim();
    }

    partial void OnNameChanged(string value)
    {
        if (!_isApplying)
        {
            _editRevision++;
            UpdateDirty();
        }
    }

    partial void OnSelectedEngineChanged(OcrEngineOption? value)
    {
        if (!_isApplying)
        {
            _editRevision++;
            UpdateDirty();
        }
    }

    partial void OnSelectedProviderChanged(OcrTextLayerProviderOption? value)
    {
        Raise(nameof(CanChooseMissingTextFallback));
        if (!_isApplying)
        {
            _editRevision++;
            UpdateDirty();
        }
    }

    partial void OnMissingTextFallbackChanged(bool value)
    {
        if (!_isApplying)
        {
            _editRevision++;
            UpdateDirty();
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        RefreshConfigCommand.NotifyCanExecuteChanged();
        ArchiveCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsDirtyChanged(bool value)
    {
        Raise(nameof(CanSave));
        SaveCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsArchivedChanged(bool value)
    {
        Raise(nameof(CanEdit));
        Raise(nameof(CanSave));
        Raise(nameof(CanChooseMissingTextFallback));
        SaveCommand.NotifyCanExecuteChanged();
        RefreshConfigCommand.NotifyCanExecuteChanged();
        ArchiveCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRemovedChanged(bool value)
    {
        Raise(nameof(CanEdit));
        Raise(nameof(CanSave));
        Raise(nameof(CanChooseMissingTextFallback));
        SaveCommand.NotifyCanExecuteChanged();
        RefreshConfigCommand.NotifyCanExecuteChanged();
    }

    private void UpdateDirty()
    {
        IsDirty = IsNew ||
                  !string.Equals(Name.Trim(), _persistedName, StringComparison.Ordinal) ||
                  !string.Equals(SelectedEngineId, _persistedEngineId ?? "",
                      StringComparison.Ordinal) ||
                  !string.Equals(NormalizeProviderId(SelectedProviderId),
                      NormalizeProviderId(_persistedProviderId), StringComparison.Ordinal) ||
                  !string.Equals(SelectedMissingTextPolicy, _persistedMissingTextPolicy,
                      StringComparison.Ordinal);
        if (IsDirty && !HasError)
        {
            StatusText = "有未保存的更改";
        }
    }

    /// <summary>
    ///     Rebuilds the engine options around the persisted engine id: an engine the registry no
    ///     longer offers stays visible as an explicit unavailable option — the row never silently
    ///     re-selects another engine.
    /// </summary>
    private void RebuildEngineOptions(IReadOnlyList<OcrEngineOption> engines, string? persistedEngineId)
    {
        EngineOptions.Clear();
        foreach (OcrEngineOption option in engines)
        {
            EngineOptions.Add(option);
        }

        if (!string.IsNullOrWhiteSpace(persistedEngineId) && EngineOptions.All(option =>
                !string.Equals(option.EngineId, persistedEngineId, StringComparison.Ordinal)))
        {
            EngineOptions.Add(OcrEngineOption.Unavailable(persistedEngineId));
        }
    }

    private void RebuildProviderOptions(IReadOnlyList<OcrTextLayerProviderOption> providers,
        string? persistedProviderId)
    {
        ProviderOptions.Clear();
        foreach (OcrTextLayerProviderOption option in providers)
        {
            ProviderOptions.Add(option);
        }

        if (!string.IsNullOrWhiteSpace(persistedProviderId) &&
            !string.Equals(persistedProviderId, OcrTextLayerProviderOption.Disabled.ProviderId,
                StringComparison.Ordinal) &&
            ProviderOptions.All(option =>
                !string.Equals(option.ProviderId, persistedProviderId, StringComparison.Ordinal)))
        {
            ProviderOptions.Add(OcrTextLayerProviderOption.Unavailable(persistedProviderId));
        }
    }

    /// <summary>
    ///     Applies a fresh persisted snapshot to a CLEAN row (dirty/new rows are never touched by
    ///     the owner). <paramref name="providerKnown" /> is false when the stored envelope is
    ///     unreadable — the previous provider selection is then preserved instead of being
    ///     blanked out.
    /// </summary>
    internal void ApplyPersisted(OcrPreset preset, OcrPresetVersion? version,
        OcrPresetComposition? composition, IReadOnlyList<OcrEngineOption> engines,
        IReadOnlyList<OcrTextLayerProviderOption> providers, string? statusOverride = null)
    {
        bool providerKnown = composition is { IsValid: true };
        string? providerId = providerKnown ? composition!.TextLayerProviderId : _persistedProviderId;
        string? engineId = version?.EngineId ?? _persistedEngineId;

        _isApplying = true;
        try
        {
            PresetId = preset.PresetId;
            _persistedName = preset.Name;
            _persistedDescription = preset.Description;
            _persistedEngineId = engineId;
            _persistedProviderId = providerId;
            _baselineVersion = version ?? _baselineVersion;
            _persistedMissingTextPolicy = ReadMissingTextPolicy(version) ??
                                          _persistedMissingTextPolicy;
            IsArchived = preset.Archived;
            IsRemoved = false;
            HasError = false;

            Name = preset.Name;
            MissingTextFallback = !string.Equals(_persistedMissingTextPolicy,
                TextLayerMissingTextPolicies.Fail, StringComparison.Ordinal);
            RebuildEngineOptions(engines, engineId);
            RebuildProviderOptions(providers, providerId);
            SelectedEngine = EngineOptions.FirstOrDefault(option =>
                string.Equals(option.EngineId, engineId, StringComparison.Ordinal));
            SelectedProvider = ProviderOptions.FirstOrDefault(option =>
                                   string.Equals(option.ProviderId, NormalizeProviderId(providerId),
                                       StringComparison.Ordinal)) ??
                               OcrTextLayerProviderOption.Disabled;
        }
        finally
        {
            _isApplying = false;
        }

        IsDirty = IsNew;
        StatusText = statusOverride ?? ComposeStatus(version, composition);
    }

    /// <summary>The preset vanished from the library listing — stays visible and actionable.</summary>
    internal void MarkRemoved()
    {
        IsRemoved = true;
        HasError = true;
        StatusText = "预设已不在当前文献库中（可能已被移除）；请刷新或归档此行。";
    }

    /// <summary>Archive committed server-side; the row freezes into its read-only state.</summary>
    internal void MarkArchived()
    {
        IsArchived = true;
        HasError = false;
        StatusText = "已归档；历史版本保留。";
    }

    /// <summary>A failed save/refresh keeps every edit; the row only reports the failure.</summary>
    internal void MarkFailed(string message)
    {
        HasError = true;
        StatusText = message;
        UpdateDirty();
    }

    /// <summary>The persisted baseline moved under this row — the stale row stays editable.</summary>
    internal void MarkConflict(string message)
    {
        HasError = true;
        StatusText = message;
        UpdateDirty();
    }

    private string ComposeStatus(OcrPresetVersion? version, OcrPresetComposition? composition)
    {
        if (IsArchived)
        {
            return "已归档";
        }

        if (version is null)
        {
            return "无可执行版本";
        }

        if (composition is null)
        {
            return $"版本 {ShortId(version.PresetVersionId.ToString())}";
        }

        if (!composition.IsValid)
        {
            return $"配置损坏：{composition.ParseError}";
        }

        if (!composition.EngineAvailable)
        {
            return $"引擎 '{composition.EngineId}' 当前不可用";
        }

        if (!composition.ProviderAvailable)
        {
            return $"文本层提供器 '{composition.TextLayerProviderId}' 当前不可用";
        }

        string provider = NormalizeProviderId(composition.TextLayerProviderId);
        string providerName = provider == OcrTextLayerProviderOption.Disabled.ProviderId
            ? "无文本层"
            : provider;
        return $"版本 {ShortId(version.PresetVersionId.ToString())} · {providerName}";
    }

    private static string ShortId(string id)
    {
        return id.Length > 8 ? id[..8] : id;
    }

    /// <summary>
    ///     Lenient envelope read for the row's persisted policy — a malformed or absent version
    ///     keeps the previous value rather than silently flipping the toggle.
    /// </summary>
    private static string? ReadMissingTextPolicy(OcrPresetVersion? version)
    {
        if (version is null)
        {
            return null;
        }

        Result<OcrPresetConfiguration> read = OcrPresetConfiguration.TryRead(version);
        return read is { IsSuccess: true, Value.IsValid: true }
            ? read.Value.TextLayerMissingTextPolicy
            : null;
    }
}
