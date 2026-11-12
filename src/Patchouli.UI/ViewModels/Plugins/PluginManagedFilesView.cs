namespace Patchouli.UI.ViewModels;

/// <summary>
/// The sanitized per-file status a plugin web view is allowed to see: the declared relative
/// path plus presence/metadata only — never a local path and never the download URL (its
/// query may carry secrets the document does not need). <see cref="State"/> is normalized
/// to "present" | "missing" | "invalid" by <see cref="PluginManagedFileDialogs.ToView"/>.
/// </summary>
internal sealed record PluginManagedFileView(
    string Path,
    string State,
    long? ExpectedBytes = null,
    string? Sha256 = null,
    string? Version = null)
{
    /// <summary>True only when the file is verified present — every other state needs the download.</summary>
    public bool IsSatisfied => State == "present";
}

/// <summary>
/// The sanitized managed-files surface a plugin view sees: the plugin id, a coarse
/// <see cref="State"/> ("satisfied" | "missing" | "completed"), and the per-file list.
/// Replies to the document never contain local paths.
/// </summary>
internal sealed record PluginManagedFilesView(
    string PluginId,
    string State,
    IReadOnlyList<PluginManagedFileView> Files)
{
    /// <summary>Every declared file is verified present — no download or confirmation needed.</summary>
    public bool AllSatisfied => Files.All(file => file.IsSatisfied);
}
