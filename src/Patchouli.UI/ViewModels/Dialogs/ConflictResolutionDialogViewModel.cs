using System.Collections.ObjectModel;
using Patchouli.Core.Conflicts;

namespace Patchouli.UI.ViewModels.Dialogs;

public sealed record ConflictDialogResult(string ActionId, string? OptionId = null);

public sealed record ConflictDialogOption(string OptionId, string Label, string Detail);

public sealed class ConflictDialogActionViewModel
{
    private readonly ConflictResolutionDialogViewModel _owner;

    public ConflictDialogActionViewModel(ConflictAction action, ConflictResolutionDialogViewModel owner)
    {
        ActionId = action.ActionId;
        Label = action.Label;
        Description = action.Description ?? "";
        IsRecommended = action.IsRecommended;
        _owner = owner;
        SelectCommand = new AsyncCommand(() => _owner.SubmitAsync(ActionId));
    }

    public string ActionId { get; }
    public string Label { get; }
    public string Description { get; }
    public bool IsRecommended { get; }
    public AsyncCommand SelectCommand { get; }
}

public sealed class ConflictResolutionDialogViewModel : ViewModelBase
{
    public ConflictResolutionDialogViewModel()
        : this(new ConflictDescriptor(
            "unknown", ConflictDomain.SnapshotSync, ConflictSeverity.Blocking,
            "unknown", "unknown", "本地版本与传入版本存在不一致。",
            "", "", [new ConflictAction("leave_unresolved", "暂不处理")]))
    {
    }

    public ConflictResolutionDialogViewModel(
        ConflictDescriptor descriptor,
        IReadOnlyList<ConflictDialogOption>? options = null)
    {
        ConflictCode = descriptor.ConflictCode;
        Title = $"检测到冲突 {descriptor.ConflictCode}";
        ConflictDescription = descriptor.Summary;
        LocalContent = descriptor.LocalSnapshot ?? "无本地状态";
        IncomingContent = descriptor.IncomingSnapshot ?? "无传入状态";
        foreach (ConflictDialogOption option in options ?? [])
        {
            Options.Add(option);
        }

        foreach (ConflictAction action in descriptor.RecommendedActions)
        {
            Actions.Add(new ConflictDialogActionViewModel(action, this));
        }

        Actions.Add(new ConflictDialogActionViewModel(
            new ConflictAction("leave_unresolved", "暂不处理", "保持冲突未解决。", false), this));
    }

    public string ConflictCode { get; }
    public string Title { get; }
    public string ConflictDescription { get; }
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
        }
    }

    public Action<object?>? RequestClose { get; set; }

    internal Task SubmitAsync(string actionId)
    {
        if (actionId is "choose_candidate" or "confirm_changed_file" && SelectedOption is null)
        {
            return Task.CompletedTask;
        }

        RequestClose?.Invoke(new ConflictDialogResult(actionId, SelectedOption?.OptionId));
        return Task.CompletedTask;
    }
}
