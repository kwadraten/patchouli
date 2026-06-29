using Dapper;
using FluentAssertions;
using LiteratureApp.Infrastructure.Database;
using LiteratureApp.Infrastructure.Migrations;

namespace LiteratureApp.Tests;

public sealed class MigrationRunnerTests
{
    [Fact]
    public async Task RunAsync_creates_schema_migrations_table()
    {
        await using var database = TemporarySqliteDatabase.Create();
        var runner = new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory);

        await runner.RunAsync();

        await using var connection = database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();

        var tableCount = await connection.ExecuteScalarAsync<int>(
            """
            select count(1)
            from sqlite_master
            where type = 'table' and name = 'schema_migrations';
            """);

        tableCount.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_is_idempotent_for_already_applied_migrations()
    {
        await using var database = TemporarySqliteDatabase.Create();
        var runner = new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory);
        var expectedMigrationCount = Directory
            .EnumerateFiles(TestPaths.MigrationsDirectory, "*.sql", SearchOption.TopDirectoryOnly)
            .Count();

        var firstRun = await runner.RunAsync();
        var secondRun = await runner.RunAsync();

        await using var connection = database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();

        var appliedCount = await connection.ExecuteScalarAsync<int>("select count(1) from schema_migrations;");

        firstRun.Should().Contain(m => m.Id == "0001" && m.Name == "0001_create_schema_migrations");
        firstRun.Should().Contain(m => m.Id == "002" && m.Name == "002_create_library_metadata");
        firstRun.Should().Contain(m => m.Id == "003" && m.Name == "003_create_bibliographic_core");
        firstRun.Should().Contain(m => m.Id == "004" && m.Name == "004_create_file_resolution");
        firstRun.Should().Contain(m => m.Id == "005" && m.Name == "005_create_pages_and_layout");
        firstRun.Should().Contain(m => m.Id == "006" && m.Name == "006_create_ocr_lifecycle");
        firstRun.Should().Contain(m => m.Id == "007" && m.Name == "007_create_search_units_and_fts");
        firstRun.Should().Contain(m => m.Id == "008" && m.Name == "008_create_evidence_refs");
        firstRun.Should().Contain(m => m.Id == "009" && m.Name == "009_create_provider_credentials");
        firstRun.Should().HaveCount(expectedMigrationCount);
        secondRun.Should().BeEmpty();
        appliedCount.Should().Be(expectedMigrationCount);
    }
}
