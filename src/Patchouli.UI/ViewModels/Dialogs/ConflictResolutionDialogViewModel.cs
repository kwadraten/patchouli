using System.Collections.ObjectModel;
using Patchouli.Core.Conflicts;

namespace Patchouli.UI.ViewModels.Dialogs;

public sealed record ConflictDialogResult(string ActionId, string? OptionId = null);

public sealed record ConflictDialogOption(string OptionId, string Label, string Detail);

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

    public void RefreshCanSubmit(bool hasOption)
    {
        IsEnabled = !RequiresOption || hasOption;
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
        foreach (ConflictDialogOption option in options ?? descriptor.AvailableOptions.Select(option =>
                     new ConflictDialogOption(option.OptionId, option.Label, option.Detail)))
        {
            Options.Add(option);
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
    public ObservableCollection<ConflictDialogActionViewModel> Actions { get; } = new();
    public ObservableCollection<ConflictDialogOption> Options { get; } = new();
    public bool HasOptions => Options.Count > 0;

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

        RequestClose?.Invoke(new ConflictDialogResult(actionId, SelectedOption?.OptionId));
        return Task.CompletedTask;
    }

    private void RefreshActionAvailability()
    {
        foreach (ConflictDialogActionViewModel action in Actions)
        {
            action.RefreshCanSubmit(SelectedOption is not null);
        }
    }

    private static string DescribeSnapshot(string? snapshot, string emptyText)
    {
        if (string.IsNullOrWhiteSpace(snapshot))
        {
            return emptyText;
        }

        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(snapshot);
            return document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                ? snapshot
                : string.Join("\n", document.RootElement.EnumerateObject().Select(property =>
                    $"{property.Name.Replace('_', ' ')}: {property.Value}"));
        }
        catch (System.Text.Json.JsonException)
        {
            return snapshot;
        }
    }
}
