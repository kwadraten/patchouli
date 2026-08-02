using System.Data.Common;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Core.Search;

namespace Patchouli.Infrastructure.Search;

public sealed class SearchIndexRebuilder : ISearchIndexRebuilder
{
    private const int FtsWriteBatchSize = 500;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;

    public SearchIndexRebuilder(SqliteConnectionFactory connectionFactory, IClock clock)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
    }

    public async Task<Result> RebuildFtsForDocumentInstanceAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction tx = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync("delete from search_units_fts where document_instance_id = @Id;",
                new { Id = documentInstanceId.ToString() }, tx);
            IEnumerable<UnitRow> units = await connection.QueryAsync<UnitRow>(
                "select unit_id as UnitId, document_instance_id as DocumentInstanceId, page_id as PageId, resolved_text as ResolvedText from search_units where document_instance_id = @Id and status = @Status and length(trim(resolved_text)) > 0;",
                new { Id = documentInstanceId.ToString(), Status = SearchUnitStatus.Current },
                tx);
            foreach (UnitRow[] chunk in units.Chunk(FtsWriteBatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await InsertFtsBatchAsync(connection, tx, chunk);
            }

            await tx.CommitAsync(cancellationToken);

            await SearchUnitBuilder.UpsertStatusAsync(connection, SearchIndexScopeType.DocumentInstance,
                documentInstanceId.ToString(), SearchIndexStatusValue.Current, 0, 0, null, null, cancellationToken);
            string? libraryId =
                await connection.ExecuteScalarAsync<string?>("select library_id from library_metadata limit 1;");
            if (libraryId is not null)
            {
                await SearchUnitBuilder.UpsertStatusAsync(connection, SearchIndexScopeType.Library, libraryId,
                    SearchIndexStatusValue.Partial, 0, 0, $"document_instance:{documentInstanceId}",
                    "Document instance FTS was rebuilt; library index may still be partial.", cancellationToken);
            }

            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.search-index-rebuilder"))
        {
            await SetIndexUnavailableAsync(SearchIndexScopeType.DocumentInstance, documentInstanceId.ToString(),
                ex.Message, cancellationToken);
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result> RebuildFtsForLibraryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction tx = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync("delete from search_units_fts;", transaction: tx);
            IEnumerable<UnitRow> units = await connection.QueryAsync<UnitRow>(
                "select unit_id as UnitId, document_instance_id as DocumentInstanceId, page_id as PageId, resolved_text as ResolvedText from search_units where status = @Status and length(trim(resolved_text)) > 0;",
                new { Status = SearchUnitStatus.Current },
                tx);
            foreach (UnitRow[] chunk in units.Chunk(FtsWriteBatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await InsertFtsBatchAsync(connection, tx, chunk);
            }

            await tx.CommitAsync(cancellationToken);

            string? libraryId =
                await connection.ExecuteScalarAsync<string?>("select library_id from library_metadata limit 1;");
            if (libraryId is not null)
            {
                await SearchUnitBuilder.UpsertStatusAsync(connection, SearchIndexScopeType.Library, libraryId,
                    SearchIndexStatusValue.Current, 0, 0, null, null, cancellationToken);
            }

            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.search-index-rebuilder"))
        {
            await SetIndexUnavailableAsync(SearchIndexScopeType.Library, "current", ex.Message, cancellationToken);
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}");
        }
    }

    public async Task<Result> SetIndexUnavailableAsync(string scopeType, string scopeId, string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scopeType) || string.IsNullOrWhiteSpace(scopeId) ||
            string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "Scope and reason are required.");
        }

        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await SearchUnitBuilder.UpsertStatusAsync(connection, scopeType.Trim(), scopeId.Trim(),
                SearchIndexStatusValue.Unavailable, 0, 0, null, reason.Trim(), cancellationToken);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.search-index-rebuilder"))
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {ex.Message}");
        }
    }

    internal static string BuildIndexText(string canonicalText)
    {
        return SearchTextAnalyzer.BuildIndexText(canonicalText);
    }

    private static Task InsertFtsBatchAsync(SqliteConnection connection,
        DbTransaction tx, IReadOnlyList<UnitRow> chunk)
    {
        StringBuilder values = new();
        Dictionary<string, object?> parameters = new();
        for (int i = 0; i < chunk.Count; i++)
        {
            UnitRow unit = chunk[i];
            if (i > 0)
            {
                values.Append(',');
            }

            values.Append("(@p").Append(i).Append("UnitId,@p").Append(i).Append("DocId,@p").Append(i)
                .Append("PageId,@p").Append(i).Append("Text)");
            string prefix = "p" + i;
            parameters[prefix + "UnitId"] = unit.UnitId;
            parameters[prefix + "DocId"] = unit.DocumentInstanceId;
            parameters[prefix + "PageId"] = unit.PageId;
            parameters[prefix + "Text"] = BuildIndexText(unit.ResolvedText);
        }

        return connection.ExecuteAsync(
            "insert into search_units_fts (unit_id, document_instance_id, page_id, resolved_text) values " +
            values,
            parameters,
            tx);
    }

    private sealed class UnitRow
    {
        public string UnitId { get; set; } = "";
        public string DocumentInstanceId { get; set; } = "";
        public string PageId { get; set; } = "";
        public string ResolvedText { get; set; } = "";
    }
}
