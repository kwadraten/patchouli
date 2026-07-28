using System.Collections.ObjectModel;
using System.Text.Json;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Conflicts;

namespace Patchouli.UI.ViewModels.Dialogs;

public sealed record ConflictDialogResult(
    string ActionId,
    string? OptionId = null,
    IReadOnlyDictionary<string, string>? Choices = null);

public sealed record ConflictDialogOption(string OptionId, string Label, string Detail);

public sealed class ConflictFieldChoiceViewModel : ViewModelBase
{
    private string _selectedSide = BiblatexMappedItemMerge.ChoiceIncoming;

    public ConflictFieldChoiceViewModel(string fieldKey, string label, string? localValue, string incomingValue)
    {
        FieldKey = fieldKey;
        Label = label;
        LocalValue = localValue ?? "(empty)";
        IncomingValue = incomingValue;
    }

    public string FieldKey { get; }
    public string Label { get; }
    public string LocalValue { get; }
    public string IncomingValue { get; }

    public string SelectedSide
    {
        get => _selectedSide;
        set
        {
            if (_selectedSide == value)
            {
                return;
            }

            _selectedSide = value;
            Raise();
            Raise(nameof(KeepLocal));
            Raise(nameof(UseIncoming));
        }
    }

    public bool KeepLocal
    {
        get => string.Equals(SelectedSide, BiblatexMappedItemMerge.ChoiceLocal, StringComparison.Ordinal);
        set
        {
            if (value)
            {
                SelectedSide = BiblatexMappedItemMerge.ChoiceLocal;
            }
        }
    }

    public bool UseIncoming
    {
        get => string.Equals(SelectedSide, BiblatexMappedItemMerge.ChoiceIncoming, StringComparison.Ordinal);
        set
        {
            if (value)
            {
                SelectedSide = BiblatexMappedItemMerge.ChoiceIncoming;
            }
        }
    }
}

public sealed class ConflictLinkChoiceViewModel : ViewModelBase
{
    private ConflictDialogOption? _selectedOption;

    public ConflictLinkChoiceViewModel(
        string sourceEntryKey,
        string sourceTitle,
        IReadOnlyList<ConflictDialogOption> options)
    {
        SourceEntryKey = sourceEntryKey;
        SourceTitle = sourceTitle;
        foreach (ConflictDialogOption option in options)
        {
            Options.Add(option);
        }

        SelectedOption = Options.FirstOrDefault();
    }

    public string SourceEntryKey { get; }
    public string SourceTitle { get; }
    public ObservableCollection<ConflictDialogOption> Options { get; } = new();

    public ConflictDialogOption? SelectedOption
    {
        get => _selectedOption;
        set
        {
            _selectedOption = value;
            Raise();
        }
    }
}

public sealed class ConflictDialogActionViewModel : ViewModelBase
{
    private readonly ConflictResolutionDialogViewModel _owner;

    public ConflictDialogActionViewModel(ConflictAction action, ConflictResolutionDialogViewModel owner)
    {
        ActionId = action.ActionId;
        Label = action.Label;
        Description = action.Description ?? "";
        IsRecommended = action.IsRecommended;
        RequiresOption = action.RequiresOption;
        _owner = owner;
        SelectCommand = new AsyncCommand(() => _owner.SubmitAsync(ActionId));
    }

    public string ActionId { get; }
    public string Label { get; }
    public string Description { get; }
    public bool IsRecommended { get; }
    public bool RequiresOption { get; }
    private bool _isEnabled;

    public bool IsEnabled
    {
        get => _isEnabled;
        private set
        {
            if (_isEnabled == value)
            {
                return;
            }

            _isEnabled = value;
            Raise();
        }
    }

    public AsyncCommand SelectCommand { get; }

    public void RefreshCanSubmit(bool canSubmit)
    {
        IsEnabled = !RequiresOption || canSubmit;
    }
}

public sealed class ConflictResolutionDialogViewModel : ViewModelBase
{
    public ConflictResolutionDialogViewModel()
        : this(new ConflictDescriptor(
            "unknown", ConflictDomain.SnapshotSync, ConflictSeverity.Blocking,
            "unknown", "unknown", "本地版本与传入版本存在不一致。",
            "", "", []))
    {
    }

