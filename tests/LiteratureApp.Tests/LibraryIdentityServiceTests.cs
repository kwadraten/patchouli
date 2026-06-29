using Dapper;
using FluentAssertions;
using LiteratureApp.Core;
using LiteratureApp.Core.Ids;
using LiteratureApp.Core.Results;
using LiteratureApp.Infrastructure.LibraryIdentity;
using LiteratureApp.Infrastructure.Migrations;

namespace LiteratureApp.Tests;

public sealed class LibraryIdentityServiceTests
{
    [Fact]
    public async Task CreateLibrary_creates_stable_library_id()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        var clock = new FixedClock(new DateTimeOffset(2026, 6, 19, 1, 0, 0, TimeSpan.Zero));
        var service = new LibraryIdentityService(database.ConnectionFactory, clock);

        var result = await service.CreateLibraryAsync("Qing archives");

        result.IsSuccess.Should().BeTrue();
        result.Value.LibraryId.Value.Should().NotBe(Guid.Empty);
        result.Value.DisplayName.Should().Be("Qing archives");
        result.Value.SchemaVersion.Should().Be(AppSchemaVersion.Current);
        result.Value.CreatedAt.Should().Be(clock.UtcNow);
        result.Value.UpdatedAt.Should().Be(clock.UtcNow);
    }

    [Fact]
    public async Task RenameLibrary_does_not_change_library_id()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        var createdAt = new DateTimeOffset(2026, 6, 19, 1, 0, 0, TimeSpan.Zero);
        var clock = new FixedClock(createdAt);
        var service = new LibraryIdentityService(database.ConnectionFactory, clock);
        var created = await service.CreateLibraryAsync("Draft name");

        clock.UtcNow = createdAt.AddMinutes(5);
        var renamed = await service.RenameLibraryAsync("Archive notes");

        renamed.IsSuccess.Should().BeTrue();
        renamed.Value.LibraryId.Should().Be(created.Value.LibraryId);
        renamed.Value.DisplayName.Should().Be("Archive notes");
        renamed.Value.CreatedAt.Should().Be(created.Value.CreatedAt);
        renamed.Value.UpdatedAt.Should().Be(clock.UtcNow);
        renamed.Value.UpdatedAt.Should().BeOnOrAfter(renamed.Value.CreatedAt);
    }

    [Fact]
    public async Task GetCurrentLibrary_returns_not_found_when_missing()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        var service = new LibraryIdentityService(
            database.ConnectionFactory,
            new FixedClock(DateTimeOffset.UnixEpoch));

        var result = await service.GetCurrentLibraryAsync();

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.NotFound);
    }

    [Fact]
    public async Task CreateLibrary_rejects_blank_display_name()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        var service = new LibraryIdentityService(
            database.ConnectionFactory,
            new FixedClock(DateTimeOffset.UnixEpoch));

        var result = await service.CreateLibraryAsync("   ");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task RenameLibrary_rejects_blank_display_name()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        var service = new LibraryIdentityService(
            database.ConnectionFactory,
            new FixedClock(DateTimeOffset.UnixEpoch));

        var result = await service.RenameLibraryAsync("   ");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task CreateLibrary_rejects_second_library_in_same_runtime_db()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        var service = new LibraryIdentityService(
            database.ConnectionFactory,
            new FixedClock(DateTimeOffset.UnixEpoch));

        var first = await service.CreateLibraryAsync("First library");
        var second = await service.CreateLibraryAsync("Second library");

        await using var connection = database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        var count = await connection.ExecuteScalarAsync<int>("select count(1) from library_metadata;");

        first.IsSuccess.Should().BeTrue();
        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(AppErrorCodes.InvalidState);
        count.Should().Be(1);
    }

    [Fact]
    public async Task ValidateLibraryId_returns_success_for_same_id()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        var service = new LibraryIdentityService(
            database.ConnectionFactory,
            new FixedClock(DateTimeOffset.UnixEpoch));
        var created = await service.CreateLibraryAsync("Library");

        var result = await service.ValidateLibraryIdAsync(created.Value.LibraryId);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateLibraryId_returns_library_mismatch_for_different_id()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        var service = new LibraryIdentityService(
            database.ConnectionFactory,
            new FixedClock(DateTimeOffset.UnixEpoch));
        await service.CreateLibraryAsync("Library");

        var result = await service.ValidateLibraryIdAsync(LibraryId.New());

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.LibraryMismatch);
    }

    [Fact]
    public async Task Library_id_survives_database_file_move()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        var service = new LibraryIdentityService(
            database.ConnectionFactory,
            new FixedClock(DateTimeOffset.UnixEpoch));
        var created = await service.CreateLibraryAsync("Portable library");

        var movedPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"literatureapp-moved-{Guid.NewGuid():N}.sqlite");

        try
        {
            File.Move(database.Path, movedPath);
            var movedDatabase = new TemporarySqliteDatabaseHandle(movedPath);
            var movedService = new LibraryIdentityService(
                movedDatabase.ConnectionFactory,
                new FixedClock(DateTimeOffset.UnixEpoch));

            var current = await movedService.GetCurrentLibraryAsync();

            current.IsSuccess.Should().BeTrue();
            current.Value.LibraryId.Should().Be(created.Value.LibraryId);
        }
        finally
        {
            if (File.Exists(movedPath))
            {
                File.Delete(movedPath);
            }
        }
    }

    [Fact]
    public async Task MigrationRunner_applies_library_metadata_migration()
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
            where type = 'table' and name = 'library_metadata';
            """);

        tableCount.Should().Be(1);
    }

    private static async Task<TemporarySqliteDatabase> CreateMigratedDatabaseAsync()
    {
        var database = TemporarySqliteDatabase.Create();
        var runner = new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory);
        await runner.RunAsync();
        return database;
    }

    private sealed class TemporarySqliteDatabaseHandle
    {
        public TemporarySqliteDatabaseHandle(string path)
        {
            ConnectionFactory = new LiteratureApp.Infrastructure.Database.SqliteConnectionFactory(path);
        }

        public LiteratureApp.Infrastructure.Database.SqliteConnectionFactory ConnectionFactory { get; }
    }
}
