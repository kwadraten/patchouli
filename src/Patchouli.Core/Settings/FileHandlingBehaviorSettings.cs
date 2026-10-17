namespace Patchouli.Core.Settings;

/// <summary>
/// Issue #11: Per-action file handling behavior settings. These are device-local system settings
/// (not synced with Library snapshots); desktop and headless hosts read them by the same rules.
/// </summary>
public enum FileHandlingBehavior
{
    /// <summary>
    /// Keep the file in place, compute BLAKE3, and register the FileAsset with its known location.
    /// This is the default and preserves current behavior.
    /// </summary>
    KeepInPlace = 0,

    /// <summary>
    /// Move the file to the first file search root, then compute BLAKE3 and register.
    /// Fails explicitly if no search root exists or the first root is not writable/available.
    /// </summary>
    MoveToFirstSearchRoot = 1,
}

/// <summary>
/// Device-local behavior settings organized as "trigger action → file handling behavior".
/// MCP add and window PDF drag-drop are configured independently.
/// </summary>
public sealed record FileHandlingBehaviorSettings(
    FileHandlingBehavior McpAddPdfBehavior,
    FileHandlingBehavior PdfDragDropBehavior)
{
    public static FileHandlingBehaviorSettings Default()
    {
        return new FileHandlingBehaviorSettings(
            FileHandlingBehavior.KeepInPlace,
            FileHandlingBehavior.KeepInPlace);
    }
}
