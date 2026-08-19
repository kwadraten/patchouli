namespace Patchouli.Core.Bibliography;

/// <summary>
/// Reasons why two library items are considered potential duplicates.
/// </summary>
public enum DuplicateItemReason
{
    IdentifierMatch,
    SimilarMetadata,
    FileHashMatch
}
