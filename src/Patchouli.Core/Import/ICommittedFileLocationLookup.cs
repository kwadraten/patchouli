namespace Patchouli.Core.Import;

/// <summary>
/// Issue #30: read-only probe used on the import recovery path. File moves and database
/// commits are not one transaction, so before a failed or cancelled import moves a file
/// back out of a search root, the recovery code asks whether the library database already
/// holds a committed, available location at the moved path. If it does — e.g. the commit
/// landed and cancellation was only observed afterwards, or another importer committed
/// the same path — moving the file away would leave the database pointing at a missing
/// file, so the file must stay where the database expects it.
/// </summary>
public interface ICommittedFileLocationLookup
{
    /// <summary>
    /// Returns <see langword="true"/> when an available file asset has an available known
    /// location at <paramref name="path"/>. Lookup failures report <see langword="false"/>
    /// (the caller falls back to the plain restore) and never throw.
    /// </summary>
    Task<bool> HasCommittedLocationAsync(string path, CancellationToken cancellationToken = default);
}