    public ConflictResolutionDialogViewModel(
        ConflictDescriptor descriptor,
        IReadOnlyList<ConflictDialogOption>? options = null)
    {
        ConflictCode = descriptor.ConflictCode;
        Title = $"检测到冲突 {descriptor.ConflictCode}";
        ConflictDescription = descriptor.Summary;
        Severity = descriptor.Severity;
        SeverityDescription = descriptor.Severity switch
        {
            ConflictSeverity.Blocking => "必须处理后才能继续危险操作。",
            ConflictSeverity.Warning => "可以继续，但风险和恢复操作会保留。",
            _ => "此信息不阻止继续操作。"
        };
        LocalContent = DescribeSnapshot(descriptor.LocalSnapshot, "无本地状态");
        IncomingContent = DescribeSnapshot(descriptor.IncomingSnapshot, "无传入状态");
        IsFieldChoiceMode = string.Equals(
            descriptor.ConflictCode,
            Patchouli.Core.Conflicts.ConflictCode.BiblatexItemFieldConflict,
            StringComparison.Ordinal);
        IsLinkChoiceMode = string.Equals(
            descriptor.ConflictCode,
            Patchouli.Core.Conflicts.ConflictCode.BiblatexBatchLinkCandidates,
            StringComparison.Ordinal);

        if (IsFieldChoiceMode)
        {
            foreach (ConflictFieldChoiceViewModel row in BuildFieldChoices(descriptor))
            {
                FieldChoices.Add(row);
            }
        }
        else if (IsLinkChoiceMode)
        {
            foreach (ConflictLinkChoiceViewModel row in BuildLinkChoices(descriptor))
            {
                LinkChoices.Add(row);
            }
        }
        else
        {
            foreach (ConflictDialogOption option in options ?? descriptor.AvailableOptions.Select(option =>
                         new ConflictDialogOption(option.OptionId, option.Label, option.Detail)))
            {
                Options.Add(option);
            }
        }

        foreach (ConflictAction action in descriptor.RecommendedActions)
        {
            Actions.Add(new ConflictDialogActionViewModel(action, this));
        }

        Actions.Add(new ConflictDialogActionViewModel(
            new ConflictAction("leave_unresolved", "暂不处理", "保持冲突未解决。", false), this));
        RefreshActionAvailability();
    }

    public string ConflictCode { get; }
    public string Title { get; }
    public string ConflictDescription { get; }
    public string Severity { get; }
    public string SeverityDescription { get; }
    public string LocalContent { get; }
    public string IncomingContent { get; }
    public bool IsFieldChoiceMode { get; }
    public bool IsLinkChoiceMode { get; }
    public bool IsSimpleOptionMode => !IsFieldChoiceMode && !IsLinkChoiceMode;
    public ObservableCollection<ConflictDialogActionViewModel> Actions { get; } = new();
    public ObservableCollection<ConflictDialogOption> Options { get; } = new();
    public ObservableCollection<ConflictFieldChoiceViewModel> FieldChoices { get; } = new();
    public ObservableCollection<ConflictLinkChoiceViewModel> LinkChoices { get; } = new();
    public bool HasOptions => Options.Count > 0;
    public bool ShowSnapshotPanels => IsSimpleOptionMode;

    private ConflictDialogOption? _selectedOption;

    public ConflictDialogOption? SelectedOption
    {
        get => _selectedOption;
        set
        {
            _selectedOption = value;
            Raise();
            RefreshActionAvailability();
        }
    }

    public Action<object?>? RequestClose { get; set; }

    internal Task SubmitAsync(string actionId)
    {
        ConflictDialogActionViewModel? action = Actions.SingleOrDefault(candidate => candidate.ActionId == actionId);
        if (action is null || !action.IsEnabled)
        {
            return Task.CompletedTask;
        }

        IReadOnlyDictionary<string, string>? choices = null;
        if (IsFieldChoiceMode)
        {
            choices = FieldChoices.ToDictionary(
                static row => row.FieldKey,
                static row => row.SelectedSide,
                StringComparer.Ordinal);
        }
        else if (IsLinkChoiceMode)
        {
            choices = LinkChoices.ToDictionary(
                static row => row.SourceEntryKey,
                static row =>
                {
                    string optionId = row.SelectedOption?.OptionId ?? "";
                    int separator = optionId.IndexOf('|');
                    return separator < 0 ? optionId : optionId[(separator + 1)..];
                },
                StringComparer.Ordinal);
        }

        RequestClose?.Invoke(new ConflictDialogResult(actionId, SelectedOption?.OptionId, choices));
        return Task.CompletedTask;
    }

