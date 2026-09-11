using System.Collections.ObjectModel;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Search;
using Patchouli.Host.Composition;

namespace Patchouli.UI.ViewModels.Settings;

/// <summary>
/// Editable query-rewrite rule table for the current library. Global rules (<c>ProfileId</c> is null)
/// and per-profile rules are displayed together. The enabled checkbox is dirty-tracked like every
/// other table cell and is committed by the shared settings save command.
/// </summary>
public sealed class SearchRewriteSettingsViewModel : SettingsSectionViewModelBase
{
    private readonly MainWindowViewModel _main;
    private readonly List<SearchRewriteRuleId> _pendingDeletions = [];
    private bool _isDirty;
    private string _previewQuery = "";
    private string _previewSummary = "输入示例查询后点击「预览」。预览按需构建重写计划，不受「启用查询重写」开关影响。";
    private bool _previewIsError;

    public SearchRewriteSettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        AddRuleCommand = new RelayCommand(_ => AddRule());
        SaveCommand = new AsyncCommand(SaveAsync);
        DiscardCommand = new AsyncCommand(DiscardAsync);
        PreviewCommand = new AsyncCommand(PreviewAsync);
        OpenccOptions = new ObservableCollection<string>(OpenccConfigs.All);
        RuleTypeOptions =
        [
            new SearchRewriteRuleTypeOption(SearchRuleType.Literal, "字面匹配"),
            new SearchRewriteRuleTypeOption(SearchRuleType.Variant, "地区/异体变体"),
            new SearchRewriteRuleTypeOption(SearchRuleType.Regex, "正则表达式"),
            new SearchRewriteRuleTypeOption(SearchRuleType.Synonym, "同义词"),
            new SearchRewriteRuleTypeOption(SearchRuleType.OcrConfusion, "OCR 混淆"),
            new SearchRewriteRuleTypeOption(SearchRuleType.SimplifiedTraditional, "简繁转换 (OpenCC)")
        ];
        DirectionOptions =
        [
            new SearchRewriteDirectionOption(SearchRewriteDirection.Expand, "展开"),
            new SearchRewriteDirectionOption(SearchRewriteDirection.Replace, "替换"),
            new SearchRewriteDirectionOption(SearchRewriteDirection.Bidirectional, "双向")
        ];
        Scopes.Add(SearchRewriteScopeOption.Global);
    }

    public ObservableCollection<SearchRewriteRuleRowViewModel> Rules { get; } = [];
    public ObservableCollection<SearchRewriteScopeOption> Scopes { get; } = [];
    public ObservableCollection<string> OpenccOptions { get; }
    public IReadOnlyList<SearchRewriteRuleTypeOption> RuleTypeOptions { get; }
    public IReadOnlyList<SearchRewriteDirectionOption> DirectionOptions { get; }

    public RelayCommand AddRuleCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand DiscardCommand { get; }
    public AsyncCommand PreviewCommand { get; }

    public override bool SupportsEditing => true;
    public override bool IsDirty => _isDirty;
    public override bool CanSave => IsDirty;

    public string PreviewQuery
    {
        get => _previewQuery;
        set
        {
            if (_previewQuery != value)
            {
                _previewQuery = value;
                Raise();
            }
        }
    }

    public string PreviewSummary
    {
        get => _previewSummary;
        private set
        {
            if (_previewSummary != value)
            {
                _previewSummary = value;
                Raise();
            }
        }
    }

    public bool PreviewIsError
    {
        get => _previewIsError;
        private set
        {
            if (_previewIsError != value)
            {
                _previewIsError = value;
                Raise();
            }
        }
    }

    public ObservableCollection<string> PreviewExpandedQueries { get; } = [];
    public ObservableCollection<string> PreviewWarnings { get; } = [];

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            HostServices services = await _main.ServicesAsync();
            Result<IReadOnlyList<SearchProfile>> profiles =
                await services.SearchProfiles.ListProfilesAsync(true, cancellationToken);
            Scopes.Clear();
            Scopes.Add(SearchRewriteScopeOption.Global);
            if (profiles.IsSuccess)
            {
                foreach (SearchProfile profile in profiles.Value)
                {
                    Scopes.Add(new SearchRewriteScopeOption(FormatProfileName(profile), profile.ProfileId));
                }
            }

            Result<IReadOnlyList<SearchRewriteRule>> rules =
                await services.SearchProfiles.ListRulesAsync(null, true, cancellationToken);
            Rules.Clear();
            _pendingDeletions.Clear();
            if (rules.IsSuccess)
            {
                foreach (SearchRewriteRule rule in rules.Value)
                {
                    Rules.Add(CreateRow(rule));
                }

                SetDirty(false);
                SaveState = SettingsSaveState.Saved;
                ValidationState = SettingsValidationState.Valid;
                Status = "已加载";
                LastError = null;
            }
            else
            {
                SetDirty(false);
                SaveState = SettingsSaveState.Failed;
                LastError = rules.ErrorMessage;
                Status = $"加载失败：{rules.ErrorMessage}";
            }
        }
        catch (Exception exception)
        {
            SetDirty(false);
            SaveState = SettingsSaveState.Failed;
            LastError = exception.Message;
            Status = $"加载失败：{exception.Message}";
        }
    }

    public override async Task SaveAsync()
    {
        SaveState = SettingsSaveState.Saving;
        Status = "正在保存...";
        try
        {
            HostServices services = await _main.ServicesAsync();
            List<string> errors = [];
            foreach (SearchRewriteRuleRowViewModel row in Rules.ToArray())
            {
                Result committed = await row.CommitAsync(services);
                if (committed.IsFailure)
                {
                    errors.Add($"{row.DisplayLabel}：{committed.ErrorMessage}");
                }
            }

            foreach (SearchRewriteRuleId ruleId in _pendingDeletions)
            {
                Result deleted = await services.SearchProfiles.DeleteRuleAsync(ruleId);
                if (deleted.IsFailure)
                {
                    errors.Add($"删除规则：{deleted.ErrorMessage}");
                }
            }

            if (errors.Count > 0)
            {
                SaveState = SettingsSaveState.Failed;
                LastError = string.Join("；", errors);
                Status = $"保存失败：{LastError}";
                return;
            }

            _pendingDeletions.Clear();
            await LoadAsync();
            SaveState = SettingsSaveState.Saved;
            ValidationState = SettingsValidationState.Valid;
            Status = "已保存";
            LastError = null;
        }
        catch (Exception exception)
        {
            SaveState = SettingsSaveState.Failed;
            LastError = exception.Message;
            Status = $"保存失败：{exception.Message}";
        }
    }

    public override async Task DiscardAsync()
    {
        await LoadAsync();
        SaveState = SettingsSaveState.Clean;
        Status = "已放弃更改";
        LastError = null;
    }

    internal void AddRule()
    {
        Rules.Add(CreateRow(null,
            Scopes.FirstOrDefault() ?? SearchRewriteScopeOption.Global,
            RuleTypeOptions[0],
            DirectionOptions[0],
            "",
            "",
            0,
            null,
            true));
        MarkDirty();
    }

    internal void Remove(SearchRewriteRuleRowViewModel row)
    {
        if (row.RuleId is { } ruleId)
        {
            _pendingDeletions.Add(ruleId);
        }

        Rules.Remove(row);
        MarkDirty();
    }

    internal void MarkDirty()
    {
        SetDirty(true);
        SaveState = SettingsSaveState.Dirty;
        Status = "有未保存的更改";
    }

    private SearchRewriteRuleRowViewModel CreateRow(SearchRewriteRule? rule, SearchRewriteScopeOption? scope = null,
        SearchRewriteRuleTypeOption? ruleType = null, SearchRewriteDirectionOption? direction = null,
        string pattern = "", string replacement = "", int priority = 0, string? note = null, bool enabled = true)
    {
        if (rule is not null)
        {
            scope = Scopes.FirstOrDefault(option => option.ProfileId == rule.ProfileId) ??
                    SearchRewriteScopeOption.Global;
            ruleType = RuleTypeOptions.FirstOrDefault(option => option.Value == rule.RuleType) ??
                       RuleTypeOptions[0];
            direction = DirectionOptions.FirstOrDefault(option => option.Value == rule.Direction) ??
                        DirectionOptions[0];
            pattern = rule.Pattern;
            replacement = rule.Replacement;
            priority = rule.Priority;
            note = rule.Note;
            enabled = rule.Enabled;
        }

        return new SearchRewriteRuleRowViewModel(
            this,
            rule?.RuleId,
            scope ?? SearchRewriteScopeOption.Global,
            ruleType ?? RuleTypeOptions[0],
            direction ?? DirectionOptions[0],
            rule?.ProfileId,
            pattern,
            replacement,
            priority,
            note ?? "",
            enabled);
    }

    private async Task PreviewAsync()
    {
        if (string.IsNullOrWhiteSpace(PreviewQuery))
        {
            PreviewIsError = true;
            PreviewSummary = "请输入示例查询。";
            PreviewExpandedQueries.Clear();
            PreviewWarnings.Clear();
            return;
        }

        try
        {
            HostServices services = await _main.ServicesAsync();
            Result<LibraryMetadata> library = await services.Library.GetCurrentLibraryAsync();
            if (library.IsFailure)
            {
                PreviewIsError = true;
                PreviewSummary = "请先创建或打开资料库。";
                PreviewExpandedQueries.Clear();
                PreviewWarnings.Clear();
                return;
            }

            Result<SearchRewritePlan> plan = await services.QueryRewriter.BuildRewritePlanAsync(PreviewQuery,
                new SearchRewriteOptions(library.Value.LibraryId, PreviewOnly: true));
            PreviewExpandedQueries.Clear();
            PreviewWarnings.Clear();
            if (plan.IsFailure)
            {
                PreviewIsError = true;
                PreviewSummary = $"预览失败：{plan.ErrorCode} {plan.ErrorMessage}";
                return;
            }

            foreach (string query in plan.Value.ExpandedQueries)
            {
                PreviewExpandedQueries.Add(query);
            }

            foreach (string warning in plan.Value.Warnings)
            {
                PreviewWarnings.Add(warning);
            }

            string profileName = plan.Value.EffectiveProfileName ?? "(默认配置)";
            PreviewIsError = false;
            PreviewSummary =
                $"原查询：{plan.Value.OriginalQuery} ｜ 生效配置：{profileName} ｜ 展开 {plan.Value.ExpandedQueries.Count} 条" +
                (plan.Value.PreviewOnly ? "（预览，不影响检索）" : "");
        }
        catch (Exception exception)
        {
            PreviewIsError = true;
            PreviewSummary = $"预览失败：{exception.Message}";
        }
    }

    private void SetDirty(bool value)
    {
        if (_isDirty == value)
        {
            return;
        }

        _isDirty = value;
        Raise(nameof(IsDirty));
    }

    private static string FormatProfileName(SearchProfile profile)
    {
        string label = profile.Archived ? $"{profile.Name}（已归档）" : profile.Name;
        return profile.IsDefault ? $"{label}（默认）" : label;
    }
}

