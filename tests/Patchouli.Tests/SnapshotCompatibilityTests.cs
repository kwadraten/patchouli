using Dapper;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Snapshots;

namespace Patchouli.Tests;

public sealed class SnapshotCompatibilityTests
{
    [Theory]
    [InlineData(1, null, true)]
    [InlineData(2, "0.4.2", true)]
    [InlineData(2, null, false)]
    [InlineData(2, "", false)]
    [InlineData(2, "not-a-version", false)]
    [InlineData(2, "99.0.0", false)]
    [InlineData(1, "99.0.0", false)]
    [InlineData(3, "0.4.1", false)]
    [InlineData(0, "0.4.1", false)]
    public async Task Manifest_gate_validates_version_and_minimum_before_staging(int version, string? minimum,
        bool valid)
    {
        await using Context context = await Context.CreateAsync();
        SnapshotManifest manifest = context.Manifest with { ManifestVersion = version, MinHostVersion = minimum };
        await SnapshotPublisher.WriteJsonAtomicAsync(context.ManifestPath, manifest, default);
        if (minimum is null)
        {
            JsonObject json = JsonNode.Parse(await File.ReadAllTextAsync(context.ManifestPath))!.AsObject();
            json.Remove(nameof(SnapshotManifest.MinHostVersion));
            await File.WriteAllTextAsync(context.ManifestPath, json.ToJsonString());
        }

        Result<SnapshotValidationResult> validation =
            await new SnapshotImporter().ValidateSnapshotAsync(context.ManifestPath);
        validation.Value.IsValid.Should().Be(valid, string.Join(" ", validation.Value.Errors));
        if (!valid)
        {
            Result<SnapshotImportResult> imported = await new SnapshotImporter().ImportSnapshotToStagingAsync(
                new SnapshotImportRequest(context.ManifestPath, context.Staging));
            imported.Value.IsValid.Should().BeFalse();
            Directory.Exists(context.Staging).Should().BeFalse();
        }
    }

    [Theory]
    [InlineData(2, "99.0.0")]
    [InlineData(2, "bad")]
    [InlineData(3, "0.4.1")]
    [InlineData(1, "0.4.1")]
    public async Task Actual_shard_metadata_is_checked_even_with_recomputed_hash(int epoch, string minimum)
    {
        await using Context context = await Context.CreateAsync();
        SnapshotShard shard = context.Manifest.Shards[0];
        string path = Path.Combine(context.Root, shard.FileName);
        File.SetAttributes(path, FileAttributes.Normal);
        await using (SqliteConnection connection =
                     new(SnapshotPublisher.BuildConnectionString(path, SqliteOpenMode.ReadWrite)))
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "update library_metadata set schema_version=@epoch, min_host_version=@minimum;",
                new { epoch, minimum });
        }

        SnapshotShard changed = shard with
        {
            SizeBytes = new FileInfo(path).Length, Blake3 = await SnapshotPublisher.Blake3FileAsync(path)
        };
        SnapshotManifest manifest = context.Manifest with
        {
            ManifestVersion = 1, MinHostVersion = null,
            Shards = context.Manifest.Shards.Select(s => s == shard ? changed : s).ToArray()
        };
        await SnapshotPublisher.WriteJsonAtomicAsync(context.ManifestPath, manifest, default);
        Result<SnapshotImportResult> result = await new SnapshotImporter().ImportSnapshotToStagingAsync(
            new SnapshotImportRequest(context.ManifestPath, context.Staging));
        result.Value.IsValid.Should().BeFalse();
        result.Value.Warnings.Should().Contain(message => message.Contains("Shard schema invalid"));
        Directory.Exists(context.Staging).Should().BeFalse();
    }

    [Fact]
    public async Task Legacy_v1_shard_without_minimum_or_latest_migration_is_migratable()
    {
        await using Context context = await Context.CreateAsync();
        SnapshotShard shard = context.Manifest.Shards.Single();
        string path = Path.Combine(context.Root, shard.FileName);
        File.SetAttributes(path, FileAttributes.Normal);
        await using (SqliteConnection connection =
                     new(SnapshotPublisher.BuildConnectionString(path, SqliteOpenMode.ReadWrite)))
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync("alter table library_metadata drop column min_host_version;" +
                                          "delete from schema_migrations where id='061';");
        }

        SnapshotShard changed = shard with
        {
            SizeBytes = new FileInfo(path).Length, Blake3 = await SnapshotPublisher.Blake3FileAsync(path)
        };
        await SnapshotPublisher.WriteJsonAtomicAsync(context.ManifestPath, context.Manifest with
        {
            ManifestVersion = 1, MinHostVersion = null, Shards = [changed]
        }, default);
        Result<SnapshotImportResult> result = await new SnapshotImporter().ImportSnapshotToStagingAsync(
            new SnapshotImportRequest(context.ManifestPath, context.Staging));
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.IsValid.Should().BeTrue(string.Join(" ", result.Value.Warnings));
        await using SqliteConnection verify = new(SnapshotPublisher.BuildConnectionString(
            result.Value.StagingDatabasePath!, SqliteOpenMode.ReadOnly));
        await verify.OpenAsync();
        (await verify.ExecuteScalarAsync<string>("select min_host_version from library_metadata;")).Should()
            .Be("0.4.1");
    }

    private sealed class Context(
        TemporarySqliteDatabase database,
        string root,
        string manifestPath,
        SnapshotManifest manifest) : IAsyncDisposable
    {
        public string Root { get; } = root;
        public string ManifestPath { get; } = manifestPath;
        public SnapshotManifest Manifest { get; } = manifest;
        public string Staging => Path.Combine(Root, "staging");

        public static async Task<Context> CreateAsync()
        {
            TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateMigratedAsync();
            string root = Path.Combine(Path.GetTempPath(), $"snapshot-compatibility-{Guid.NewGuid():N}");
            try
            {
                (await new LibraryIdentityService(database.ConnectionFactory, new SystemClock())
                    .CreateLibraryAsync("Snapshot compatibility")).IsSuccess.Should().BeTrue();
                // Model an existing, migrated Library. Export must still set the new manifest floor.
                await using (SqliteConnection connection = database.ConnectionFactory.CreateConnection())
                {
                    await connection.OpenAsync();
                    await connection.ExecuteAsync("update library_metadata set min_host_version='0.4.1';");
                }

                Result<SnapshotPublishResult> result =
                    await new SnapshotPublisher(new SystemClock()).PublishSnapshotAsync(
                        new SnapshotPublishRequest(database.Path, root, "device"));
                result.IsSuccess.Should().BeTrue(result.ErrorMessage);
                SnapshotManifest manifest = (await SnapshotPublisher.ReadJsonAsync<SnapshotManifest>(
                    result.Value.ManifestPath, default))!;
                manifest.ManifestVersion.Should().Be(2);
                manifest.MinHostVersion.Should().Be("0.4.2");
                manifest.SchemaVersion.Should().Be(AppSchemaVersion.Current);
                return new Context(database, root, result.Value.ManifestPath, manifest);
            }
            catch
            {
                await database.DisposeAsync();
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }

                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await database.DisposeAsync();
            SqliteTestCleanup.ReleasePoolsInDirectory(Root);
            Directory.Delete(Root, true);
        }
    }
}
