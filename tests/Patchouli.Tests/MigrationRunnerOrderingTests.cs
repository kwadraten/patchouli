using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class MigrationRunnerOrderingTests
{
    [Fact]
    public async Task Migrations_are_executed_in_file_name_order()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        using TemporaryMigrationDirectory migrations = TemporaryMigrationDirectory.Create();
        migrations.Write("0002_insert_second.sql", "insert into ordering_probe (value) values ('second');");
        migrations.Write("0001_create_ordering_probe.sql",
            "create table ordering_probe (value text not null); insert into ordering_probe (value) values ('first');");

        MigrationRunner runner = new(database.ConnectionFactory, migrations.Path);

        IReadOnlyList<AppliedMigration> applied = await runner.RunAsync();

        await using SqliteConnection connection = database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();

        string[] values = (await connection.QueryAsync<string>("select value from ordering_probe order by rowid;"))
            .ToArray();

        applied.Select(m => m.Name).Should().Equal("0001_create_ordering_probe", "0002_insert_second");
        values.Should().Equal("first", "second");
    }
}