public sealed record SearchRewriteRuleTypeOption(string Value, string Label);

public sealed record SearchRewriteDirectionOption(string Value, string Label);

public sealed record SearchRewriteScopeOption(string Label, SearchProfileId? ProfileId)
{
    public static SearchRewriteScopeOption Global { get; } = new("全局", null);
}

/// <summary>One editable rewrite-rule row. All edits, including the enabled checkbox, mark the
/// section dirty; <see cref="SearchRewriteSettingsViewModel.SaveAsync"/> persists them.</summary>
public sealed class SearchRewriteRuleRowViewModel : ViewModelBase
{
    private readonly SearchRewriteSettingsViewModel _parent;
    private readonly SearchProfileId? _originalProfileId;
    private readonly bool _originalEnabled;
    private SearchRewriteRuleTypeOption _selectedRuleType;
    private SearchRewriteDirectionOption _selectedDirection;
    private SearchRewriteScopeOption _selectedScope;
    private string _pattern;
    private string _replacement;
    private int _priority;
    private string _note;
    private bool _enabled;

    internal SearchRewriteRuleRowViewModel(
        SearchRewriteSettingsViewModel parent,
        SearchRewriteRuleId? ruleId,
        SearchRewriteScopeOption scope,
        SearchRewriteRuleTypeOption ruleType,
        SearchRewriteDirectionOption direction,
        SearchProfileId? originalProfileId,
        string pattern,
        string replacement,
        int priority,
        string note,
        bool enabled)
    {
        _parent = parent;
        RuleId = ruleId;
        _selectedScope = scope;
        _selectedRuleType = ruleType;
        _selectedDirection = direction;
        _originalProfileId = originalProfileId;
        _originalEnabled = enabled;
        _pattern = pattern;
        _replacement = replacement;
        _priority = priority;
        _note = note;
        _enabled = enabled;
        DeleteCommand = new RelayCommand(_ => _parent.Remove(this));
    }

