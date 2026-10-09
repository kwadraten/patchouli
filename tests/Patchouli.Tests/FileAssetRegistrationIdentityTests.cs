using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class FileAssetRegistrationIdentityTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"patchouli-file-identity-{Guid.NewGuid():N}");
    private SqliteConnectionFactory _database = null!;
    private FileAssetService _files = null!;
    private string _original = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _original = Path.Combine(_root, "original.pdf");
        await File.WriteAllTextAsync(_original, "original PDF content");
        _database = new SqliteConnectionFactory(Path.Combine(_root, "library.sqlite"));
        SystemClock clock = new();
        await new MigrationRunner(_database, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService library = new(_database, clock);
        (await library.CreateLibraryAsync("File identity regression")).IsSuccess.Should().BeTrue();
        _files = new FileAssetService(_database, library, clock);
    }

    public Task DisposeAsync()
    {
        _database.ClearPools();
        Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Registration_reuses_a_legacy_asset_id_by_exact_content_hash()
    {
        FileAsset original = (await _files.RegisterFileAsync(_original)).Value;
        FileAssetId legacyId = FileAssetId.New();
        await using SqliteConnection connection = _database.CreateConnection();
        await connection.OpenAsync();
        // Missing-source resolution can persist the verified hash without changing its random ID.
        await connection.ExecuteAsync(
            "delete from known_file_locations where file_asset_id = @Id;",
            new { Id = original.FileAssetId.ToString() });
        await connection.ExecuteAsync(
            "update file_assets set file_asset_id = @LegacyId where file_asset_id = @Id;",
            new { LegacyId = legacyId.ToString(), Id = original.FileAssetId.ToString() });
        string renamed = Path.Combine(_root, "renamed.pdf");
        File.Copy(_original, renamed);

        Result<FileAsset> registered = await _files.RegisterFileAsync(renamed);

        registered.IsSuccess.Should().BeTrue(registered.ErrorMessage);
        registered.Value.FileAssetId.Should().Be(legacyId);
        (await connection.ExecuteScalarAsync<int>("select count(*) from file_assets;")).Should().Be(1);
        (await connection.ExecuteScalarAsync<int>(
            "select count(*) from known_file_locations where file_asset_id = @Id and path = @Path;",
            new { Id = legacyId.ToString(), Path = renamed })).Should().Be(1);
    }

    [Fact]
    public async Task Registration_preserves_a_confirmed_changed_asset_that_retained_its_original_hash_id()
    {
        FileAsset original = (await _files.RegisterFileAsync(_original)).Value;
        string changedPath = Path.Combine(_root, "changed.pdf");
        await File.WriteAllTextAsync(changedPath, "different confirmed PDF content");
        FileFingerprint changed = (await new FileFingerprintService().GetFileMetadataAsync(changedPath)).Value;
        await using SqliteConnection connection = _database.CreateConnection();
        await connection.OpenAsync();
        // ConfirmChangedFileAsync retains the original ID while replacing the verified hash.
        await connection.ExecuteAsync(
            "update file_assets set full_blake3 = @Hash where file_asset_id = @Id;",
            new { Hash = changed.FullBlake3, Id = original.FileAssetId.ToString() });

        Result<FileAsset> registered = await _files.RegisterFileAsync(_original);

        registered.IsSuccess.Should().BeTrue(registered.ErrorMessage);
        registered.Value.FileAssetId.Should().NotBe(original.FileAssetId);
        registered.Value.FullBlake3.Should().Be(original.FullBlake3);
        (await connection.ExecuteScalarAsync<string>(
            "select full_blake3 from file_assets where file_asset_id = @Id;",
            new { Id = original.FileAssetId.ToString() })).Should().Be(changed.FullBlake3);
        (await connection.ExecuteScalarAsync<int>("select count(*) from file_assets;")).Should().Be(2);
        Result<FileAsset> replay = await _files.RegisterFileAsync(_original);
        replay.IsSuccess.Should().BeTrue(replay.ErrorMessage);
        replay.Value.FileAssetId.Should().Be(registered.Value.FileAssetId);
    }
}
