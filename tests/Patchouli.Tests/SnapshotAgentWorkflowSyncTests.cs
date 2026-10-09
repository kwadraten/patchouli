using System.IO.Compression;
using System.Text.Json.Nodes;
using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Snapshots;

namespace Patchouli.Tests;

/// <summary>
/// S5: the two independent session/workflow snapshot switches (ADR 0036 §Persistence, plan §3.4,
/// acceptance 13). Session run ownership must not transfer on import, built-in locked workflow
/// definitions must survive imports, and the volatile runs/ projection never enters a snapshot.
/// </summary>
public sealed class SnapshotAgentWorkflowSyncTests
{
    [Fact]
    public async Task Publish_without_switches_omits_session_and_workflow_payloads()
    {
        await using Ctx c = await Ctx.Create();
        c.WriteSession("session-a", "Running");
        c.WriteWorkflow("custom", false, false);

        SnapshotPublishResult pub = await c.PublishAsync();

        SnapshotManifest manifest = await c.ReadManifestAsync(pub);
        manifest.FilePayloads.Should().BeEmpty();
        Directory.Exists(Path.Combine(c.SyncRoot, "payloads")).Should().BeFalse();
    }

    [Fact]
    public async Task
        Publish_with_switches_includes_session_and_workflow_payloads_but_never_the_runs_projection()
    {
        await using Ctx c = await Ctx.Create();
        c.WriteSession("session-a", "Running");
        c.WriteWorkflow("custom", false, false);
        c.WriteFile("runs/agent/session-a/status", "volatile");

        SnapshotPublishResult pub = await c.PublishAsync(true, true);

        SnapshotManifest manifest = await c.ReadManifestAsync(pub);
        manifest.FilePayloads.Select(payload => payload.Kind).Should()
            .BeEquivalentTo([SnapshotFilePayloadKinds.AgentSessions, SnapshotFilePayloadKinds.Workflows]);

        List<string> sessionEntries = ReadZipEntries(c, manifest, SnapshotFilePayloadKinds.AgentSessions);
        sessionEntries.Should().Contain("session-a/snapshot.json");
        sessionEntries.Should().NotContain(entry => entry.StartsWith("runs/", StringComparison.Ordinal));

        List<string> workflowEntries = ReadZipEntries(c, manifest, SnapshotFilePayloadKinds.Workflows);
        workflowEntries.Should().Contain("custom.json");
        workflowEntries.Should().Contain("scripts/custom.fsx");

        pub.FilePayloads.Select(payload => payload.Kind).Should().BeEquivalentTo(
            [SnapshotFilePayloadKinds.AgentSessions, SnapshotFilePayloadKinds.Workflows]);
    }

    [Fact]
    public async Task Publish_with_only_session_switch_carries_only_session_payload()
    {
        await using Ctx c = await Ctx.Create();
        c.WriteSession("session-a", "Stopped");
        c.WriteWorkflow("custom", false, false);

        SnapshotPublishResult pub = await c.PublishAsync(true, false);

        SnapshotManifest manifest = await c.ReadManifestAsync(pub);
        manifest.FilePayloads.Select(payload => payload.Kind).Should()
            .BeEquivalentTo([SnapshotFilePayloadKinds.AgentSessions]);
    }

    [Fact]
    public async Task Import_lands_running_and_awaiting_sessions_as_stopped_so_auto_resume_skips_them()
    {
        await using Ctx c = await Ctx.Create();
        c.WriteSession("session-running", "Running");
        c.WriteSession("session-awaiting", "AwaitingEffect");
        c.WriteSession("session-finished", "Finished");
        SnapshotPublishResult pub = await c.PublishAsync(true);

        SnapshotImporter importer = new();
        string stagingRoot = Path.Combine(c.Root, "staging");
        Result<SnapshotImportResult> imported = await importer.ImportSnapshotToStagingAsync(
            new SnapshotImportRequest(pub.ManifestPath, stagingRoot));
        imported.IsSuccess.Should().BeTrue(imported.ErrorMessage);

        string payloadsRoot = Path.Combine(stagingRoot, $"{pub.SnapshotId}.payloads", "agent-sessions");
        ReadSessionStatus(Path.Combine(payloadsRoot, "session-running", "snapshot.json")).Should().Be("Stopped");
        ReadSessionStatus(Path.Combine(payloadsRoot, "session-awaiting", "snapshot.json")).Should().Be("Stopped");
        ReadSessionStatus(Path.Combine(payloadsRoot, "session-finished", "snapshot.json")).Should().Be("Finished");
    }

