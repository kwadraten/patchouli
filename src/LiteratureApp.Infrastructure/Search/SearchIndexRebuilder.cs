using System.Text;
using Dapper;
using LiteratureApp.Core.Ids;
using LiteratureApp.Core.Results;
using LiteratureApp.Core.Time;
using LiteratureApp.Infrastructure.Database;
using LiteratureApp.Search;

namespace LiteratureApp.Infrastructure.Search;

public sealed class SearchIndexRebuilder : ISearchIndexRebuilder
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;

    public SearchIndexRebuilder(SqliteConnectionFactory connectionFactory, IClock clock)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
    }

    public async Task<Result> RebuildFtsForDocumentInstanceAsync(DocumentInstanceId documentInstanceId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var tx = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync("delete from search_units_fts where document_instance_id = @Id;", new { Id = documentInstanceId.ToString() }, tx);
            var units = await connection.QueryAsync<UnitRow>(
                "select unit_id as UnitId, document_instance_id as DocumentInstanceId, page_id as PageId, resolved_text as ResolvedText from search_units where document_instance_id = @Id and status = @Status and length(trim(resolved_text)) > 0;",
                new { Id = documentInstanceId.ToString(), Status = SearchUnitStatus.Current },
                tx);
            foreach (var unit in units)
            {
                await InsertFtsAsync(connection, tx, unit);
            }
            await tx.CommitAsync(cancellationToken);

            await SearchUnitBuilder.UpsertStatusAsync(connection, SearchIndexScopeType.DocumentInstance, documentInstanceId.ToString(), SearchIndexStatusValue.Current, 0, 0, null, null, cancellationToken);
            var libraryId = await connection.ExecuteScalarAsync<string?>("select library_id from library_metadata limit 1;");
            if (libraryId is not null)
            {
                await SearchUnitBuilder.UpsertStatusAsync(connection, SearchIndexScopeType.Library, libraryId, SearchIndexStatusValue.Partial, 0, 0, $"document_instance:{documentInstanceId}", "Document instance FTS was rebuilt; library index may still be partial.", cancellationToken);
            }
            return Result.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await SetIndexUnavailableAsync(SearchIndexScopeType.DocumentInstance, documentInstanceId.ToString(), ex.Message, cancellationToken);
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result> RebuildFtsForLibraryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var tx = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync("delete from search_units_fts;", transaction: tx);
            var units = await connection.QueryAsync<UnitRow>(
                "select unit_id as UnitId, document_instance_id as DocumentInstanceId, page_id as PageId, resolved_text as ResolvedText from search_units where status = @Status and length(trim(resolved_text)) > 0;",
                new { Status = SearchUnitStatus.Current },
                tx);
            foreach (var unit in units)
            {
                await InsertFtsAsync(connection, tx, unit);
            }
            await tx.CommitAsync(cancellationToken);

            var libraryId = await connection.ExecuteScalarAsync<string?>("select library_id from library_metadata limit 1;");
            if (libraryId is not null)
            {
                await SearchUnitBuilder.UpsertStatusAsync(connection, SearchIndexScopeType.Library, libraryId, SearchIndexStatusValue.Current, 0, 0, null, null, cancellationToken);
            }
            return Result.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await SetIndexUnavailableAsync(SearchIndexScopeType.Library, "current", ex.Message, cancellationToken);
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result> SetIndexUnavailableAsync(string scopeType, string scopeId, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scopeType) || string.IsNullOrWhiteSpace(scopeId) || string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "Scope and reason are required.");
        }

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await SearchUnitBuilder.UpsertStatusAsync(connection, scopeType.Trim(), scopeId.Trim(), SearchIndexStatusValue.Unavailable, 0, 0, null, reason.Trim(), cancellationToken);
            return Result.Success();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}"); }
    }

    internal static string BuildIndexText(string canonicalText)
    {
        var builder = new StringBuilder(canonicalText);
        var cjk = canonicalText.Where(IsCjk).ToArray();
        if (cjk.Length > 0)
        {
            builder.Append(' ');
            builder.AppendJoin(' ', cjk);
            for (var i = 0; i < cjk.Length - 1; i++)
            {
                builder.Append(' ');
                builder.Append(cjk[i]);
                builder.Append(cjk[i + 1]);
            }
        }
        return builder.ToString();
    }

    private static bool IsCjk(char c)
        => (c >= '\u3400' && c <= '\u9fff') || (c >= '\uf900' && c <= '\ufaff');

    private static Task InsertFtsAsync(Microsoft.Data.Sqlite.SqliteConnection connection, System.Data.Common.DbTransaction tx, UnitRow unit)
        => connection.ExecuteAsync(
            "insert into search_units_fts (unit_id, document_instance_id, page_id, resolved_text) values (@UnitId, @DocumentInstanceId, @PageId, @ResolvedText);",
            new { unit.UnitId, unit.DocumentInstanceId, unit.PageId, ResolvedText = BuildIndexText(unit.ResolvedText) },
            tx);

    private sealed class UnitRow
    {
        public string UnitId { get; set; } = "";
        public string DocumentInstanceId { get; set; } = "";
        public string PageId { get; set; } = "";
        public string ResolvedText { get; set; } = "";
    }
}
