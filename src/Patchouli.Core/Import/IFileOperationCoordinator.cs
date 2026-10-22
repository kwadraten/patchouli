namespace Patchouli.Core.Import;

/// <summary>
/// Issue #30: process-wide registry of exclusive per-path claims shared by every file
/// importer in this process (MCP <c>add</c> via <see cref="IFileImportService"/>, the file
/// search root watcher, manual rescans). One holder per normalized path at a time, so two
/// importers can never process the same file concurrently and a rollback can never race
/// another importer's commit for the same path. Claims are in-memory only: the desktop app
/// is a single-instance process, so no cross-process locking is attempted.
/// </summary>
public interface IFileOperationCoordinator
{
    /// <summary>
    /// Acquires exclusive claims for all <paramref name="paths"/> atomically
    /// (all-or-nothing: either every path is claimed or the call waits). Waiting is
    /// signal-driven and honors <paramref name="cancellationToken"/>; a cancelled wait
    /// claims nothing. The caller must hold the returned lease until the file operation —
    /// including any import commit, failure cleanup or rollback — has fully finished.
    /// </summary>
    Task<IFileOperationLease> ClaimAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Non-blocking all-or-nothing claim attempt. Returns <see langword="null"/> when any of
    /// <paramref name="paths"/> is already claimed; background scanners use this to skip a
    /// path instead of queueing behind an in-flight explicit import.
    /// </summary>
    IFileOperationLease? TryClaim(IEnumerable<string> paths);

    /// <summary>Reports whether <paramref name="path"/> currently has a claim holder.</summary>
    bool IsClaimed(string path);
}

/// <summary>
/// A held set of path claims. Extending a lease with <see cref="AddAsync"/> is also
/// all-or-nothing for the added paths; previously held paths stay claimed if the wait is
/// cancelled. Dispose releases every held path and wakes waiters.
/// </summary>
public interface IFileOperationLease : IAsyncDisposable
{
    /// <summary>The normalized paths this lease currently holds.</summary>
    IReadOnlyCollection<string> Paths { get; }

    /// <summary>
    /// Atomically adds claims for <paramref name="paths"/> to this lease, waiting until
    /// every added path is free. Paths already held by this lease are ignored.
    /// </summary>
    Task AddAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default);
}
