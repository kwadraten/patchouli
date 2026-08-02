using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class SqliteWalBootstrapperTests
{
    [Fact]
    public async Task EnableWalAsync_switches_a_fresh_database_to_wal_journal_mode()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();

        await new SqliteWalBootstrapper(database.ConnectionFactory).EnableWalAsync();

        await using SqliteConnection connection = database.ConnectionFactory.CreateAdminConnection();
        await connection.OpenAsync();
        string journalMode = (await connection.ExecuteScalarAsync<string>("pragma journal_mode;"))!;
        journalMode.Should().Be("wal");
    }

    [Fact]
    public async Task EnableWalAsync_is_idempotent()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        SqliteWalBootstrapper bootstrapper = new(database.ConnectionFactory);

        await bootstrapper.EnableWalAsync();
        await bootstrapper.EnableWalAsync();

        await using SqliteConnection connection = database.ConnectionFactory.CreateAdminConnection();
        await connection.OpenAsync();
        string journalMode = (await connection.ExecuteScalarAsync<string>("pragma journal_mode;"))!;
        journalMode.Should().Be("wal");
    }

    [Fact]
    public async Task MigrationRunner_enables_wal_before_running_migrations()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();

        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();

        await using SqliteConnection connection = database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        string journalMode = (await connection.ExecuteScalarAsync<string>("pragma journal_mode;"))!;
        journalMode.Should().Be("wal");
    }

    [Fact]
    public async Task EnableWalAsync_fails_with_a_diagnostic_when_wal_cannot_be_entered()
    {
        string missingDirectory = Path.Combine(Path.GetTempPath(), $"patchouli-wal-missing-{Guid.NewGuid():N}");
        SqliteConnectionFactory factory = new(Path.Combine(missingDirectory, "database.sqlite"));

        SqliteWalBootstrapper bootstrapper = new(factory);
        Func<Task> enable = () => bootstrapper.EnableWalAsync();
        await enable.Should().ThrowAsync<SqliteWalBootstrapFailedException>();
    }
}
