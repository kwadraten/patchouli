using System.Data.Common;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Documents;

public interface ITransactionalDocumentTreeService : IDocumentTreeService
{
    Task<Result<DocumentCommit>> CreateDocumentCommitInTransactionAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        DocumentInstanceId documentInstanceId,
        string source,
        string? message = null,
        CancellationToken cancellationToken = default);

    Task<Result<DocumentTreeRevision>> CommitWorkingRevisionInTransactionAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        DocumentTreeRevisionId workingRevisionId,
        DocumentCommitId? commitId = null,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<DocumentTreeRevision>>> CommitWorkingRevisionsInTransactionAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        IReadOnlyList<DocumentTreeRevisionId> workingRevisionIds,
        DocumentCommitId? commitId = null,
        CancellationToken cancellationToken = default);
}
