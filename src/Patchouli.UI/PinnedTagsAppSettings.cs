namespace Patchouli.UI;

/// <summary>
/// Library-level pinned tag order persisted through <see cref="LibrarySettingCoordinator"/>.
/// </summary>
public sealed record PinnedTagsAppSettings(IReadOnlyList<string> Tags)
{
    public static PinnedTagsAppSettings Empty { get; } = new(Array.Empty<string>());
}