    [Fact]
    public async Task Import_without_payloads_creates_no_payload_staging_directory()
    {
        await using Ctx c = await Ctx.Create();
        SnapshotPublishResult pub = await c.PublishAsync();

        SnapshotImporter importer = new();
        Result<SnapshotImportResult> imported = await importer.ImportSnapshotToStagingAsync(
            new SnapshotImportRequest(pub.ManifestPath, Path.Combine(c.Root, "staging")));
        imported.IsSuccess.Should().BeTrue(imported.ErrorMessage);

        Directory.Exists(Path.Combine(c.Root, "staging", $"{pub.SnapshotId}.payloads")).Should().BeFalse();
    }

    [Fact]
    public async Task
        Workflow_payload_merge_skips_built_in_and_locked_definitions_and_keeps_local_locked_definitions()
    {
        await using Ctx c = await Ctx.Create();
        string payloadsRoot = Path.Combine(c.Root, "staged-payloads", SnapshotFilePayloadKinds.Workflows);
        Directory.CreateDirectory(Path.Combine(payloadsRoot, "scripts"));
        await File.WriteAllTextAsync(Path.Combine(payloadsRoot, "builtin.json"),
            """{"id":"builtin","locked":true,"builtIn":true}""");
        await File.WriteAllTextAsync(Path.Combine(payloadsRoot, "locked-import.json"),
            """{"id":"locked-import","locked":true,"builtIn":false}""");
        await File.WriteAllTextAsync(Path.Combine(payloadsRoot, "custom.json"),
            """{"id":"custom","locked":false,"builtIn":false}""");
        await File.WriteAllTextAsync(Path.Combine(payloadsRoot, "scripts", "custom.fsx"), "// script");

        string targetLibraryDir = Path.Combine(c.Root, "target-library");
        string targetWorkflowsDir = Path.Combine(targetLibraryDir, "workflows");
        Directory.CreateDirectory(targetWorkflowsDir);
        string targetDatabasePath = Path.Combine(targetLibraryDir, "library.sqlite");
        await File.WriteAllTextAsync(Path.Combine(targetWorkflowsDir, "locked-local.json"),
            """{"id":"locked-local","locked":true,"builtIn":false}""");
        await File.WriteAllTextAsync(Path.Combine(targetWorkflowsDir, "custom.json"),
            """{"id":"custom","locked":false,"builtIn":false,"old":true}""");

        Result<IReadOnlyList<string>> merged =
            await SnapshotFilePayloads.MergeStagedPayloadsAsync(
                Path.Combine(c.Root, "staged-payloads"), targetDatabasePath, default);

        merged.IsSuccess.Should().BeTrue(merged.ErrorMessage);
        merged.Value.Should().HaveCount(2);
        merged.Value.Should().Contain(warning => warning.Contains("'builtin'", StringComparison.Ordinal));
        merged.Value.Should().Contain(warning => warning.Contains("'locked-import'", StringComparison.Ordinal));
        File.Exists(Path.Combine(targetWorkflowsDir, "builtin.json")).Should().BeFalse();
        File.Exists(Path.Combine(targetWorkflowsDir, "locked-import.json")).Should().BeFalse();
        (await File.ReadAllTextAsync(Path.Combine(targetWorkflowsDir, "locked-local.json"))).Should()
            .Contain("locked-local");
        (await File.ReadAllTextAsync(Path.Combine(targetWorkflowsDir, "custom.json"))).Should()
            .Contain("\"custom\"").And.NotContain("\"old\"");
        File.Exists(Path.Combine(targetWorkflowsDir, "scripts", "custom.fsx")).Should().BeTrue();
    }

    [Fact]
    public async Task DiscardBranch_removes_extracted_payload_staging_directory()
    {
        await using Ctx c = await Ctx.Create();
        SnapshotPublishResult pub = await c.PublishAsync(true);

        SnapshotImporter importer = new();
        string stagingRoot = Path.Combine(c.Root, "staging");
        Result<SnapshotImportResult> imported = await importer.ImportSnapshotToStagingAsync(
            new SnapshotImportRequest(pub.ManifestPath, stagingRoot));
        imported.IsSuccess.Should().BeTrue(imported.ErrorMessage);

        string stagingDatabasePath = Path.Combine(stagingRoot, $"{pub.SnapshotId}.staging.sqlite");
        string payloadsRoot = Path.Combine(stagingRoot, $"{pub.SnapshotId}.payloads");
        Directory.Exists(payloadsRoot).Should().BeTrue();

        SnapshotBranchInspectionService branchInspection = new(importer, c.Db.ConnectionFactory, c.Library);
        SnapshotBranchInspectionInfo branch = new(
            Guid.NewGuid().ToString("D"),
            (await c.Library.GetCurrentLibraryAsync()).Value.LibraryId,
            pub.SnapshotId,
            "device",
            DateTimeOffset.UtcNow,
            pub.ManifestPath,
            stagingDatabasePath,
            true,
            []);

        Result discarded = await branchInspection.DiscardBranchAsync(branch);
        discarded.IsSuccess.Should().BeTrue(discarded.ErrorMessage);
        Directory.Exists(payloadsRoot).Should().BeFalse();
    }

