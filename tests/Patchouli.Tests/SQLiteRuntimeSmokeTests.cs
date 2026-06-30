using Dapper;
using FluentAssertions;

namespace Patchouli.Tests;

public sealed class SQLiteRuntimeSmokeTests
{
    [Fact]
    public async Task SqliteConnectionFactory_opens_database_with_foreign_keys_enabled()
    {
        await using var database = TemporarySqliteDatabase.Create();
        await using var connection = database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();

        var foreignKeys = await connection.ExecuteScalarAsync<int>("PRAGMA foreign_keys;");
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

        var value = await connection.ExecuteScalarAsync<string>(
            "SELECT value FROM smoke_test WHERE id = @Id;",
            new { Id = 1 });

        value.Should().Be("sqlite-ok");
    }
}
