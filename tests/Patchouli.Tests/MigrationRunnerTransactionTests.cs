using Dapper;
using FluentAssertions;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class MigrationRunnerTransactionTests
{
    [Fact]
    public async Task Failed_migration_is_not_recorded_and_is_attempted_again()
    {
        await using var database = TemporarySqliteDatabase.Create();
        using var migrations = TemporaryMigrationDirectory.Create();
        migrations.Write("0001_create_attempt_marker.sql", "create table attempt_marker (id integer not null);");
        migrations.Write("0002_failing_migration.sql", "insert into attempt_marker (id) values (1); select * from missing_table;");

        var runner = new MigrationRunner(database.ConnectionFactory, migrations.Path);

        var firstFailure = await Assert.ThrowsAsync<MigrationFailedException>(() => runner.RunAsync());
        var secondFailure = await Assert.ThrowsAsync<MigrationFailedException>(() => runner.RunAsync());

        await using var connection = database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();

        var failedMigrationRecords = await connection.ExecuteScalarAsync<int>(
            "select count(1) from schema_migrations where id = '0002';");
        var attemptRows = await connection.ExecuteScalarAsync<int>("select count(1) from attempt_marker;");

        firstFailure.MigrationId.Should().Be("0002");
        secondFailure.MigrationId.Should().Be("0002");
        failedMigrationRecords.Should().Be(0);
        attemptRows.Should().Be(0);
    }
}
