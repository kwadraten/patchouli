using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core;

namespace Patchouli.Infrastructure.Migrations;

public static class SchemaInspector
{
    public static async Task<IReadOnlyList<string>> InspectAsync(SqliteConnection connection,
        bool checkForeignKeys = true)
    {
        List<string> errors = new();
        string[] integrity = (await connection.QueryAsync<string>("pragma integrity_check;")).ToArray();
        if (integrity.Any(value => !string.Equals(value, "ok", StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"SQLite integrity check failed: {string.Join(", ", integrity)}");
        }

        string[] foreignKeys = checkForeignKeys
            ? (await connection.QueryAsync<string>("pragma foreign_key_check;")).ToArray()
            : [];
        if (foreignKeys.Length > 0)
        {
            errors.Add("SQLite foreign key check failed.");
        }

        int metadata = await connection.ExecuteScalarAsync<int>(
            "select count(1) from sqlite_master where type = 'table' and name = 'library_metadata';");
        if (metadata == 0)
        {
            errors.Add("Library schema epoch is missing.");
            return errors;
        }

        int[] epochs = (await connection.QueryAsync<int>(
            "select distinct schema_version from library_metadata where schema_version is not null;")).ToArray();
        if (epochs.Length != 1 || epochs[0] != AppSchemaVersion.Current)
        {
            errors.Add($"Library schema epoch is unknown, empty, or mixed: " +
                       (epochs.Length == 0 ? "unknown" : string.Join(", ", epochs.Order())));
        }

        return errors;
    }
}
