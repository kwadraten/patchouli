using System.Security.Cryptography;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class MigratedDatabaseTemplateTests
{
    [Fact]
    public async Task A_copy_matches_a_fresh_migration_in_schema_and_migration_records()
    {
        await using TemporarySqliteDatabase reference = TemporarySqliteDatabase.Create();
        await new MigrationRunner(reference.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        await using TemporarySqliteDatabase copy = await TemporarySqliteDatabase.CreateMigratedAsync();

        IReadOnlyList<SchemaRow> referenceSchema = await ReadSchemaAsync(reference);
        IReadOnlyList<SchemaRow> copySchema = await ReadSchemaAsync(copy);
        copySchema.Should().Equal(referenceSchema,
            "the copy carries every table, index, trigger, constraint definition and FTS shadow " +
            "table the real migration produced");
        copySchema.Should().Contain(row => row.Type == "index", "migration-created indexes exist");
        copySchema.Should().Contain(row => row.Type == "trigger", "migration-created triggers exist");
        copySchema.Should().Contain(row => row.Sql.Contains("fts5", StringComparison.OrdinalIgnoreCase),
            "the FTS5 virtual table is part of the copied schema");
        copySchema.Should().Contain(row => row.Sql.Contains("foreign key", StringComparison.OrdinalIgnoreCase),
            "foreign key constraints are part of the copied schema");

        IReadOnlyList<MigrationRecord> copyRecords = await ReadMigrationRecordsAsync(copy);
        copyRecords.Should().Equal(await ReadMigrationRecordsAsync(reference),
            "the copy inherits the complete schema_migrations history");
        copyRecords.Select(record => record.Name).Should().Equal(
            Directory.EnumerateFiles(TestPaths.MigrationsDirectory, "*.sql")
                .Select(path => Path.GetFileNameWithoutExtension(path)!)
                .Order(StringComparer.Ordinal),
            "every migration file has a recorded row");

        (await ScalarAsync<long>(copy, "select count(1) from search_units_fts;")).Should().Be(0,
            "the FTS table is queryable on the copy");
        await using SqliteConnection check = copy.ConnectionFactory.CreateReadConnection();
        await check.OpenAsync();
        (await check.ExecuteScalarAsync<string>("pragma integrity_check;")).Should().Be("ok");
    }

    [Fact]
    public async Task A_copy_enforces_foreign_keys_like_a_fresh_migration()
    {
        await using TemporarySqliteDatabase reference = TemporarySqliteDatabase.Create();
        await new MigrationRunner(reference.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        await using TemporarySqliteDatabase copy = await TemporarySqliteDatabase.CreateMigratedAsync();

        const string orphanInsert =
            "insert into pages (page_id, document_instance_id, page_index, rotation, " +
            "coordinate_basis, renderer_basis_version, created_at, updated_at) values " +
            "('p', 'missing-document-instance', 0, 0, 'normalized_page', 'test', 'now', 'now');";

        // ReSharper disable AccessToDisposedClosure -- both delegates are awaited inside this scope.
        Func<Task> onReference = () => ExecuteAsync(reference, orphanInsert);
        Func<Task> onCopy = () => ExecuteAsync(copy, orphanInsert);
        // ReSharper restore AccessToDisposedClosure
        await onReference.Should().ThrowAsync<SqliteException>(
            "the freshly migrated database enforces the document_instances foreign key");
        await onCopy.Should().ThrowAsync<SqliteException>(
            "the template copy enforces the same foreign key");
    }

    [Fact]
    public async Task Two_copies_are_isolated_and_writes_never_reach_the_template()
    {
        string templatePath = await MigratedDatabaseTemplate.Shared.EnsureInitializedAsync();
        byte[] templateHash = SHA256.HashData(await File.ReadAllBytesAsync(templatePath));
        await using TemporarySqliteDatabase first = await TemporarySqliteDatabase.CreateMigratedAsync();
        await using TemporarySqliteDatabase second = await TemporarySqliteDatabase.CreateMigratedAsync();
        first.Path.Should().NotBe(second.Path, "each case owns an independent file");

        await ExecuteAsync(first,
            "create table first_only (id integer primary key); insert into first_only values (1);");
        await ExecuteAsync(second,
            "create table second_only (id integer primary key); insert into second_only values (2);");

        (await ScalarAsync<long>(first, "select count(1) from first_only;")).Should().Be(1);
        (await ScalarAsync<long>(second, "select count(1) from second_only;")).Should().Be(1);
        // ReSharper disable once AccessToDisposedClosure -- the delegate is awaited inside this scope.
        Func<Task> firstInSecond = () => ScalarAsync<long>(second, "select count(1) from first_only;");
        await firstInSecond.Should().ThrowAsync<SqliteException>(
            "writes stay inside their own copy");

        (File.GetAttributes(templatePath) & FileAttributes.ReadOnly).Should().Be(FileAttributes.ReadOnly,
            "the shared template is never writable");
        SHA256.HashData(await File.ReadAllBytesAsync(templatePath)).Should().Equal(templateHash,
            "no consumer's writes reached the template file");
    }

    [Fact]
    public async Task Concurrent_consumers_run_the_real_migration_once()
    {
        int migrations = 0;
        await using MigratedDatabaseTemplate template = new(async (factory, token) =>
        {
            Interlocked.Increment(ref migrations);
            await new MigrationRunner(factory, TestPaths.MigrationsDirectory).RunAsync(token);
        });

        // ReSharper disable once AccessToDisposedClosure -- the tasks are awaited inside this scope.
        Task<string>[] inits = Enumerable.Range(0, 8)
            .Select(_ => template.EnsureInitializedAsync())
            .ToArray();
        string[] paths = await Task.WhenAll(inits);

        paths.Should().OnlyContain(path => path == paths[0], "one shared template serves every caller");
        migrations.Should().Be(1, "concurrent callers share a single real migration");
        File.Exists(paths[0]).Should().BeTrue();

        string[] copies = Enumerable.Range(0, 4).Select(_ => NewDatabasePath()).ToArray();
        try
        {
            // ReSharper disable once AccessToDisposedClosure -- the tasks are awaited inside this scope.
            await Task.WhenAll(copies.Select(copy => template.CreateCopyAsync(copy)));
            copies.Should().OnlyContain(path => File.Exists(path),
                "independent concurrent copies all materialized");
        }
        finally
        {
            foreach (string copy in copies)
            {
                TestTempFileCleanup.DeleteFileWithRetry(copy);
            }
        }
    }

    [Fact]
    public async Task Concurrent_CreateMigratedAsync_calls_stay_independent()
    {
        TemporarySqliteDatabase[] databases = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => TemporarySqliteDatabase.CreateMigratedAsync()));
        try
        {
            databases.Select(database => database.Path).Distinct().Should()
                .HaveCount(databases.Length, "every case owns its own file");
            foreach (TemporarySqliteDatabase database in databases)
            {
                (await ScalarAsync<long>(database, "select count(1) from schema_migrations;"))
                    .Should().BeGreaterThan(0);
            }
        }
        finally
        {
            foreach (TemporarySqliteDatabase database in databases)
            {
                await database.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task A_failed_first_migration_is_visible_and_the_next_caller_retries()
    {
        int attempts = 0;
        string? attemptedPath = null;
        await using MigratedDatabaseTemplate template = new(async (factory, token) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                attemptedPath = factory.DatabasePath;
                throw new InvalidOperationException("Simulated first-migration failure.");
            }

            await new MigrationRunner(factory, TestPaths.MigrationsDirectory).RunAsync(token);
        });

        // ReSharper disable once AccessToDisposedClosure -- the delegate is awaited inside this scope.
        Func<Task> first = () => template.EnsureInitializedAsync();
        await first.Should().ThrowAsync<InvalidOperationException>(
            "the first migration failure reaches every waiter");
        attemptedPath.Should().NotBeNull();
        File.Exists(attemptedPath!).Should().BeFalse("the failed attempt removes its partial template");
        File.Exists($"{attemptedPath}-wal").Should().BeFalse();
        File.Exists($"{attemptedPath}-shm").Should().BeFalse();

        string readyPath = await template.EnsureInitializedAsync();
        attempts.Should().Be(2, "a failed attempt does not poison later callers");
        readyPath.Should().NotBe(attemptedPath);
        File.Exists(readyPath).Should().BeTrue();
    }

    [Fact]
    public async Task A_copy_failure_never_deletes_a_preexisting_destination()
    {
        await using MigratedDatabaseTemplate template = new();
        string destination = NewDatabasePath();
        try
        {
            await File.WriteAllTextAsync(destination, "pre-existing content");
            // ReSharper disable once AccessToDisposedClosure -- the delegate is awaited inside this scope.
            Func<Task> copy = () => template.CreateCopyAsync(destination);
            await copy.Should().ThrowAsync<IOException>();
            (await File.ReadAllTextAsync(destination)).Should().Be("pre-existing content",
                "a file the copy did not create is left untouched");
        }
        finally
        {
            TestTempFileCleanup.DeleteFileWithRetry(destination);
        }
    }

    [Fact]
    public async Task The_template_is_checkpointed_read_only_and_still_wal_mode()
    {
        string templatePath = await MigratedDatabaseTemplate.Shared.EnsureInitializedAsync();
        (File.GetAttributes(templatePath) & FileAttributes.ReadOnly).Should().Be(FileAttributes.ReadOnly);
        (File.Exists($"{templatePath}-wal") && new FileInfo($"{templatePath}-wal").Length > 0).Should()
            .BeFalse("the template is fully checkpointed before copies are made");
        File.Exists($"{templatePath}-shm").Should().BeFalse();

        await using TemporarySqliteDatabase copy = await TemporarySqliteDatabase.CreateMigratedAsync();
        await using SqliteConnection connection = copy.ConnectionFactory.CreateAdminConnection();
        await connection.OpenAsync();
        (await connection.ExecuteScalarAsync<string>("pragma journal_mode;")).Should().Be("wal",
            "copies inherit WAL journal mode like a freshly migrated database");
    }

    [Fact]
    public async Task The_template_carries_no_library_business_or_credential_state()
    {
        await using TemporarySqliteDatabase copy = await TemporarySqliteDatabase.CreateMigratedAsync();
        string[] businessTables =
        [
            "library_metadata", "items", "file_assets", "document_instances", "pages",
            "document_tree_revisions", "document_commits", "page_translations", "translation_boxes",
            "ocr_runs", "search_units", "library_setting_records",
            "search_settings", "csl_settings", "collections"
        ];
        await using SqliteConnection connection = copy.ConnectionFactory.CreateReadConnection();
        await connection.OpenAsync();
        foreach (string table in businessTables)
        {
            (await connection.ExecuteScalarAsync<int>($"select count(1) from {table};"))
                .Should().Be(0, $"the template must not carry {table} rows between cases");
        }

        (await connection.ExecuteScalarAsync<int>("select count(1) from schema_migrations;"))
            .Should().BeGreaterThan(0, "schema_migrations bookkeeping is the only expected content");
    }

    [Fact]
    public async Task Cancelling_the_first_initialization_cleans_up_and_allows_retry()
    {
        using CancellationTokenSource cancellation = new();
        int attempts = 0;
        string? attemptedPath = null;
        // ReSharper disable once AccessToDisposedClosure -- the delegate only runs while an
        // awaited template call is inside this test's scope.
        Func<SqliteConnectionFactory, CancellationToken, Task> migrate = (factory, token) =>
        {
            attemptedPath = factory.DatabasePath;
            if (Interlocked.Increment(ref attempts) == 1)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }

            return new MigrationRunner(factory, TestPaths.MigrationsDirectory).RunAsync(token);
        };
        await using MigratedDatabaseTemplate template = new(migrate);

        // ReSharper disable once AccessToDisposedClosure -- the delegate is awaited inside this scope.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            template.EnsureInitializedAsync(cancellation.Token));
        attemptedPath.Should().NotBeNull();
        File.Exists(attemptedPath!).Should().BeFalse("the cancelled attempt removes its partial template");
        File.Exists($"{attemptedPath}-wal").Should().BeFalse();

        string readyPath = await template.EnsureInitializedAsync();
        attempts.Should().Be(2, "a cancelled attempt does not poison later callers");
        File.Exists(readyPath).Should().BeTrue();
    }

    [Fact]
    public async Task Cancelling_a_copy_request_leaves_no_destination_file()
    {
        await using MigratedDatabaseTemplate template = new();
        await template.EnsureInitializedAsync();
        string destination = NewDatabasePath();
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        CancellationToken cancellationToken = cancelled.Token;

        // The assertion invokes and awaits this copy before the template's async-disposal scope ends.
        // ReSharper disable once AccessToDisposedClosure
        Func<Task> copy = () => template.CreateCopyAsync(destination, cancellationToken);
        await copy.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(destination).Should().BeFalse("a cancelled copy leaves no partial destination");
    }

    [Fact]
    public async Task Create_still_returns_an_empty_database()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        await using SqliteConnection connection = database.ConnectionFactory.CreateAdminConnection();
        await connection.OpenAsync();
        (await connection.ExecuteScalarAsync<int>(
                "select count(1) from sqlite_master where type = 'table';"))
            .Should().Be(0, "Create() stays the empty-database entry point");
    }

    private static string NewDatabasePath()
    {
        return Path.Combine(Path.GetTempPath(), $"patchouli-template-test-{Guid.NewGuid():N}.sqlite");
    }

    private static async Task ExecuteAsync(TemporarySqliteDatabase database, string sql)
    {
        await using SqliteConnection connection = database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync(sql);
    }

    private static async Task<T> ScalarAsync<T>(TemporarySqliteDatabase database, string sql)
    {
        await using SqliteConnection connection = database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        return (await connection.ExecuteScalarAsync<T>(sql))!;
    }

    private static async Task<IReadOnlyList<SchemaRow>> ReadSchemaAsync(TemporarySqliteDatabase database)
    {
        await using SqliteConnection connection = database.ConnectionFactory.CreateReadConnection();
        await connection.OpenAsync();
        return (await connection.QueryAsync<SchemaRow>(
            """
            select type as Type, name as Name, tbl_name as TableName, coalesce(sql, '') as Sql
            from sqlite_master
            order by type, name;
            """)).ToArray();
    }

    private static async Task<IReadOnlyList<MigrationRecord>> ReadMigrationRecordsAsync(
        TemporarySqliteDatabase database)
    {
        await using SqliteConnection connection = database.ConnectionFactory.CreateReadConnection();
        await connection.OpenAsync();
        // applied_at legitimately differs between the template and a fresh run; identity is id + name.
        return (await connection.QueryAsync<MigrationRecord>(
            "select id as Id, name as Name from schema_migrations order by name;")).ToArray();
    }

    private sealed record SchemaRow(string Type, string Name, string TableName, string Sql);

    private sealed record MigrationRecord(string Id, string Name);
}
