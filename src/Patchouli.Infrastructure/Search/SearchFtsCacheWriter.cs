using System.Data.Common;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Search;

namespace Patchouli.Infrastructure.Search;

internal static class SearchFtsCacheWriter
{
    private const int BatchSize = 500;
    private const int CurrentCacheVersion = 1;

    internal static async Task EnsureReadyAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        bool mapExisted = await TableExistsAsync(connection, transaction, "fts_row_map", cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);
        if (!mapExisted)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "delete from fts_cache_state where state_id = 1;",
                transaction: transaction,
                cancellationToken: cancellationToken));
        }

        int? cacheVersion = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "select cache_version from fts_cache_state where state_id = 1;",
            transaction: transaction,
            cancellationToken: cancellationToken));
        if (cacheVersion == CurrentCacheVersion)
        {
            bool hasMappedRows = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "select exists (select 1 from fts_row_map limit 1);",
                transaction: transaction,
                cancellationToken: cancellationToken));
            bool hasFtsRows = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "select exists (select 1 from search_units_fts limit 1);",
                transaction: transaction,
                cancellationToken: cancellationToken));
            if (hasMappedRows == hasFtsRows)
            {
                if (hasMappedRows)
                {
                    return;
                }

                bool hasCurrentUnits = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "select exists (select 1 from search_units where status = @Status limit 1);",
                    new { Status = SearchUnitStatus.Current },
                    transaction,
                    cancellationToken: cancellationToken));
                if (!hasCurrentUnits)
                {
                    return;
                }
            }
        }

        await RebuildAllAsync(connection, transaction, cancellationToken);
    }

    internal static async Task RebuildAllAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(connection, transaction, cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "delete from search_units_fts;",
            transaction: transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "delete from fts_row_map;",
            transaction: transaction,
            cancellationToken: cancellationToken));

        IEnumerable<SearchFtsUnitRow> units = await connection.QueryAsync<SearchFtsUnitRow>(new CommandDefinition(
            """
            select unit_id as UnitId, document_instance_id as DocumentInstanceId,
                page_id as PageId, resolved_text as ResolvedText
            from search_units
            where status = @Status and length(trim(resolved_text)) > 0;
            """,
            new { Status = SearchUnitStatus.Current },
            transaction,
            cancellationToken: cancellationToken));

        foreach (SearchFtsUnitRow[] chunk in units.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await InsertBatchAsync(connection, transaction, chunk, cancellationToken);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into fts_cache_state (state_id, cache_version)
            values (1, @CacheVersion)
            on conflict(state_id) do update set cache_version = excluded.cache_version;
            """,
            new { CacheVersion = CurrentCacheVersion },
            transaction,
            cancellationToken: cancellationToken));
    }

    internal static async Task InvalidateAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(connection, transaction, cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "delete from search_units_fts;",
            transaction: transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "delete from fts_row_map;",
            transaction: transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "delete from fts_cache_state where state_id = 1;",
            transaction: transaction,
            cancellationToken: cancellationToken));
    }

    internal static async Task DeleteForDocumentInstancesAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        IReadOnlyCollection<string> documentInstanceIds,
        CancellationToken cancellationToken)
    {
        if (documentInstanceIds.Count == 0)
        {
            return;
        }

        foreach (string[] documentBatch in documentInstanceIds.Chunk(BatchSize))
        {
            while (true)
            {
                long[] rowIds = (await connection.QueryAsync<long>(new CommandDefinition(
                    """
                    select fts_row_id
                    from fts_row_map
                    where document_instance_id in @DocumentInstanceIds
                    order by fts_row_id
                    limit @BatchSize;
                    """,
                    new { DocumentInstanceIds = documentBatch, BatchSize },
                    transaction,
                    cancellationToken: cancellationToken))).ToArray();
                if (rowIds.Length == 0)
                {
                    break;
                }

                await DeleteMappedRowsAsync(connection, transaction, rowIds, cancellationToken);
            }
        }
    }

    internal static async Task InsertBatchAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        IReadOnlyList<SearchFtsUnitRow> units,
        CancellationToken cancellationToken)
    {
        if (units.Count == 0)
        {
            return;
        }

        string[] unitIds = units.Select(static unit => unit.UnitId).ToArray();
        await DeleteForUnitIdsAsync(connection, transaction, unitIds, cancellationToken);

        StringBuilder mapValues = new();
        Dictionary<string, object?> mapParameters = new();
        for (int i = 0; i < units.Count; i++)
        {
            SearchFtsUnitRow unit = units[i];
            if (i > 0)
            {
                mapValues.Append(',');
            }

            mapValues.Append("(@p").Append(i).Append("DocId,@p").Append(i).Append("UnitId)");
            string prefix = "p" + i;
            mapParameters[prefix + "DocId"] = unit.DocumentInstanceId;
            mapParameters[prefix + "UnitId"] = unit.UnitId;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "insert into fts_row_map (document_instance_id, unit_id) values " + mapValues,
            mapParameters,
            transaction,
            cancellationToken: cancellationToken));

        Dictionary<string, long> rowIdByUnit = (await connection.QueryAsync<FtsMapRow>(new CommandDefinition(
                "select fts_row_id as FtsRowId, unit_id as UnitId from fts_row_map where unit_id in @UnitIds;",
                new { UnitIds = unitIds },
                transaction,
                cancellationToken: cancellationToken)))
            .ToDictionary(static row => row.UnitId, static row => row.FtsRowId, StringComparer.Ordinal);

        StringBuilder ftsValues = new();
        Dictionary<string, object?> ftsParameters = new();
        for (int i = 0; i < units.Count; i++)
        {
            SearchFtsUnitRow unit = units[i];
            if (!rowIdByUnit.TryGetValue(unit.UnitId, out long rowId))
            {
                throw new InvalidOperationException($"FTS row mapping was not created for SearchUnit '{unit.UnitId}'.");
            }

            if (i > 0)
            {
                ftsValues.Append(',');
            }

            ftsValues.Append("(@p").Append(i).Append("RowId,@p").Append(i).Append("UnitId,@p").Append(i)
                .Append("DocId,@p").Append(i).Append("PageId,@p").Append(i).Append("Text)");
            string prefix = "p" + i;
            ftsParameters[prefix + "RowId"] = rowId;
            ftsParameters[prefix + "UnitId"] = unit.UnitId;
            ftsParameters[prefix + "DocId"] = unit.DocumentInstanceId;
            ftsParameters[prefix + "PageId"] = unit.PageId;
            ftsParameters[prefix + "Text"] = SearchIndexRebuilder.BuildIndexText(unit.ResolvedText);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "insert into search_units_fts (rowid, unit_id, document_instance_id, page_id, resolved_text) values " +
            ftsValues,
            ftsParameters,
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task DeleteForUnitIdsAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        IReadOnlyCollection<string> unitIds,
        CancellationToken cancellationToken)
    {
        foreach (string[] unitBatch in unitIds.Chunk(BatchSize))
        {
            while (true)
            {
                long[] rowIds = (await connection.QueryAsync<long>(new CommandDefinition(
                    """
                    select fts_row_id
                    from fts_row_map
                    where unit_id in @UnitIds
                    order by fts_row_id
                    limit @BatchSize;
                    """,
                    new { UnitIds = unitBatch, BatchSize },
                    transaction,
                    cancellationToken: cancellationToken))).ToArray();
                if (rowIds.Length == 0)
                {
                    break;
                }

                await DeleteMappedRowsAsync(connection, transaction, rowIds, cancellationToken);
            }
        }
    }

    private static async Task DeleteMappedRowsAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        IReadOnlyCollection<long> rowIds,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "delete from search_units_fts where rowid in @RowIds;",
            new { RowIds = rowIds },
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "delete from fts_row_map where fts_row_id in @RowIds;",
            new { RowIds = rowIds },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task EnsureSchemaAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            create table if not exists fts_row_map (
                fts_row_id integer primary key autoincrement,
                document_instance_id text not null,
                unit_id text not null
            );
            create unique index if not exists idx_fts_row_map_unit_id on fts_row_map(unit_id);
            create index if not exists idx_fts_row_map_document_instance_id
                on fts_row_map(document_instance_id, fts_row_id);
            create table if not exists fts_cache_state (
                state_id integer primary key check (state_id = 1),
                cache_version integer not null
            );
            """,
            transaction: transaction,
            cancellationToken: cancellationToken));
    }

    private static Task<bool> TableExistsAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        return connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from sqlite_master where type = 'table' and name = @TableName);",
            new { TableName = tableName },
            transaction,
            cancellationToken: cancellationToken));
    }

    private sealed class FtsMapRow
    {
        public long FtsRowId { get; set; }
        public string UnitId { get; set; } = "";
    }
}

internal sealed class SearchFtsUnitRow
{
    public string UnitId { get; set; } = "";
    public string DocumentInstanceId { get; set; } = "";
    public string PageId { get; set; } = "";
    public string ResolvedText { get; set; } = "";
}