    private static string ReadSessionStatus(string statePath)
    {
        JsonObject state = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
        return state["status"]!.GetValue<string>();
    }

    private static List<string> ReadZipEntries(Ctx c, SnapshotManifest manifest, string kind)
    {
        SnapshotFilePayload payload = manifest.FilePayloads.Single(filePayload => filePayload.Kind == kind);
        using ZipArchive archive = ZipFile.OpenRead(Path.Combine(c.SyncRoot, payload.FileName));
        return archive.Entries.Select(entry => entry.FullName).ToList();
    }

    private sealed class Ctx : IAsyncDisposable
    {
        private Ctx(TemporarySqliteDatabase db, string root, FixedClock clock, LibraryIdentityService library)
        {
            Db = db;
            Root = root;
            Clock = clock;
            Library = library;
            SyncRoot = Path.Combine(root, "sync");
        }

        public TemporarySqliteDatabase Db { get; }
        public string Root { get; }
        public FixedClock Clock { get; }
        public LibraryIdentityService Library { get; }
        public string SyncRoot { get; }

        private string LibraryDirectory => Path.GetDirectoryName(Db.Path)!;

        public static async Task<Ctx> Create()
        {
            TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
            string root = Directory
                .CreateDirectory(
                    Path.Combine(Path.GetTempPath(), "agent-workflow-sync-" + Guid.NewGuid().ToString("N")))
                .FullName;
            FixedClock clock = new(DateTimeOffset.UtcNow);
            await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService library = new(db.ConnectionFactory, clock);
            (await library.CreateLibraryAsync("Agent Workflow Sync Test")).IsSuccess.Should().BeTrue();
            return new Ctx(db, root, clock, library);
        }

        public async Task<SnapshotPublishResult> PublishAsync(
            bool syncAgentSessions = false,
            bool syncWorkflowDefinitions = false)
        {
            SnapshotPublisher publisher = new(Clock);
            Result<SnapshotPublishResult> result = await publisher.PublishSnapshotAsync(
                new SnapshotPublishRequest(
                    Db.Path,
                    SyncRoot,
                    "device",
                    SyncAgentSessions: syncAgentSessions,
                    SyncWorkflowDefinitions: syncWorkflowDefinitions));
            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            return result.Value;
        }

        public async Task<SnapshotManifest> ReadManifestAsync(SnapshotPublishResult pub)
        {
            SnapshotManifest? manifest =
                await SnapshotPublisher.ReadJsonAsync<SnapshotManifest>(pub.ManifestPath, default);
            manifest.Should().NotBeNull();
            return manifest!;
        }

        public void WriteSession(string sessionId, string status)
        {
            string directory = Path.Combine(LibraryDirectory, "agent-sessions", sessionId);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "snapshot.json"),
                $$"""{"sessionId":"{{sessionId}}","status":"{{status}}","eventSeq":0,"historyCount":0,"updatedAt":"2026-01-01T00:00:00Z"}""");
            File.WriteAllText(Path.Combine(directory, "events.jsonl"), string.Empty);
        }

        public void WriteWorkflow(string id, bool builtIn, bool locked)
        {
            string directory = Path.Combine(LibraryDirectory, "workflows", "scripts");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(LibraryDirectory, "workflows", id + ".json"),
                $$"""{"id":"{{id}}","locked":{{locked.ToString().ToLowerInvariant()}},"builtIn":{{builtIn.ToString().ToLowerInvariant()}}}""");
            File.WriteAllText(Path.Combine(directory, id + ".fsx"), "// " + id);
        }

        public void WriteFile(string relativePath, string content)
        {
            string path = Path.Combine(LibraryDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public async ValueTask DisposeAsync()
        {
            SqliteTestCleanup.ReleasePoolsInDirectory(Root);
            SqliteTestCleanup.ReleasePoolsInDirectory(Path.GetDirectoryName(Db.Path)!);
            await Db.DisposeAsync();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
    }
}
