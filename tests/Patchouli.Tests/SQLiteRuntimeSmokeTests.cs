using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Patchouli.Tests;

public sealed class SQLiteRuntimeSmokeTests
{
    [Fact]
    public async Task SqliteConnectionFactory_opens_database_with_foreign_keys_enabled()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        await using SqliteConnection connection = database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();

        int foreignKeys = await connection.ExecuteScalarAsync<int>("PRAGMA foreign_keys;");
        foreignKeys.Should().Be(1);

        await connection.ExecuteAsync(
            """
            CREATE TABLE smoke_test (
                id INTEGER PRIMARY KEY NOT NULL,
                value TEXT NOT NULL
            );
            """);

        await connection.ExecuteAsync(
            "INSERT INTO smoke_test (id, value) VALUES (@Id, @Value);",
            new { Id = 1, Value = "sqlite-ok" });

        string? value = await connection.ExecuteScalarAsync<string>(
            "SELECT value FROM smoke_test WHERE id = @Id;",
            new { Id = 1 });

        value.Should().Be("sqlite-ok");
    }

    [Fact]
    public async Task CreateReadConnection_is_file_level_read_only_and_rejects_writes()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        await using (SqliteConnection general = database.ConnectionFactory.CreateConnection())
        {
            await general.OpenAsync();
            await general.ExecuteAsync(
                "CREATE TABLE smoke_readonly (id INTEGER PRIMARY KEY NOT NULL, value TEXT NOT NULL);");
        }

        await using SqliteConnection connection = database.ConnectionFactory.CreateReadConnection();
        await connection.OpenAsync();

        int existing = await connection.ExecuteScalarAsync<int>("SELECT count(1) FROM smoke_readonly;");
        existing.Should().Be(0);

        SqliteException? insertError = await WriteErrorAsync(connection,
            "INSERT INTO smoke_readonly (id, value) VALUES (@Id, @Value);",
            new { Id = 1, Value = "must-be-rejected" });
        insertError.Should().NotBeNull("the read-only connection must reject INSERT at the file level");

        SqliteException? ddlError = await WriteErrorAsync(connection,
            "CREATE TABLE smoke_never_created (id INTEGER PRIMARY KEY NOT NULL);");
        ddlError.Should().NotBeNull("the read-only connection must reject DDL at the file level");

        int queryOnly = await connection.ExecuteScalarAsync<int>("PRAGMA query_only;");
        queryOnly.Should().Be(1, "pooled read connections must also be query-only at the SQLite connection level");

        int busyTimeout = await connection.ExecuteScalarAsync<int>("PRAGMA busy_timeout;");
        busyTimeout.Should().Be(30000);
    }

    private static async Task<SqliteException?> WriteErrorAsync(SqliteConnection connection, string sql,
        object? parameters = null)
    {
        try
        {
            if (parameters is null)
            {
                await connection.ExecuteAsync(sql);
            }
            else
            {
                await connection.ExecuteAsync(sql, parameters);
            }

            return null;
        }
        catch (SqliteException exception)
        {
            return exception;
        }
    }

    [Fact]
    public async Task Writer_connections_remain_writer_owned_after_read_only_traffic()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        await using (SqliteConnection general = database.ConnectionFactory.CreateConnection())
        {
            await general.OpenAsync();
            await general.ExecuteAsync(
                "CREATE TABLE smoke_writer (id INTEGER PRIMARY KEY NOT NULL, value TEXT NOT NULL);");
            await general.ExecuteAsync(
                "INSERT INTO smoke_writer (id, value) VALUES (@Id, @Value);",
                new { Id = 1, Value = "initial" });
        }

        await using (SqliteConnection reader = database.ConnectionFactory.CreateReadConnection())
        {
            await reader.OpenAsync();
            string? value =
                await reader.ExecuteScalarAsync<string>("SELECT value FROM smoke_writer WHERE id = @Id;",
                    new { Id = 1 });
            value.Should().Be("initial");
        }

        await using SqliteConnection writer = database.ConnectionFactory.CreateConnection();
        await writer.OpenAsync();
        await writer.ExecuteAsync(
            "INSERT INTO smoke_writer (id, value) VALUES (@Id, @Value);",
            new { Id = 2, Value = "writer-owned" });
        string? after =
            await writer.ExecuteScalarAsync<string>("SELECT value FROM smoke_writer WHERE id = @Id;", new { Id = 2 });
        after.Should().Be("writer-owned");

        int queryOnly = await writer.ExecuteScalarAsync<int>("PRAGMA query_only;");
        queryOnly.Should().Be(0, "read-only connection state must never leak from a pool into writers");

        int busyTimeout = await writer.ExecuteScalarAsync<int>("PRAGMA busy_timeout;");
        busyTimeout.Should().Be(30000);

        await using (SqliteConnection reader = database.ConnectionFactory.CreateReadConnection())
        {
            await reader.OpenAsync();
            string? observed =
                await reader.ExecuteScalarAsync<string>("SELECT value FROM smoke_writer WHERE id = @Id;",
                    new { Id = 2 });
            observed.Should().Be("writer-owned",
                "a read-only connection never leaks read mode state into a writer-owned connection and observes committed writes");
        }
    }
}
