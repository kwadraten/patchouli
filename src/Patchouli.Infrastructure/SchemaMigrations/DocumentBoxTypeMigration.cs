using System.Data.Common;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Patchouli.Infrastructure.Migrations;

internal static class DocumentBoxTypeMigration
{
    public static async Task ApplyAsync(SqliteConnection connection, DbTransaction transaction, string sql)
    {
        string guard = await connection.ExecuteScalarAsync<string>(
                           "select sql from sqlite_master where type='trigger' and name='document_boxes_committed_update_guard';",
                           transaction: transaction) ??
                       throw new InvalidOperationException("The committed-box update guard is missing.");
        // Representation migration is the only exception to committed-payload immutability.
        // Both the guard and every row change are restored by the runner's rollback on failure.
        await connection.ExecuteAsync("drop trigger document_boxes_committed_update_guard;", transaction: transaction);
        await RemoveBaseTypeAsync(connection, transaction);
        await connection.ExecuteAsync(sql, transaction: transaction);
        await connection.ExecuteAsync(guard, transaction: transaction);
    }

    private static async Task RemoveBaseTypeAsync(SqliteConnection connection, DbTransaction transaction)
    {
        string[] columns = (await connection.QueryAsync<string>(
            "select name from pragma_table_info('document_boxes') order by cid;", transaction: transaction)).ToArray();
        if (!columns.Contains("base_type", StringComparer.Ordinal))
        {
            return;
        }

        // Preserve the meaning of old extension types before removing their fallback column.
        await connection.ExecuteAsync("""
                                      update document_boxes set box_type = case base_type
                                          when 'image' then 'image' when 'table' then 'table' when 'code' then 'code' else 'text' end
                                      where box_type not in (
                                          'logical_page','text','title','ref_text','equation','equation_interline','formula',
                                          'list','index','image','table','chart','code','algorithm',
                                          'image_caption','image_footnote','table_caption','table_footnote','chart_caption',
                                          'chart_footnote','code_caption','code_footnote','header','page_header','footer',
                                          'page_footer','page_number','aside_text','page_aside_text','page_footnote');
                                      """, transaction: transaction);

        string definition = await connection.ExecuteScalarAsync<string>(
                                "select sql from sqlite_master where type='table' and name='document_boxes';",
                                transaction: transaction)
                            ?? throw new InvalidOperationException("Document box table is missing.");
        definition = Regex.Replace(definition, @"\bbase_type\s+text\s+null\s*,", "", RegexOptions.IgnoreCase);
        definition = Regex.Replace(definition,
            @",\s*check\s*\(base_type\s+is\s+null\s+or\s+base_type\s+in\s*\([^)]*\)\s*\)",
            "", RegexOptions.IgnoreCase);
        if (definition.Contains("base_type", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unsupported base_type table constraint; migration was rolled back.");
        }

        SchemaObject[] dependencies = (await connection.QueryAsync<SchemaObject>("""
            select type as Type, name as Name, sql as Sql from sqlite_master
            where sql is not null and
                ((type='index' and tbl_name='document_boxes') or
                 (type='trigger' and instr(lower(sql), 'document_boxes') > 0));
            """, transaction: transaction)).ToArray();
        foreach (SchemaObject dependency in dependencies.Where(entry => entry.Type == "trigger"))
        {
            await connection.ExecuteAsync($"drop trigger {Quote(dependency.Name)};", transaction: transaction);
        }

        // Keep foreign keys pointing at the final table name. Foreign keys are disabled by the runner.
        int open = definition.IndexOf('(');
        await connection.ExecuteAsync("create table document_boxes_without_base_type " + definition[open..],
            transaction: transaction);
        string names = string.Join(",", columns.Where(name => name != "base_type").Select(Quote));
        await connection.ExecuteAsync($"""
                                       insert into document_boxes_without_base_type ({names}) select {names} from document_boxes;
                                       drop table document_boxes;
                                       alter table document_boxes_without_base_type rename to document_boxes;
                                       """, transaction: transaction);
        foreach (SchemaObject dependency in dependencies)
        {
            await connection.ExecuteAsync(dependency.Sql, transaction: transaction);
        }
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private sealed record SchemaObject(string Type, string Name, string Sql);
}
