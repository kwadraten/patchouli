using Patchouli.Core.Conflicts;
using Patchouli.Host.Import;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Dialogs;

namespace Patchouli.UI.Services;

/// <summary>UI-backed <see cref="IImportConflictPrompt"/> for the import orchestrator: shows the
/// standard conflict-resolution dialog and translates the result into a <see cref="ConflictPromptChoice"/>.</summary>
public sealed class DialogImportConflictPrompt : IImportConflictPrompt
{
    private readonly MainWindowViewModel _main;

    public DialogImportConflictPrompt(MainWindowViewModel main)
    {
        _main = main;
    }

    public async Task<ConflictPromptChoice?> ResolveAsync(
        ConflictDescriptor conflict,
        CancellationToken cancellationToken = default)
    {
        ConflictResolutionDialogViewModel dialog = new(conflict);
        ConflictDialogResult? choice = await _main.Dialogs.ShowDialogAsync<ConflictDialogResult>(dialog);
        if (choice is null ||
            string.Equals(choice.ActionId, "leave_unresolved", StringComparison.Ordinal) ||
            choice.Choices is null)
        {
            return null;
        }

        return new ConflictPromptChoice(choice.ActionId, choice.OptionId, choice.Choices);
    }
}

/// <summary>UI-backed <see cref="IBiblatexImportPrompt"/> for the import orchestrator: entry picker
/// for multi-entry BibLaTeX text and the silent-create batch confirmation.</summary>
public sealed class DialogBiblatexImportPrompt : IBiblatexImportPrompt
{
    private readonly MainWindowViewModel _main;

    public DialogBiblatexImportPrompt(MainWindowViewModel main)
    {
        _main = main;
    }

    public async Task<string?> SelectEntryKeyAsync(
        BiblatexEntryPickRequest request,
        CancellationToken cancellationToken = default)
    {
        BiblatexImportPreviewDialogViewModel preview = new(request.Entries, true, request.Message);
        BiblatexImportPreviewResult? confirmed =
            await _main.Dialogs.ShowDialogAsync<BiblatexImportPreviewResult>(preview);
        return confirmed is { Confirmed: true } && !string.IsNullOrWhiteSpace(confirmed.SelectedEntryKey)
            ? confirmed.SelectedEntryKey
            : null;
    }

    public async Task<bool> ConfirmSilentBatchCreateAsync(
        BiblatexBatchConfirmRequest request,
        CancellationToken cancellationToken = default)
    {
        BiblatexImportPreviewDialogViewModel confirm = new(request.Entries, false, request.Message);
        BiblatexImportPreviewResult? ok =
            await _main.Dialogs.ShowDialogAsync<BiblatexImportPreviewResult>(confirm);
        return ok is { Confirmed: true };
    }
}
