using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Conflicts;

namespace Patchouli.Host.Import;

/// <summary>
/// UI callback for resolving a blocking conflict descriptor during an import
/// (e.g. BibLaTeX field conflicts or batch link candidates). Returning null, an
/// "leave_unresolved" action, or a choice without field selections means the
/// user cancelled the import.
/// </summary>
public interface IImportConflictPrompt
{
    Task<ConflictPromptChoice?> ResolveAsync(
        ConflictDescriptor conflict,
        CancellationToken cancellationToken = default);
}

/// <summary>The user's resolution for a conflict descriptor.</summary>
public sealed record ConflictPromptChoice(
    string ActionId,
    string? OptionId = null,
    IReadOnlyDictionary<string, string>? Choices = null);

/// <summary>
/// UI callbacks for the BibLaTeX import preview/confirmation dialogs. Implemented
/// by the UI layer; the orchestrator only consumes the decisions.
/// </summary>
public interface IBiblatexImportPrompt
{
    /// <summary>
    /// Prompts the user to pick a single entry key when a BibLaTeX source contains
    /// multiple visible entries. Returns the selected <see cref="BiblatexMappedItem.SourceEntryKey"/>,
    /// or null when the user cancels.
    /// </summary>
    Task<string?> SelectEntryKeyAsync(
        BiblatexEntryPickRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms a batch import that would silently create new items without link
    /// candidates. Returns false when the user cancels.
    /// </summary>
    Task<bool> ConfirmSilentBatchCreateAsync(
        BiblatexBatchConfirmRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Request DTO for the single-entry picker shown for multi-entry BibLaTeX text.</summary>
public sealed record BiblatexEntryPickRequest(
    IReadOnlyList<BiblatexMappedItem> Entries,
    string Message);

/// <summary>Request DTO for the silent-create batch confirmation dialog.</summary>
public sealed record BiblatexBatchConfirmRequest(
    IReadOnlyList<BiblatexMappedItem> Entries,
    string Message);
