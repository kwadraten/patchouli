using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.LibraryIdentity;

public sealed class LibraryItemQueryService : ILibraryItemQueryService
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public LibraryItemQueryService(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<Result<IReadOnlyList<LibraryItemRow>>> ListRowsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            IEnumerable<Row> rows = await connection.QueryAsync<Row>(
                """
                select
                    i.item_id as ItemId,
                    i.title as Title,
                    i.item_type as ItemType,
                    i.creators_json as CreatorsJson,
                    coalesce(
                        (select group_concat(
                            case
                                when length(trim(coalesce(c.literal, ''))) > 0 then c.literal
                                else trim(coalesce(c.given, '') || ' ' || coalesce(c.particles, '') || ' ' || coalesce(c.family, '') || ' ' || coalesce(c.suffix, ''))
                            end,
                            ', '
                        )
                         from item_creators c
                         where c.item_id = i.item_id and c.role = 'author'
                         order by c.sequence_index),
                        ''
                    ) as Authors,
                    coalesce((select d.literal from item_dates d where d.item_id = i.item_id and d.role = 'issued'), i.date, '') as Year,
                    i.publication_title as PublicationTitle,
                    di.document_instance_id as DocumentInstanceId,
                    fa.file_name as LinkedFileName,
                    (select count(1) from pages p where p.document_instance_id = di.document_instance_id) as PageCount,
                    coalesce((select sis.status from search_index_status sis where sis.scope_type = 'document_instance' and sis.scope_id = di.document_instance_id), 'not_indexed') as IndexStatus,
                    coalesce(di.status, 'unknown') as DocumentStatus,
                    (select count(1) from search_units su where su.document_instance_id = di.document_instance_id and su.status = 'current') as SearchUnitCount
                from items i
                left join document_instances di on di.item_id = i.item_id and di.is_primary = 1
                left join file_assets fa on fa.file_asset_id = di.file_asset_id
                where i.deleted_at is null
                order by i.created_at desc, i.title;
                """);
            return Result<IReadOnlyList<LibraryItemRow>>.Success(rows.Select(row => row.ToModel()).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.library-item-query"))
        {
            return Result<IReadOnlyList<LibraryItemRow>>.Failure(AppErrorCodes.DatabaseError,
                $"Database operation failed: {exception.Message}");
        }
    }

    private sealed class Row
    {
        public string ItemId { get; set; } = "";
        public string Title { get; set; } = "";
        public string ItemType { get; set; } = "";
        public string CreatorsJson { get; set; } = "[]";
        public string Authors { get; set; } = "";
        public string? Year { get; set; }
        public string? PublicationTitle { get; set; }
        public string? DocumentInstanceId { get; set; }
        public string? LinkedFileName { get; set; }
        public int PageCount { get; set; }
        public string IndexStatus { get; set; } = "";
        public string DocumentStatus { get; set; } = "";
        public int SearchUnitCount { get; set; }

        public LibraryItemRow ToModel()
        {
            return new LibraryItemRow(
                Patchouli.Core.Ids.ItemId.Parse(ItemId),
                Title,
                ItemType,
                string.IsNullOrWhiteSpace(Authors) ? FormatCreators(CreatorsJson) : Authors,
                string.IsNullOrWhiteSpace(Year) ? null : Year,
                PublicationTitle,
                string.IsNullOrWhiteSpace(DocumentInstanceId)
                    ? null
                    : Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId),
                LinkedFileName,
                PageCount,
                SearchUnitCount,
                SearchUnitCount > 0 ? $"indexed ({SearchUnitCount})" : $"not_indexed ({DocumentStatus})",
                IndexStatus);
        }
    }

    private static string FormatCreators(string creatorsJson)
    {
        try
        {
            using JsonDocument document =
                JsonDocument.Parse(string.IsNullOrWhiteSpace(creatorsJson) ? "[]" : creatorsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            return string.Join(", ", document.RootElement.EnumerateArray()
                .Select(element =>
                    element.TryGetProperty("name", out JsonElement name) ? name.GetString() :
                    element.TryGetProperty("Name", out JsonElement upperName) ? upperName.GetString() :
                    null)
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        catch
        {
            return string.Empty;
        }
    }
}
