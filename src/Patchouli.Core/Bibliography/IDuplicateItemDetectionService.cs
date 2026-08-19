namespace Patchouli.Core.Bibliography;

/// <summary>
/// Detects potential duplicate library items using identifier, metadata similarity and primary
/// document file hash rules.
/// </summary>
public interface IDuplicateItemDetectionService
{
    /// <summary>
    /// Returns all duplicate pairs currently detectable among active items.
    /// </summary>
    Task<IReadOnlyList<DuplicateItemPair>> FindDuplicatesAsync(CancellationToken cancellationToken = default);
}
