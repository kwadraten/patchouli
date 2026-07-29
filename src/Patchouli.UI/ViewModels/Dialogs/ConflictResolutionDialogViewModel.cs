using System.Collections.ObjectModel;
using System.Text.Json;
using Patchouli.Core.Bibliography;
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
        LocalValue = FormatValue(fieldKey, localValue);
        IncomingValue = FormatValue(fieldKey, incomingValue);
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

    private static string FormatValue(string fieldKey, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "（空）";
        }

        return fieldKey switch
        {
            "item_type" => LocalizeItemType(value),
            "creators" => FormatCreators(value),
            "dates" => FormatDates(value),
            "tags" => FormatStringArray(value),
            "identifiers" => FormatIdentifiers(value),
            _ => value
        };
    }

    private static string LocalizeItemType(string value)
    {
        return CslItemTypeDisplayNames.For(value);
    }

    private static string FormatCreators(string value)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return value;
            }

            string[] creators = document.RootElement.EnumerateArray()
                .Select(FormatCreator)
                .Where(static creator => creator.Length > 0)
                .ToArray();
            return creators.Length == 0 ? "（空）" : string.Join("；", creators);
        }
        catch (JsonException)
        {
            return value;
        }
    }

    private static string FormatCreator(JsonElement creator)
    {
        string roleKey = ReadString(creator, "role") ?? "";
        string role = ItemCreatorRoles.Supported.Contains(roleKey)
            ? ItemCreatorRoles.DisplayLabelFor(roleKey)
            : "责任者";
        string? literal = ReadString(creator, "literal");
        string name = literal ?? string.Join(" ", new[]
            {
                ReadString(creator, "given"),
                ReadString(creator, "particles"),
                ReadString(creator, "family"),
                ReadString(creator, "suffix")
            }
            .Where(static part => !string.IsNullOrWhiteSpace(part)));
        return string.IsNullOrWhiteSpace(name) ? "" : $"{role}：{name}";
    }

    private static string FormatDates(string value)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return value;
            }

            string[] dates = document.RootElement.EnumerateArray()
                .Select(FormatDate)
                .Where(static date => date.Length > 0)
                .ToArray();
            return dates.Length == 0 ? "（空）" : string.Join("；", dates);
        }
        catch (JsonException)
        {
            return value;
        }
    }

    private static string FormatDate(JsonElement date)
    {
        string role = ReadString(date, "role") switch
        {
            ItemDateRoles.Issued => "出版日期",
            ItemDateRoles.Accessed => "访问日期",
            ItemDateRoles.OriginalDate => "原始日期",
            ItemDateRoles.EventDate => "事件日期",
            ItemDateRoles.Submitted => "提交日期",
            _ => "日期"
        };
        string? literal = ReadString(date, "literal");
        string? datePartsJson = ReadString(date, "date_parts_json");
        string display = literal ?? FormatDateParts(datePartsJson);
        return string.IsNullOrWhiteSpace(display) ? "" : $"{role}：{display}";
    }

    private static string FormatDateParts(string? datePartsJson)
    {
        if (string.IsNullOrWhiteSpace(datePartsJson))
        {
            return "";
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(datePartsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return datePartsJson;
            }

            return string.Join("；", document.RootElement.EnumerateArray().Select(static parts =>
                string.Join("-", parts.EnumerateArray().Select(static part => part.ToString()))));
        }
        catch (JsonException)
        {
            return datePartsJson;
        }
    }

    private static string FormatStringArray(string value)
    {
        try
        {
            string[]? values = JsonSerializer.Deserialize<string[]>(value);
            return values is null || values.Length == 0 ? "（空）" : string.Join("、", values);
        }
        catch (JsonException)
        {
            return value;
        }
    }

    private static string FormatIdentifiers(string value)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return value;
            }

            string[] identifiers = document.RootElement.EnumerateArray()
                .Select(static identifier =>
                {
                    string? scheme = ReadString(identifier, "scheme");
                    string? identifierValue = ReadString(identifier, "value");
                    return string.IsNullOrWhiteSpace(identifierValue)
                        ? ""
                        : string.IsNullOrWhiteSpace(scheme)
                            ? identifierValue
                            : $"{scheme.ToUpperInvariant()}：{identifierValue}";
                })
                .Where(static identifier => identifier.Length > 0)
                .ToArray();
            return identifiers.Length == 0 ? "（空）" : string.Join("；", identifiers);
        }
        catch (JsonException)
        {
            return value;
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        string value = property.ToString().Trim();
        return value.Length == 0 ? null : value;
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
        Label = action.ActionId switch
        {
            "choose_fields" => "应用字段选择",
            "resolve_links" => "应用关联选择",
            _ => action.Label
        };
        Description = action.ActionId switch
        {
            "choose_fields" => "逐项选择保留本地值或采用导入值，然后继续导入。",
            "resolve_links" => "为每个源条目选择候选题录或新建题录。",
            _ => action.Description ?? ""
        };
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
        Title = descriptor.ConflictCode switch
        {
            Patchouli.Core.Conflicts.ConflictCode.BiblatexItemFieldConflict => "处理题录字段冲突",
            Patchouli.Core.Conflicts.ConflictCode.BiblatexBatchLinkCandidates => "处理题录关联冲突",
            _ => "处理冲突"
        };
        ConflictDescription = descriptor.ConflictCode switch
        {
            Patchouli.Core.Conflicts.ConflictCode.BiblatexItemFieldConflict =>
                "导入的 BibLaTeX 字段与目标题录不同，请逐项选择处理方式。",
            Patchouli.Core.Conflicts.ConflictCode.BiblatexBatchLinkCandidates =>
                "一个或多个 BibLaTeX 源条目存在候选题录，请明确选择关联现有题录或新建题录。",
            _ => descriptor.Summary
        };
        Severity = descriptor.Severity switch
        {
            ConflictSeverity.Blocking => "必须处理",
            ConflictSeverity.Warning => "警告",
            _ => "提示"
        };
        SeverityDescription = descriptor.Severity switch
        {
            ConflictSeverity.Blocking => "必须先处理此冲突，才能继续当前操作。",
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
