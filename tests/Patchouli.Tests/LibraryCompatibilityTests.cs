using System.Security.Cryptography;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Core.Update;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Snapshots;
using Patchouli.Host.Composition;
using Patchouli.UI;

namespace Patchouli.Tests;

public sealed class LibraryCompatibilityTests
{
    [Fact]
    public async Task HostServices_gate_fails_before_composition()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        await using (SqliteConnection connection = database.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "create table library_metadata(schema_version integer, min_host_version text);" +
                "insert into library_metadata values(2, '99.0.0');");
        }

        PatchouliAppSettings defaults = PatchouliAppSettings.Default();
        PatchouliAppSettings settings = defaults with
        {
            Runtime = defaults.Runtime with
            {
                DefaultSyncRoot = Path.Combine(Path.GetTempPath(), $"unused-sync-{Guid.NewGuid():N}"),
                LogDirectory = Path.Combine(Path.GetTempPath(), $"unused-log-{Guid.NewGuid():N}")
            }
        };
        List<StartupStage> stages = [];
        string databasePath = database.Path;
        Func<Task> create = () => HostServices.CreateAsync(databasePath, settings,
            startupProgress: new ImmediateProgress<StartupStage>(stages.Add));
        await create.Should().ThrowAsync<LibraryRequiresNewerHostException>();
        stages.Should().NotContain(StartupStage.ComposingServices);
        Directory.Exists(settings.Runtime.LogDirectory).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, "0.4.1")]
    [InlineData("", "0.4.1")]
    [InlineData("0.4.2", "0.4.2")]
    public async Task Migration_backfills_only_empty_minimum(string? original, string expected)
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateMigratedAsync();
        await using (SqliteConnection connection = database.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into library_metadata(library_id, display_name, schema_version, created_at, updated_at, min_host_version)
                values('test', 'Test', 2, 'now', 'now', @original);
                delete from schema_migrations where id='061';
                """, new { original });
        }

        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        await using SqliteConnection verify = database.ConnectionFactory.CreateReadConnection();
        await verify.OpenAsync();
        (await verify.ExecuteScalarAsync<string>("select min_host_version from library_metadata;")).Should()
            .Be(expected);
    }

    [Theory]
    [InlineData(2, "0.4.1", "0.4.2", LibraryCompatibilityStatus.Compatible)]
    [InlineData(2, "0.4.10", "0.4.2", LibraryCompatibilityStatus.RequiresNewerHost)]
    [InlineData(3, "0.4.1", "0.4.2", LibraryCompatibilityStatus.RequiresNewerHost)]
    [InlineData(1, "0.4.1", "0.4.2", LibraryCompatibilityStatus.TooOld)]
    [InlineData(1, "9.0.0", "0.4.2", LibraryCompatibilityStatus.RequiresNewerHost)]
    [InlineData(2, "invalid", "0.4.2", LibraryCompatibilityStatus.InvalidOrCorrupt)]
    public void Policy_classifies_in_required_order(int epoch, string minimum, string host,
        LibraryCompatibilityStatus expected)
    {
        LibraryCompatibility.Evaluate(epoch, minimum, host).Should().Be(expected);
        LibraryCompatibility.Evaluate(epoch, minimum, host, true)
            .Should().Be(LibraryCompatibilityStatus.InvalidOrCorrupt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Migration_gate_rejects_high_minimum_without_writing_main_database(bool wal)
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        await using SqliteConnection writer = new(new SqliteConnectionStringBuilder
        {
            DataSource = database.Path, Pooling = false
        }.ToString());
        await writer.OpenAsync();
        await writer.ExecuteAsync(wal ? "pragma journal_mode=wal;" : "pragma journal_mode=delete;");
        await writer.ExecuteAsync(
            "create table library_metadata(schema_version integer, min_host_version text);" +
            "insert into library_metadata values (2, '99.0.0');");
        if (wal)
        {
            await writer.ExecuteAsync("pragma wal_checkpoint(truncate);");
        }

        byte[] before = await HashMainFileAsync(database.Path);
        string journal = await writer.ExecuteScalarAsync<string>("pragma journal_mode;") ?? "";
        MigrationRunner runner = new(database.ConnectionFactory, TestPaths.MigrationsDirectory);
        Func<Task> run = () => runner.RunAsync();
        LibraryRequiresNewerHostException exception = (await run.Should()
            .ThrowAsync<LibraryRequiresNewerHostException>()).Which;
        exception.RequiredHostVersion.Should().Be("99.0.0");
        (await HashMainFileAsync(database.Path)).Should().Equal(before);
        (await writer.ExecuteScalarAsync<string>("pragma journal_mode;")).Should().Be(journal);
        (await writer.ExecuteScalarAsync<int>(
            "select count(*) from sqlite_master where name='schema_migrations';")).Should().Be(0);
    }

    [Theory]
    [InlineData("create table other(value text);")]
    [InlineData(
        "create table library_metadata(schema_version integer, min_host_version text); insert into library_metadata values(2,'bad');")]
    [InlineData("create table library_metadata(schema_version integer); insert into library_metadata values(2),(3);")]
    [InlineData("create table library_metadata(schema_version integer); insert into library_metadata values(null);")]
    [InlineData(
        "create table library_metadata(schema_version integer); insert into library_metadata values(2); create table layout_nodes(node_id text);")]
    public async Task Guard_fails_closed_for_unrecognized_metadata(string sql)
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        await using (SqliteConnection connection = database.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(sql);
        }

        string databasePath = database.Path;
        Func<Task> check = () => LibraryCompatibilityGuard.CheckAsync(databasePath);
        await check.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Guard_distinguishes_old_and_new_epoch(int epoch)
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        await using (SqliteConnection connection = database.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync("create table library_metadata(schema_version integer);" +
                                          "insert into library_metadata values(@epoch);", new { epoch });
        }

        string databasePath = database.Path;
        Func<Task> check = () => LibraryCompatibilityGuard.CheckAsync(databasePath);
        if (epoch == 1)
        {
            await check.Should().ThrowAsync<UnsupportedLibrarySchemaException>();
        }
        else
        {
            await check.Should().ThrowAsync<LibraryRequiresNewerHostException>();
        }
    }

    [Fact]
    public async Task New_library_is_readable_and_its_snapshot_importable_by_same_host()
    {
        UpdateVersion.Parse(BuildInfo.Version)
            .CompareTo(UpdateVersion.Parse(LibraryCompatibility.NewLibraryMinHostVersion))
            .Should().BeGreaterThanOrEqualTo(0);
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateMigratedAsync();
        LibraryIdentityService identity = new(database.ConnectionFactory, new SystemClock());
        (await identity.CreateLibraryAsync("Compatibility")).IsSuccess.Should().BeTrue();
        await using (SqliteConnection connection = database.ConnectionFactory.CreateReadConnection())
        {
            await connection.OpenAsync();
            (await connection.ExecuteScalarAsync<string>("select min_host_version from library_metadata;"))
                .Should().Be("0.4.2");
        }

        await LibraryCompatibilityGuard.CheckAsync(database.Path);
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        string root = Path.Combine(Path.GetTempPath(), $"compatibility-snapshot-{Guid.NewGuid():N}");
        try
        {
            Result<SnapshotPublishResult> published =
                await new SnapshotPublisher(new SystemClock()).PublishSnapshotAsync(
                    new SnapshotPublishRequest(database.Path, root, "device"));
            published.IsSuccess.Should().BeTrue(published.ErrorMessage);
            string staging = Path.Combine(root, "staging");
            Result<SnapshotImportResult> imported = await new SnapshotImporter().ImportSnapshotToStagingAsync(
                new SnapshotImportRequest(published.Value.ManifestPath, staging));
            imported.IsSuccess.Should().BeTrue(imported.ErrorMessage);
            imported.Value.IsValid.Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                SqliteTestCleanup.ReleasePoolsInDirectory(root);
                Directory.Delete(root, true);
            }
        }
    }

    private static async Task<byte[]> HashMainFileAsync(string path)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return await SHA256.HashDataAsync(stream);
    }

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            report(value);
        }
    }
}
