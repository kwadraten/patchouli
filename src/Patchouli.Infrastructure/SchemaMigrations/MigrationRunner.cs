using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
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

        await LibraryCompatibilityGuard.CheckAsync(_connectionFactory.DatabasePath, cancellationToken);
        await new SqliteWalBootstrapper(_connectionFactory).EnableWalAsync(cancellationToken);

        await using SqliteConnection connection = _connectionFactory.CreateAdminConnection();
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync("pragma busy_timeout = 30000;");

        await EnsureSchemaMigrationsTableAsync(connection);

        // Issues #4 and #11 used these IDs before main assigned them to document types and CSL.
        // Match the full name so existing main databases keep their migration history.
        await connection.ExecuteAsync(
            """
            update schema_migrations set id = '050', name = '050_file_search_root_sort_order'
            where id = '048' and name = '048_file_search_root_sort_order';
            update schema_migrations set id = '051', name = '051_file_import_recovery'
            where id = '049' and name = '049_file_import_recovery';
            update schema_migrations set id = '052', name = '052_create_document_page_maps'
            where id = '048' and name = '048_create_document_page_maps';
            """);

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
                if (file.Id == "061" && await connection.ExecuteScalarAsync<long>(
                        "select count(*) from pragma_table_info('library_metadata') where name = 'min_host_version';",
                        transaction: transaction) == 0)
                {
                    await connection.ExecuteAsync("alter table library_metadata add column min_host_version text;",
                        transaction: transaction);
                }

                if (file.Id == "048")
                {
                    await DocumentBoxTypeMigration.ApplyAsync(connection, transaction, sql);
                }
                else if (file.Id == "054")
                {
                    await ContinuationStateMigration.ApplyAsync(connection, transaction, sql);
                }
                else
                {
                    await connection.ExecuteAsync(sql, transaction: transaction);
                }

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
