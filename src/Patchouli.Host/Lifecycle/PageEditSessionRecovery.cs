using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Host.Lifecycle;

internal static class PageEditSessionRecovery
{
    // Only the new, exclusive runtime owner may discard drafts left by its predecessor.
    public static async Task RecoverAsync(SqliteConnectionFactory factory, RuntimeHostLease lease, string databasePath)
    {
        lease.ValidateOwnership(databasePath);
        using IDisposable writeLease = await factory.EnterWriteAsync(CancellationToken.None);
        await using SqliteConnection connection = factory.CreateConnection();
        await connection.OpenAsync();
        await using DbTransaction transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            """
            delete from document_boxes where tree_revision_id in (
                select tree_revision_id from document_tree_revisions
                where status = 'working' and edit_session_id is not null and is_current = 0
            );
            delete from document_tree_revisions
            where status = 'working' and edit_session_id is not null and is_current = 0;
            """, transaction: transaction);
        await transaction.CommitAsync();
    }
}
