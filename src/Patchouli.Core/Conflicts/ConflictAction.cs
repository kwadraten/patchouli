namespace Patchouli.Core.Conflicts;

public sealed record ConflictAction(
    string ActionId,
    string Label,
    string? Description = null,
    bool IsRecommended = true);
