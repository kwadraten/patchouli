using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Ids;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Core.Search;

namespace Patchouli.Infrastructure.Search;

public sealed class SearchIndexRebuilder : ISearchIndexRebuilder
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly IHostActivityTracker? _activityTracker;
    private readonly CancellationToken _hostLifetime;

    public SearchIndexRebuilder(SqliteConnectionFactory connectionFactory, IClock clock,
        IHostActivityTracker? activityTracker = null, CancellationToken hostLifetime = default)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _activityTracker = activityTracker;
        _hostLifetime = hostLifetime;
    }

    public async Task<Result> RebuildFtsForDocumentInstanceAsync(DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _hostLifetime);
        cancellationToken = linkedCancellation.Token;
        using IActivityScope? activity = _activityTracker?.BeginScope(
            "重建搜索索引",
            HostActivityKind.Indexing,
            $"文档 {documentInstanceId}");
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction tx = await connection.BeginTransactionAsync(cancellationToken);
            await SearchFtsCacheWriter.EnsureReadyAsync(connection, tx, cancellationToken);
            await SearchFtsCacheWriter.DeleteForDocumentInstancesAsync(
                connection, tx, [documentInstanceId.ToString()], cancellationToken);
            IEnumerable<SearchFtsUnitRow> units = await connection.QueryAsync<SearchFtsUnitRow>(
                "select unit_id as UnitId, document_instance_id as DocumentInstanceId, page_id as PageId, resolved_text as ResolvedText from search_units where document_instance_id = @Id and status = @Status and length(trim(resolved_text)) > 0;",
                new { Id = documentInstanceId.ToString(), Status = SearchUnitStatus.Current },
                tx);
            foreach (SearchFtsUnitRow[] chunk in units.Chunk(500))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SearchFtsCacheWriter.InsertBatchAsync(connection, tx, chunk, cancellationToken);
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

    public async Task<Result> EnsureCacheAsync(CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _hostLifetime);
        cancellationToken = linkedCancellation.Token;
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            await SearchFtsCacheWriter.EnsureReadyAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(
                                              exception,
                                              "infrastructure.search-index-rebuilder"))
        {
            await SetIndexUnavailableAsync(SearchIndexScopeType.Library, "current", exception.Message,
                cancellationToken);
            return Result.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    public async Task<Result> RebuildFtsForLibraryAsync(CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _hostLifetime);
        cancellationToken = linkedCancellation.Token;
        using IActivityScope? activity = _activityTracker?.BeginScope(
            "重建搜索索引",
            HostActivityKind.Indexing,
            "整个资料库");
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction tx = await connection.BeginTransactionAsync(cancellationToken);
            await SearchFtsCacheWriter.RebuildAllAsync(connection, tx, cancellationToken);

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
}