    private void RefreshActionAvailability()
    {
        bool canSubmit = IsFieldChoiceMode
            ? FieldChoices.Count > 0
            : IsLinkChoiceMode
                ? LinkChoices.Count > 0 && LinkChoices.All(static row => row.SelectedOption is not null)
                : SelectedOption is not null;
        foreach (ConflictDialogActionViewModel action in Actions)
        {
            action.RefreshCanSubmit(canSubmit);
        }
    }

    private static IEnumerable<ConflictFieldChoiceViewModel> BuildFieldChoices(ConflictDescriptor descriptor)
    {
        Dictionary<string, string?> locals = ReadFieldMap(descriptor.LocalSnapshot, "local");
        Dictionary<string, string> incomings = ReadFieldMap(descriptor.IncomingSnapshot, "incoming")
            .ToDictionary(static pair => pair.Key, static pair => pair.Value ?? "", StringComparer.Ordinal);
        Dictionary<string, string> labels = ReadFieldLabels(descriptor.IncomingSnapshot);

        foreach (ConflictActionOption option in descriptor.AvailableOptions)
        {
            locals.TryGetValue(option.OptionId, out string? local);
            incomings.TryGetValue(option.OptionId, out string? incoming);
            labels.TryGetValue(option.OptionId, out string? label);
            yield return new ConflictFieldChoiceViewModel(
                option.OptionId,
                label ?? option.Label,
                local,
                incoming ?? option.Detail);
        }
    }

    private static IEnumerable<ConflictLinkChoiceViewModel> BuildLinkChoices(ConflictDescriptor descriptor)
    {
        List<(string Key, ConflictActionOption Option)> pairs = [];
        foreach (ConflictActionOption option in descriptor.AvailableOptions)
        {
            int separator = option.OptionId.IndexOf('|');
            string key = separator < 0 ? option.OptionId : option.OptionId[..separator];
            pairs.Add((key, option));
        }

        foreach (IGrouping<string, (string Key, ConflictActionOption Option)> group in pairs.GroupBy(
                     static pair => pair.Key, StringComparer.Ordinal))
        {
            ConflictDialogOption[] options = group
                .Select(static pair => new ConflictDialogOption(
                    pair.Option.OptionId,
                    pair.Option.Label,
                    pair.Option.Detail))
                .ToArray();
            string title = options.FirstOrDefault()?.Detail ?? group.Key;
            yield return new ConflictLinkChoiceViewModel(group.Key, title, options);
        }
    }

    private static Dictionary<string, string?> ReadFieldMap(string? snapshot, string valueProperty)
    {
        Dictionary<string, string?> map = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(snapshot))
        {
            return map;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(snapshot);
            if (!document.RootElement.TryGetProperty("fields", out JsonElement fields) ||
                fields.ValueKind != JsonValueKind.Array)
            {
                return map;
            }

            foreach (JsonElement field in fields.EnumerateArray())
            {
                string? key = field.TryGetProperty("field", out JsonElement fieldName)
                    ? fieldName.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                string? value = field.TryGetProperty(valueProperty, out JsonElement valueNode)
                    ? valueNode.ValueKind == JsonValueKind.Null ? null : valueNode.ToString()
                    : null;
                map[key] = value;
            }
        }
        catch (JsonException)
        {
            return map;
        }

        return map;
    }

    private static Dictionary<string, string> ReadFieldLabels(string? snapshot)
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(snapshot))
        {
            return map;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(snapshot);
            if (!document.RootElement.TryGetProperty("fields", out JsonElement fields) ||
                fields.ValueKind != JsonValueKind.Array)
            {
                return map;
            }

            foreach (JsonElement field in fields.EnumerateArray())
            {
                string? key = field.TryGetProperty("field", out JsonElement fieldName)
                    ? fieldName.GetString()
                    : null;
                string? label = field.TryGetProperty("label", out JsonElement labelNode)
                    ? labelNode.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(label))
                {
                    map[key] = label;
                }
            }
        }
        catch (JsonException)
        {
            return map;
        }

        return map;
    }

    private static string DescribeSnapshot(string? snapshot, string emptyText)
    {
        if (string.IsNullOrWhiteSpace(snapshot))
        {
            return emptyText;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(snapshot);
            return document.RootElement.ValueKind != JsonValueKind.Object
                ? snapshot
                : string.Join("\n", document.RootElement.EnumerateObject().Select(property =>
                    $"{property.Name.Replace('_', ' ')}: {property.Value}"));
        }
        catch (JsonException)
        {
            return snapshot;
        }
    }
}
