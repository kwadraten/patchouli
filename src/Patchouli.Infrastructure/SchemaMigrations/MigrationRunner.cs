using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.Migrations;

public sealed class MigrationRunner
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly string _migrationsDirectory;

    public MigrationRunner(SqliteConnectionFactory connectionFactory, string migrationsDirectory)
    {
        _connectionFactory = connectionFactory;
        _migrationsDirectory = migrationsDirectory;
    }

    public async Task<IReadOnlyList<AppliedMigration>> RunAsync(
        CancellationToken cancellationToken = default,
        IProgress<MigrationProgress>? progress = null)
    {
        if (!Directory.Exists(_migrationsDirectory))
        {
            throw new DirectoryNotFoundException($"Migrations directory was not found: {_migrationsDirectory}");
        }

        MigrationFile[] files = Directory
            .EnumerateFiles(_migrationsDirectory, "*.sql", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal)
            .Select(MigrationFile.FromPath)
            .ToArray();

        await new SqliteWalBootstrapper(_connectionFactory).EnableWalAsync(cancellationToken);

        await using SqliteConnection connection = _connectionFactory.CreateAdminConnection();
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync("pragma busy_timeout = 30000;");

        await EnsureSupportedSchemaEpochAsync(connection);
        await EnsureSchemaMigrationsTableAsync(connection);

        IReadOnlySet<string> appliedIds = await LoadAppliedMigrationIdsAsync(connection);
        List<MigrationFile> pending = files.Where(file => !appliedIds.Contains(file.Id)).ToList();
        List<AppliedMigration> applied = new();

        for (int i = 0; i < pending.Count; i++)
        {
            MigrationFile file = pending[i];
            string sql = await File.ReadAllTextAsync(file.Path, cancellationToken);

            await connection.ExecuteAsync("pragma foreign_keys = off;");
            long foreignKeys = await connection.ExecuteScalarAsync<long>("pragma foreign_keys;");
            if (foreignKeys != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to disable foreign keys before migration '{file.Name}' ({file.Id}); current value: {foreignKeys}.");
            }

            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

            try
            {
                progress?.Report(new MigrationProgress(file.Id, file.Name, i + 1, pending.Count));
                await connection.ExecuteAsync(sql, transaction: transaction);
                await connection.ExecuteAsync(
                    """
                    insert into schema_migrations (id, name, applied_at)
                    values (@Id, @Name, @AppliedAt);
                    """,
                    new
                    {
                        file.Id,
                        file.Name,
                        AppliedAt = DateTimeOffset.UtcNow.ToString("O")
                    },
                    transaction);

                await transaction.CommitAsync(cancellationToken);
                applied.Add(new AppliedMigration(file.Id, file.Name));
            }
            catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                                  "infrastructure.migration-runner"))
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new MigrationFailedException(file.Id, file.Name, file.Path, exception);
            }
            finally
            {
                await connection.ExecuteAsync("pragma foreign_keys = on;");
            }
        }

        return applied;
    }

    private static async Task<IReadOnlySet<string>> LoadAppliedMigrationIdsAsync(SqliteConnection connection)
    {
        IEnumerable<string> ids = await connection.QueryAsync<string>("select id from schema_migrations;");
        return ids.ToHashSet(StringComparer.Ordinal);
    }

    private static async Task EnsureSupportedSchemaEpochAsync(SqliteConnection connection)
    {
        int hasLibraryMetadata = await connection.ExecuteScalarAsync<int>(
            "select count(1) from sqlite_master where type = 'table' and name = 'library_metadata';");

        if (hasLibraryMetadata > 0)
        {
            int[] versions = (await connection.QueryAsync<int>(
                "select distinct schema_version from library_metadata;")).ToArray();
            if (versions.Any(version => version != AppSchemaVersion.Current))
            {
                throw new UnsupportedLibrarySchemaException(versions);
            }
        }

        int nonMigrationTableCount = await connection.ExecuteScalarAsync<int>(
            "select count(1) from sqlite_master where type = 'table' and name not in ('schema_migrations');");
        int hasSchemaMigrations = await connection.ExecuteScalarAsync<int>(
            "select count(1) from sqlite_master where type = 'table' and name = 'schema_migrations';");
        if (hasLibraryMetadata == 0 && hasSchemaMigrations == 0 && nonMigrationTableCount > 0)
        {
            throw new UnsupportedLibrarySchemaException([]);
        }

        int legacyTableCount = await connection.ExecuteScalarAsync<int>(
            """
            select count(1)
            from sqlite_master
            where type = 'table' and name in ('layout_revisions', 'layout_nodes');
            """);
        if (legacyTableCount > 0)
        {
            throw new UnsupportedLibrarySchemaException([1]);
        }
    }

    private static Task EnsureSchemaMigrationsTableAsync(SqliteConnection connection)
    {
        return connection.ExecuteAsync(
            """
            create table if not exists schema_migrations (
                id text primary key not null,
                name text not null,
                applied_at text not null
            );
            """);
    }

    private sealed record MigrationFile(string Path, string Id, string Name)
    {
        public static MigrationFile FromPath(string path)
        {
            string fileName = System.IO.Path.GetFileNameWithoutExtension(path);
            int separatorIndex = fileName.IndexOf('_', StringComparison.Ordinal);

            if (separatorIndex <= 0 || separatorIndex == fileName.Length - 1)
            {
                throw new InvalidOperationException($"Migration file name must use '<id>_<name>.sql': {path}");
            }

            return new MigrationFile(
                path,
                fileName[..separatorIndex],
                fileName);
        }
    }
}

public sealed class UnsupportedLibrarySchemaException : Exception
{
    public UnsupportedLibrarySchemaException(IReadOnlyCollection<int> schemaVersions)
        : base(BuildMessage(schemaVersions))
    {
        SchemaVersions = schemaVersions;
    }

    public IReadOnlyCollection<int> SchemaVersions { get; }

    private static string BuildMessage(IReadOnlyCollection<int> schemaVersions)
    {
        string found = schemaVersions.Count == 0 ? "unknown" : string.Join(", ", schemaVersions.Order());
        return $"Library schema epoch {found} is not supported by Patchouli 0.3.0. " +
               "Create a new library and re-import the source documents.";
    }
}

public sealed record AppliedMigration(string Id, string Name);

public sealed record MigrationProgress(string Id, string Name, int Ordinal, int Total);

public sealed class MigrationFailedException : Exception
{
    public MigrationFailedException(string migrationId, string migrationName, string migrationPath,
        Exception innerException)
        : base($"Migration '{migrationName}' ({migrationId}) failed: {migrationPath}", innerException)
    {
        MigrationId = migrationId;
        MigrationName = migrationName;
        MigrationPath = migrationPath;
    }

    public string MigrationId { get; }
    public string MigrationName { get; }
    public string MigrationPath { get; }
}
