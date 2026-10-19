using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

/// <summary>
/// Issue #20: migration 053 adds the target language that lets the run engine decide whether a stored
/// page translation is current for the requested language or must be retranslated.
/// </summary>
public sealed class TranslationMigrationTests
{
    [Fact]
    public async Task Migration_053_adds_a_nullable_target_language_column()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        await using SqliteConnection connection = database.ConnectionFactory.CreateReadConnection();
        await connection.OpenAsync();

        IReadOnlyList<ColumnRow> columns =
        [
            .. await connection.QueryAsync<ColumnRow>(
                "select name as Name, type as Type, \"notnull\" as \"NotNull\" " +
                "from pragma_table_info('page_translations');")
        ];

        ColumnRow column = columns.Should()
            .ContainSingle(candidate => candidate.Name == "target_language").Which;
        column.Type.Should().BeEquivalentTo("text");
        column.NotNull.Should().Be(0);
    }

    private sealed class ColumnRow
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public int NotNull { get; set; }
    }
}
