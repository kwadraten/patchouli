using Dapper;
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

    public async Task<IReadOnlyList<AppliedMigration>> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_migrationsDirectory))
        {
            throw new DirectoryNotFoundException($"Migrations directory was not found: {_migrationsDirectory}");
        }

        var files = Directory
            .EnumerateFiles(_migrationsDirectory, "*.sql", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal)
            .Select(MigrationFile.FromPath)
            .ToArray();

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await EnsureSchemaMigrationsTableAsync(connection);

        var applied = new List<AppliedMigration>();

        foreach (var file in files)
        {
            var alreadyApplied = await connection.ExecuteScalarAsync<int>(
                "select count(1) from schema_migrations where id = @Id;",
                new { file.Id });

            if (alreadyApplied > 0)
            {
                continue;
            }

            var sql = await File.ReadAllTextAsync(file.Path, cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            try
            {
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
            catch (Exception exception)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new MigrationFailedException(file.Id, file.Name, file.Path, exception);
            }
        }

        return applied;
    }

    private static Task EnsureSchemaMigrationsTableAsync(Microsoft.Data.Sqlite.SqliteConnection connection)
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
            var fileName = System.IO.Path.GetFileNameWithoutExtension(path);
            var separatorIndex = fileName.IndexOf('_', StringComparison.Ordinal);

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

public sealed class MigrationFailedException : Exception
{
    public MigrationFailedException(string migrationId, string migrationName, string migrationPath, Exception innerException)
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
