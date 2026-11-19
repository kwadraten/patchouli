using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core;
using Patchouli.Core.Library;

namespace Patchouli.Infrastructure.Migrations;

/// <summary>Read-only preflight: never bootstraps WAL or writes the main database.</summary>
public static class LibraryCompatibilityGuard
{
    public static async Task CheckAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            return;
        }

        try
        {
            await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            await connection.OpenAsync(cancellationToken);
            await CheckConnectionAsync(connection);
        }
        catch (SqliteException exception)
        {
            throw Corrupt(exception);
        }
        catch (System.Data.DataException exception)
        {
            throw Corrupt(exception);
        }
        catch (FormatException exception)
        {
            throw Corrupt(exception);
        }
    }

    private static InvalidDataException Corrupt(Exception inner)
    {
        return new InvalidDataException("Library structure is unrecognized or corrupt.", inner);
    }

    public static async Task<string> ReadMinimumAsync(SqliteConnection connection)
    {
        bool hasMinimum = await connection.ExecuteScalarAsync<long>(
            "select count(*) from pragma_table_info('library_metadata') where name = 'min_host_version';") > 0;
        string? minimum = hasMinimum
            ? await connection.ExecuteScalarAsync<string>("select min_host_version from library_metadata limit 1;")
            : null;
        return string.IsNullOrWhiteSpace(minimum) ? LibraryCompatibility.LegacyMinHostVersion : minimum;
    }

    public static async Task CheckConnectionAsync(SqliteConnection connection)
    {
        string[] tables = (await connection.QueryAsync<string>(
            "select name from sqlite_master where type = 'table' and name not like 'sqlite_%';")).ToArray();
        if (tables.Length == 0)
        {
            return;
        }

        if (!tables.Contains("library_metadata", StringComparer.Ordinal))
        {
            throw new InvalidDataException("Library metadata is missing.");
        }

        string[] columns = (await connection.QueryAsync<string>(
            "select name from pragma_table_info('library_metadata');")).ToArray();
        if (!columns.Contains("schema_version", StringComparer.Ordinal))
        {
            throw new InvalidDataException("Library schema epoch is missing.");
        }

        bool hasMinimum = columns.Contains("min_host_version", StringComparer.Ordinal);
        // At most one identity is valid. LIMIT 2 also detects mixed/multiple identities without a full scan.
        MetadataRow[] rows = (await connection.QueryAsync<MetadataRow>(
                $"select schema_version as SchemaEpoch, typeof(schema_version) as EpochType, {(hasMinimum ? "min_host_version" : "null")} as Minimum from library_metadata limit 2;"))
            .ToArray();
        // Migrations may have created the schema before the first-run identity transaction.
        if (rows.Length == 0)
        {
            return;
        }

        string? storedMinimum = rows[0].Minimum;
        string minimum = string.IsNullOrWhiteSpace(storedMinimum)
            ? LibraryCompatibility.LegacyMinHostVersion
            : storedMinimum;
        bool mixedLegacySchema = rows[0].SchemaEpoch >= LibraryCompatibility.MinimumSchemaEpoch &&
                                 (tables.Contains("layout_revisions", StringComparer.Ordinal) ||
                                  tables.Contains("layout_nodes", StringComparer.Ordinal));
        LibraryCompatibilityStatus status = LibraryCompatibility.Evaluate(rows[0].SchemaEpoch, minimum,
            BuildInfo.Version, rows.Length != 1 || rows[0].EpochType != "integer" || mixedLegacySchema);
        switch (status)
        {
            case LibraryCompatibilityStatus.Compatible:
                return;
            case LibraryCompatibilityStatus.RequiresNewerHost:
                throw new LibraryRequiresNewerHostException(minimum, BuildInfo.Version, rows[0].SchemaEpoch,
                    AppSchemaVersion.Current);
            case LibraryCompatibilityStatus.TooOld:
                throw new UnsupportedLibrarySchemaException([rows[0].SchemaEpoch!.Value]);
            case LibraryCompatibilityStatus.InvalidOrCorrupt:
            default:
                throw new InvalidDataException("Library metadata is unrecognized or corrupt.");
        }
    }

    private sealed class MetadataRow
    {
        public int? SchemaEpoch { get; set; }
        public string EpochType { get; set; } = "";
        public string? Minimum { get; set; }
    }
}
