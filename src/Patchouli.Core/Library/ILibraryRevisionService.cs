using Patchouli.Core.Results;
using System.Data.Common;

namespace Patchouli.Core.Library;

/// <summary>
/// Owns the persistent, monotonic Library revision. Every successful transaction that changes a
/// protocol-visible resource or relation must increment it through <see cref="CommitAsync"/>,
/// which also publishes the typed <see cref="LibraryChangeSet"/> after the commit succeeds.
/// Staging and rebuildable local FTS cache maintenance never bump the revision.
/// </summary>
public interface ILibraryRevisionService
{
    event EventHandler<LibraryRevisionCommittedEventArgs>? ChangeCommitted;

    Task<Result<long>> GetCurrentRevisionAsync(CancellationToken cancellationToken = default);

    Task<Result<long>> CommitAsync(LibraryChangeSet changeSet, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds the next revision to an already-open host write transaction. Call
    /// <see cref="PublishCommitted"/> only after that transaction commits.
    /// </summary>
    Task<Result<LibraryChangeSet>> IncrementInTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        LibraryChangeSet changeSet,
        CancellationToken cancellationToken = default);

    /// <summary>Publishes a change which was committed by the caller's transaction.</summary>
    void PublishCommitted(LibraryChangeSet changeSet);
}