    public SearchRewriteRuleId? RuleId { get; }
    public bool IsExisting => RuleId is not null;
    public RelayCommand DeleteCommand { get; }
    public IReadOnlyList<SearchRewriteRuleTypeOption> RuleTypeOptions => _parent.RuleTypeOptions;
    public IReadOnlyList<SearchRewriteDirectionOption> DirectionOptions => _parent.DirectionOptions;
    public IReadOnlyList<SearchRewriteScopeOption> ScopeOptions => _parent.Scopes;
    public IReadOnlyList<string> OpenccOptions => _parent.OpenccOptions;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            Raise();
            _parent.MarkDirty();
        }
    }

    public SearchRewriteRuleTypeOption SelectedRuleType
    {
        get => _selectedRuleType;
        set
        {
            if (ReferenceEquals(_selectedRuleType, value) || value is null)
            {
                return;
            }

            _selectedRuleType = value;
            Raise();
            Raise(nameof(IsOpenccRule));
            Raise(nameof(IsStandardRule));
            Raise(nameof(DisplayLabel));
            if (value.Value == SearchRuleType.SimplifiedTraditional &&
                !OpenccConfigs.All.Contains(Pattern, StringComparer.Ordinal))
            {
                _pattern = OpenccConfigs.S2T;
                Raise(nameof(Pattern));
            }

            _parent.MarkDirty();
        }
    }

    public SearchRewriteDirectionOption SelectedDirection
    {
        get => _selectedDirection;
        set
        {
            if (ReferenceEquals(_selectedDirection, value) || value is null)
            {
                return;
            }

            _selectedDirection = value;
            Raise();
            _parent.MarkDirty();
        }
    }

    public SearchRewriteScopeOption SelectedScope
    {
        get => _selectedScope;
        set
        {
            if (ReferenceEquals(_selectedScope, value) || value is null)
            {
                return;
            }

            _selectedScope = value;
            Raise();
            _parent.MarkDirty();
        }
    }

    public string Pattern
    {
        get => _pattern;
        set
        {
            string updated = value ?? "";
            if (_pattern == updated)
            {
                return;
            }

            _pattern = updated;
            Raise();
            _parent.MarkDirty();
        }
    }

    public string Replacement
    {
        get => _replacement;
        set
        {
            string updated = value ?? "";
            if (_replacement == updated)
            {
                return;
            }

            _replacement = updated;
            Raise();
            _parent.MarkDirty();
        }
    }

    public int Priority
    {
        get => _priority;
        set
        {
            if (_priority == value)
            {
                return;
            }

            _priority = value;
            Raise();
            _parent.MarkDirty();
        }
    }

    public string Note
    {
        get => _note;
        set
        {
            string updated = value ?? "";
            if (_note == updated)
            {
                return;
            }

            _note = updated;
            Raise();
            _parent.MarkDirty();
        }
    }

    public bool IsOpenccRule => _selectedRuleType.Value == SearchRuleType.SimplifiedTraditional;
    public bool IsStandardRule => !IsOpenccRule;
    public string DisplayLabel => $"{_selectedRuleType.Label} / {_selectedScope.Label}";

    /// <summary>The rewrite engine reads only <see cref="Pattern"/> for OpenCC rules, but the
    /// service rejects an empty replacement, so a placeholder is stored for those rows.</summary>
    private string ReplacementForPersistence =>
        IsOpenccRule && string.IsNullOrWhiteSpace(_replacement) ? _pattern : _replacement;

    internal async Task<Result> CommitAsync(HostServices services)
    {
        string pattern = _pattern.Trim();
        string replacement = ReplacementForPersistence.Trim();
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "匹配内容不能为空。");
        }

        if (string.IsNullOrWhiteSpace(replacement))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "替换内容不能为空。");
        }

        string? note = string.IsNullOrWhiteSpace(_note) ? null : _note.Trim();
        SearchProfileId? profileId = _selectedScope.ProfileId;
        string ruleType = _selectedRuleType.Value;
        string direction = _selectedDirection.Value;

        if (RuleId is null)
        {
            Result<SearchRewriteRule> added = await services.SearchProfiles.AddRewriteRuleAsync(profileId, ruleType,
                pattern, replacement, direction, _priority, note);
            return added.IsFailure
                ? Result.Failure(added.ErrorCode!, added.ErrorMessage!)
                : await ApplyEnabledIfNeededAsync(services, added.Value.RuleId, true);
        }

        SearchRewriteRuleId ruleId = RuleId.Value;
        if (profileId != _originalProfileId)
        {
            Result deleted = await services.SearchProfiles.DeleteRuleAsync(ruleId);
            if (deleted.IsFailure)
            {
                return deleted;
            }

            Result<SearchRewriteRule> reAdded = await services.SearchProfiles.AddRewriteRuleAsync(profileId, ruleType,
                pattern, replacement, direction, _priority, note);
            return reAdded.IsFailure
                ? Result.Failure(reAdded.ErrorCode!, reAdded.ErrorMessage!)
                : await ApplyEnabledIfNeededAsync(services, reAdded.Value.RuleId, true);
        }

        Result<SearchRewriteRule> updated = await services.SearchProfiles.UpdateRewriteRuleAsync(ruleId, ruleType,
            pattern, replacement, direction, _priority, note);
        return updated.IsFailure
            ? Result.Failure(updated.ErrorCode!, updated.ErrorMessage!)
            : await ApplyEnabledIfNeededAsync(services, ruleId, _originalEnabled);
    }

    private async Task<Result> ApplyEnabledIfNeededAsync(HostServices services, SearchRewriteRuleId ruleId,
        bool createdEnabled)
    {
        if (Enabled == createdEnabled)
        {
            return Result.Success();
        }

        return Enabled
            ? await services.SearchProfiles.EnableRuleAsync(ruleId)
            : await services.SearchProfiles.DisableRuleAsync(ruleId);
    }
}
