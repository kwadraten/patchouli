using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Mcp;
using Patchouli.Ocr;
using Patchouli.UI;

namespace Patchouli.Tests;

/// <summary>
///     MCP/CLI-facing explicit preset selection over the real host command surface: receipts
///     identify the actual preset/version, a pinned version is frozen at enqueue, unknown /
///     archived / cross-library ids and engine conflicts fail before anything is queued, and
///     <c>request_id</c> fingerprints include the preset selectors.
/// </summary>
public sealed class McpOcrPresetSelectionTests
{
    [Fact]
    public async Task Explicit_preset_and_pinned_version_are_reflected_in_receipt_and_task()
    {
        await using Context context = await Context.CreateAsync();
        OcrPreset preset = context.Preset;
        OcrPresetVersion version =
            (await context.Services.OcrPresets.GetCurrentVersionAsync(preset.PresetId)).Value;

        Result<McpOcrCommandResult> result = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, "default", "explicit-preset",
                preset.PresetId.ToString(), version.PresetVersionId.ToString()));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.PresetId.Should().Be(preset.PresetId.ToString());
        result.Value.PresetVersionId.Should().Be(version.PresetVersionId.ToString());
        result.Value.EngineId.Should().Be(OcrEngineIds.Mock);
        result.Value.Reused.Should().BeFalse();

        OcrQueueTask task = (await context.Queue.GetTaskAsync(
            OcrQueueTaskId.Parse(result.Value.TaskId))).Value;
        task.TaskKind.Should().Be(OcrQueueTaskKind.Pages);
        task.Source.Should().Be(OcrQueueSource.Mcp);
        task.PresetVersionId.Should().Be(version.PresetVersionId);
        task.PresetId.Should().Be(preset.PresetId);
    }

    [Fact]
    public async Task Preset_without_version_resolves_current_version_at_enqueue()
    {
        await using Context context = await Context.CreateAsync();
        OcrPreset preset = context.Preset;
        OcrPresetVersion version =
            (await context.Services.OcrPresets.GetCurrentVersionAsync(preset.PresetId)).Value;

        Result<McpOcrCommandResult> result = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, "default", null,
                preset.PresetId.ToString()));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.PresetVersionId.Should().Be(version.PresetVersionId.ToString());
    }

    [Fact]
    public async Task Unknown_or_malformed_preset_fails_before_enqueue()
    {
        await using Context context = await Context.CreateAsync();

        Result<McpOcrCommandResult> unknown = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, "default", null,
                OcrPresetId.New().ToString()));
        unknown.IsFailure.Should().BeTrue();
        unknown.ErrorCode.Should().Be(AppErrorCodes.NotFound);

        Result<McpOcrCommandResult> malformed = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, "default", null, "not-a-uuid"));
        malformed.IsFailure.Should().BeTrue();
        malformed.ErrorCode.Should().Be(AppErrorCodes.InvalidArgument);

        (await context.Queue.ListTasksAsync(new OcrQueueTaskFilter())).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Archived_preset_and_foreign_version_are_rejected()
    {
        await using Context context = await Context.CreateAsync();
        OcrPreset preset = context.Preset;
        OcrPresetVersion version =
            (await context.Services.OcrPresets.GetCurrentVersionAsync(preset.PresetId)).Value;

        // A version pinned under another preset can never run under this preset's id.
        OcrPreset other = (await context.Services.OcrPresets.CreatePresetAsync("Other", null,
            OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
        Result<McpOcrCommandResult> crossVersion = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, "default", null,
                other.PresetId.ToString(), version.PresetVersionId.ToString()));
        crossVersion.IsFailure.Should().BeTrue();
        crossVersion.ErrorCode.Should().Be(AppErrorCodes.Conflict);

        (await context.Services.OcrPresets.ArchivePresetAsync(preset.PresetId)).IsSuccess.Should()
            .BeTrue();
        Result<McpOcrCommandResult> archived = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, "default", null,
                preset.PresetId.ToString()));
        archived.IsFailure.Should().BeTrue();
        archived.ErrorMessage.Should().Contain("archived");
        (await context.Queue.ListTasksAsync(new OcrQueueTaskFilter())).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Conflicting_engine_selector_and_preset_version_without_preset_fail()
    {
        await using Context context = await Context.CreateAsync();
        OcrPreset preset = context.Preset;

        Result<McpOcrCommandResult> conflict = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, OcrEngineIds.MinerU, null,
                preset.PresetId.ToString()));
        conflict.IsFailure.Should().BeTrue();
        conflict.ErrorCode.Should().Be(AppErrorCodes.Conflict);

        Result<McpOcrCommandResult> orphanVersion = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, "default", null, null,
                OcrPresetVersionId.New().ToString()));
        orphanVersion.IsFailure.Should().BeTrue();
        orphanVersion.ErrorCode.Should().Be(AppErrorCodes.InvalidArgument);
        (await context.Queue.ListTasksAsync(new OcrQueueTaskFilter())).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Request_id_replay_returns_original_receipt_and_changed_preset_conflicts()
    {
        await using Context context = await Context.CreateAsync();
        OcrPreset preset = context.Preset;
        OcrPreset other = (await context.Services.OcrPresets.CreatePresetAsync("Other", null,
            OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;

        Result<McpOcrCommandResult> first = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, "default", "req-preset",
                preset.PresetId.ToString()));
        first.IsSuccess.Should().BeTrue(first.ErrorMessage);

        Result<McpOcrCommandResult> replay = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, "default", "req-preset",
                preset.PresetId.ToString()));
        replay.IsSuccess.Should().BeTrue();
        replay.Value.TaskId.Should().Be(first.Value.TaskId);
        replay.Value.Reused.Should().BeTrue();

        // Same request id with a different preset selector conflicts — never silently re-mapped.
        Result<McpOcrCommandResult> changed = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, "default", "req-preset",
                other.PresetId.ToString()));
        changed.IsFailure.Should().BeTrue();
        changed.ErrorCode.Should().Be(AppErrorCodes.Conflict);

        // UUID case/format variants fingerprint identically.
        Result<McpOcrCommandResult> caseVariant = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, "default", "req-preset",
                preset.PresetId.ToString().ToUpperInvariant()));
        caseVariant.IsSuccess.Should().BeTrue();
        caseVariant.Value.Reused.Should().BeTrue();
    }

    [Fact]
    public async Task Replay_after_preset_version_change_returns_original_receipt()
    {
        await using Context context = await Context.CreateAsync();
        OcrPreset preset = context.Preset;
        Result<McpOcrCommandResult> first = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, "default", "req-v1",
                preset.PresetId.ToString()));
        first.IsSuccess.Should().BeTrue(first.ErrorMessage);

        // A new immutable version supersedes the preset's current version between calls —
        // replaying the same request must still return the ORIGINAL accepted receipt, not
        // re-resolve against the newer version.
        (await context.Services.OcrPresets.CreatePresetVersionAsync(preset.PresetId,
            OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", true)).IsSuccess.Should().BeTrue();
        Result<McpOcrCommandResult> replay = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, "default", "req-v1",
                preset.PresetId.ToString()));
        replay.IsSuccess.Should().BeTrue();
        replay.Value.TaskId.Should().Be(first.Value.TaskId);
        replay.Value.PresetVersionId.Should().Be(first.Value.PresetVersionId);
        replay.Value.Reused.Should().BeTrue();

        // Naming the NEWER version under the same request id is a changed request → conflict.
        OcrPresetVersion newer =
            (await context.Services.OcrPresets.GetCurrentVersionAsync(preset.PresetId)).Value;
        Result<McpOcrCommandResult> changed = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, "default", "req-v1",
                preset.PresetId.ToString(), newer.PresetVersionId.ToString()));
        changed.IsFailure.Should().BeTrue();
        changed.ErrorCode.Should().Be(AppErrorCodes.Conflict);
    }

    [Fact]
    public async Task Explicit_selection_never_falls_back_to_another_same_engine_preset()
    {
        await using Context context = await Context.CreateAsync();
        // A second same-engine preset exists, but a foreign preset id still fails — the host
        // must not "helpfully" pick the mock preset instead.
        (await context.Services.OcrPresets.CreatePresetAsync("Decoy", null,
            OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).IsSuccess.Should().BeTrue();
        await using Context foreignContext = await Context.CreateAsync();
        OcrPreset foreign = (await foreignContext.Services.OcrPresets.CreatePresetAsync("Foreign", null,
            OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
        LibraryId foreignLibrary = (await foreignContext.Services.Library.GetCurrentLibraryAsync()).Value.LibraryId;
        // Import only the foreign metadata row to exercise validation of an existing foreign id;
        // library creation itself is intentionally one-time per runtime database.
        await using (Microsoft.Data.Sqlite.SqliteConnection connection =
                     context.Services.ConnectionFactory.CreateWriteConnection())
        {
            await connection.OpenAsync();
            await Dapper.SqlMapper.ExecuteAsync(connection, "pragma foreign_keys=off;");
            try
            {
                await Dapper.SqlMapper.ExecuteAsync(connection,
                    "insert into ocr_presets(preset_id,library_id,name,description,current_version_id,archived,created_at,updated_at) " +
                    "values(@Id,@Library,'Foreign',null,null,0,@Now,@Now);",
                    new
                    {
                        Id = foreign.PresetId.ToString(), Library = foreignLibrary.ToString(),
                        Now = DateTimeOffset.UtcNow.ToString("O")
                    });
            }
            finally
            {
                await Dapper.SqlMapper.ExecuteAsync(connection, "pragma foreign_keys=on;");
            }
        }

        // Re-select nothing in home library; foreign preset id resolves against the wrong library.
        Result<McpOcrCommandResult> result = await context.Services.OcrCommands.StartAsync(
            new McpOcrStartRequest(context.DocumentId, null, "default", null,
                foreign.PresetId.ToString()));
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    private sealed class Context : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string _source;
        private readonly HostOcrCompositionOptions _ocrComposition;

        private Context(string root, string source, HostServices services, IOcrQueueScheduler queue,
            DocumentInstanceId documentId, OcrPreset preset, HostOcrCompositionOptions ocrComposition)
        {
            _root = root;
            _source = source;
            Services = services;
            Queue = queue;
            DocumentId = documentId;
            Preset = preset;
            _ocrComposition = ocrComposition;
        }

        public HostServices Services { get; }
        public IOcrQueueScheduler Queue { get; }
        public DocumentInstanceId DocumentId { get; }
        public OcrPreset Preset { get; }

        public static async Task<Context> CreateAsync()
        {
            string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
                $"ocr-preset-mcp-{Guid.NewGuid():N}")).FullName;
            string source = Path.Combine(root, "source.pdf");
            File.Copy(
                TestPaths.FromRepositoryRoot("tests", "Patchouli.Tests", "Fixtures", "Pdf",
                    "real-three-page-sample.pdf"), source);
            string db = Path.Combine(root, "runtime.sqlite");
            PatchouliAppSettings settings = PatchouliAppSettings.Default();
            HostOcrCompositionOptions composition = new(new MockOcrEngine(), [new MockOcrAdapter()]);
            HostServices services = await HostServices.CreateAsync(db, settings with
                {
                    OcrEngines = new OcrEnginesAppSettings(OcrEngineIds.Mock, OcrEngineIds.Mock,
                        OcrEngineIds.Mock),
                    Runtime = settings.Runtime with
                    {
                        RuntimeDatabasePath = db, DefaultSyncRoot = Path.Combine(root, "sync"),
                        DefaultStagingRoot = Path.Combine(root, "staging"), LogDirectory = Path.Combine(root, "logs"),
                        FileSearchRoot = Path.Combine(root, "search")
                    }
                }, Path.Combine(root, "settings.json"), ocrComposition: composition,
                appPaths: new TestAppPaths(root));
            (await services.Library.CreateLibraryAsync("MCP preset selection")).IsSuccess.Should().BeTrue();
            IOcrQueueScheduler queue = (await services.GetOcrQueueAsync()).Value;
            await queue.PauseAsync(OcrPauseScope.Global);
            PdfImportResult imported =
                await services.PdfImport.ImportPdfAsync(new PdfImportRequest(source, "Three pages", null, 3));
            imported.Success.Should().BeTrue(imported.ErrorMessage);
            OcrPreset preset = (await services.OcrPresets.CreatePresetAsync("Explicit mock", null,
                OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
            return new Context(root, source, services, queue,
                DocumentInstanceId.Parse(imported.CreatedDocumentInstanceId!), preset, composition);
        }

        public async ValueTask DisposeAsync()
        {
            await Services.ShutdownAsync();
            TestTempFileCleanup.DeleteDirectoryWithRetry(_root);
        }
    }
}
