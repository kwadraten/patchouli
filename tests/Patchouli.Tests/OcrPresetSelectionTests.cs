using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Host;
using Patchouli.Host.Composition;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Ocr;
using Patchouli.UI;

namespace Patchouli.Tests;

/// <summary>
///     Library-scoped preset selection coverage: per-library/per-scope persistence, explicit
///     selection error taxonomy (missing/archived/cross-library — never same-engine fallback),
///     immutable version history on save, and registry-driven defaults.
/// </summary>
public sealed class OcrPresetSelectionTests
{
    private static readonly OcrEngineDefinition MockDefinition = new(
        OcrEngineIds.Mock, "Mock OCR", "mock", OcrModelIds.MockBasic, ["page_image"],
        DefaultConfig: new Dictionary<string, string> { ["latency"] = "0" });

    private sealed class TestOcrEngineCatalog(IReadOnlyList<OcrEngineDefinition> definitions)
        : IOcrEngineCatalog
    {
        public IReadOnlyList<OcrEngineDefinition> ListDefinitions()
        {
            return definitions;
        }

        public Task<Result<OcrEngineDefinition>> GetDefinitionAsync(string engineId,
            CancellationToken cancellationToken = default)
        {
            OcrEngineDefinition? match = definitions.FirstOrDefault(d => d.EngineId == engineId);
            return Task.FromResult(match is null
                ? Result<OcrEngineDefinition>.Failure(AppErrorCodes.NotFound,
                    $"OCR engine '{engineId}' is not registered.")
                : Result<OcrEngineDefinition>.Success(match));
        }
    }

    private static OcrPresetDefaults CreateDefaults(HostServices services,
        IReadOnlyList<OcrEngineDefinition>? definitions = null)
    {
        IOcrEngineCatalog? catalog = definitions is null ? null : new TestOcrEngineCatalog(definitions);
        return new OcrPresetDefaults(services.OcrPresets, services.OcrStorage,
            () => services.LlmSettings, catalog);
    }

    [Fact]
    public async Task Two_presets_on_one_engine_keep_independent_versions_and_config()
    {
        await using Context context = await Context.CreateAsync();
        HostServices services = context.Services;
        string preciseJson = OcrPresetConfiguration.WriteEnvelopeJson(null, 1,
            new Dictionary<string, string> { ["det-threshold"] = "0.9" }, null, 1, null);
        string fastJson = OcrPresetConfiguration.WriteEnvelopeJson(null, 1,
            new Dictionary<string, string> { ["det-threshold"] = "0.1" }, null, 1, null);
        OcrPreset precise = (await services.OcrPresets.CreatePresetAsync("Precise", null,
            OcrEngineIds.Mock, OcrModelIds.MockBasic, null, preciseJson, false)).Value;
        OcrPreset fast = (await services.OcrPresets.CreatePresetAsync("Fast", null,
            OcrEngineIds.Mock, OcrModelIds.MockBasic, null, fastJson, false)).Value;

        OcrPresetVersion preciseVersion =
            (await services.OcrPresets.GetCurrentVersionAsync(precise.PresetId)).Value;
        OcrPresetVersion fastVersion =
            (await services.OcrPresets.GetCurrentVersionAsync(fast.PresetId)).Value;
        OcrPresetConfiguration.ResolveImageConfig(preciseVersion, MockDefinition)
            .Values["det-threshold"].Should().Be("0.9");
        OcrPresetConfiguration.ResolveImageConfig(fastVersion, MockDefinition)
            .Values["det-threshold"].Should().Be("0.1");

        // Explicitly resolving one preset must never substitute the other same-engine preset.
        (await services.OcrPresets.ResolveSelectionAsync(fast.PresetId)).Value.PresetId.Should()
            .Be(fast.PresetId);
    }

    [Fact]
    public async Task Editing_a_preset_creates_a_new_version_and_history_is_immutable()
    {
        await using Context context = await Context.CreateAsync();
        HostServices services = context.Services;
        OcrPreset preset = (await services.OcrPresets.CreatePresetAsync("Preset", null,
            OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
        OcrPresetVersion v1 = (await services.OcrPresets.GetCurrentVersionAsync(preset.PresetId)).Value;

        OcrPresetVersion v2 = (await services.OcrPresets.CreatePresetVersionAsync(preset.PresetId,
            OcrEngineIds.Mock, OcrModelIds.MockBasic, null,
            OcrPresetConfiguration.WriteEnvelopeJson(null, 1,
                new Dictionary<string, string> { ["det-threshold"] = "0.7" }, null, 1, null),
            true)).Value;
        v2.PresetVersionId.Should().NotBe(v1.PresetVersionId);
        // The queued/history version still resolves to the frozen original payload.
        (await services.OcrPresets.GetPresetVersionAsync(v1.PresetVersionId)).Value.ParametersJson
            .Should().Be("{}");
        (await services.OcrPresets.ListVersionsAsync(preset.PresetId)).Value.Should().HaveCount(2);
        (await services.OcrPresets.GetCurrentVersionAsync(preset.PresetId)).Value.PresetVersionId
            .Should().Be(v2.PresetVersionId);

        (await services.OcrPresets.UpdatePresetAsync(preset.PresetId, "Renamed", "desc"))
            .Value.Name.Should().Be("Renamed");
    }

    [Fact]
    public async Task Explicit_selection_rejects_missing_archived_and_cross_library_ids()
    {
        await using Context context = await Context.CreateAsync();
        HostServices services = context.Services;
        OcrPreset preset = (await services.OcrPresets.CreatePresetAsync("Scoped", null,
            OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;

        (await services.OcrPresets.ResolveSelectionAsync(OcrPresetId.New())).ErrorCode.Should()
            .Be(AppErrorCodes.NotFound);

        (await services.OcrPresets.ArchivePresetAsync(preset.PresetId)).IsSuccess.Should().BeTrue();
        Result<OcrPreset> archived = await services.OcrPresets.ResolveSelectionAsync(preset.PresetId);
        archived.IsFailure.Should().BeTrue();
        archived.ErrorMessage.Should().Contain("archived");

        // A preset row owned by another library must never resolve here — one runtime
        // database holds a single library identity, so the foreign-owned row is seeded
        // directly with referential checks off.
        OcrPresetId foreignPresetId = OcrPresetId.New();
        await InsertForeignPresetRowAsync(services, foreignPresetId, LibraryId.New());
        Result<OcrPreset> foreign =
            await services.OcrPresets.ResolveSelectionAsync(foreignPresetId);
        foreign.IsFailure.Should().BeTrue();
        foreign.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
        (await services.OcrPresets.ListPresetsAsync()).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Selections_persist_per_scope_and_are_isolated_per_library()
    {
        await using Context context = await Context.CreateAsync();
        HostServices services = context.Services;
        OcrPreset preset = (await services.OcrPresets.CreatePresetAsync("Scoped", null,
            OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
        OcrPresetSelectionService selection = new(
            new OcrPresetSelectionStore(context.SettingsPath), services.OcrPresets, services.Library);

        (await selection.SaveSelectionAsync(OcrScope.Document, preset.PresetId)).IsSuccess.Should()
            .BeTrue();
        (await selection.SaveSelectionAsync(OcrScope.Region, preset.PresetId)).IsSuccess.Should()
            .BeTrue();
        (await selection.ResolveStoredSelectionAsync(OcrScope.Document)).Value.Should()
            .Be(preset.PresetId);
        (await selection.ResolveStoredSelectionAsync(OcrScope.Region)).Value.Should()
            .Be(preset.PresetId);
        (await selection.ResolveStoredSelectionAsync(OcrScope.Page)).Value.Should().BeNull();

        // Atomic multi-scope save preserves the untouched scopes.
        (await selection.SaveSelectionsAsync(new Dictionary<OcrScope, OcrPresetId?>
        {
            [OcrScope.Page] = preset.PresetId, [OcrScope.Region] = null
        })).IsSuccess.Should().BeTrue();
        (await selection.ResolveStoredSelectionAsync(OcrScope.Document)).Value.Should()
            .Be(preset.PresetId);
        (await selection.ResolveStoredSelectionAsync(OcrScope.Region)).Value.Should().BeNull();

        // The store is keyed by library id: a second library sees none of this library's
        // selections, and a preset owned by another library cannot be stored as its choice.
        ForeignLibraryIdentity foreignLibrary = new(LibraryId.New());
        OcrPresetService foreignPresets =
            new(services.ConnectionFactory, foreignLibrary, services.Clock);
        OcrPresetSelectionService foreignSelection = new(
            new OcrPresetSelectionStore(context.SettingsPath), foreignPresets, foreignLibrary);
        (await foreignSelection.ResolveStoredSelectionAsync(OcrScope.Document)).Value.Should()
            .BeNull();
        (await foreignSelection.SaveSelectionAsync(OcrScope.Document, preset.PresetId))
            .ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Stored_invalid_selection_fails_instead_of_silent_fallback()
    {
        await using Context context = await Context.CreateAsync();
        HostServices services = context.Services;
        OcrPresetSelectionStore store = new(context.SettingsPath);
        (await store.WriteAsync((await services.Library.GetCurrentLibraryAsync()).Value.LibraryId,
            new OcrPresetSelections("not-a-uuid"))).IsSuccess.Should().BeTrue();
        OcrPresetSelectionService selection = new(store, services.OcrPresets, services.Library);

        Result<OcrPresetId> resolved = await selection.ResolveForScopeAsync(OcrScope.Document,
            _ => Task.FromResult(Result<OcrPresetId>.Success(OcrPresetId.New())));
        resolved.IsFailure.Should().BeTrue();
        resolved.ErrorMessage.Should().Contain("not-a-uuid");
    }

    [Fact]
    public async Task Defaults_come_from_registry_metadata_and_missing_engines_fail()
    {
        await using Context context = await Context.CreateAsync();
        HostServices services = context.Services;
        OcrPresetDefaults defaults = CreateDefaults(services, [MockDefinition]);

        Result<OcrPresetId> created = await defaults.EnsurePresetAsync(OcrEngineIds.Mock);
        created.IsSuccess.Should().BeTrue(created.ErrorMessage);
        OcrPresetVersion version =
            (await services.OcrPresets.GetCurrentVersionAsync(created.Value)).Value;
        version.ModelId.Should().Be(OcrModelIds.MockBasic);
        OcrPresetConfiguration.TryRead(version).Value.IsValid.Should().BeTrue();

        (await defaults.EnsurePresetAsync(OcrEngineIds.Mock)).Value.Should().Be(created.Value);
        (await defaults.EnsurePresetAsync("unregistered")).ErrorCode.Should().Be(AppErrorCodes.NotFound);
    }

    [Fact]
    public async Task Missing_catalog_fails_explicitly_without_a_preset_row()
    {
        await using Context context = await Context.CreateAsync();
        HostServices services = context.Services;
        Result<OcrPresetId> result =
            await CreateDefaults(services).EnsurePresetAsync(OcrEngineIds.Mock);
        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.NotFound);
        result.ErrorMessage.Should().Contain("catalog");
        (await services.OcrPresets.ListPresetsAsync()).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Region_scope_persists_independently_of_page_scope()
    {
        await using Context context = await Context.CreateAsync();
        HostServices services = context.Services;
        OcrPreset precise = (await services.OcrPresets.CreatePresetAsync("Precise", null,
            OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
        OcrPreset fast = (await services.OcrPresets.CreatePresetAsync("Fast", null,
            OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
        OcrPresetSelectionService selection = new(
            new OcrPresetSelectionStore(context.SettingsPath), services.OcrPresets, services.Library);

        (await selection.SaveSelectionsAsync(new Dictionary<OcrScope, OcrPresetId?>
        {
            [OcrScope.Page] = fast.PresetId,
            [OcrScope.Region] = precise.PresetId
        })).IsSuccess.Should().BeTrue();

        (await selection.ResolveStoredSelectionAsync(OcrScope.Region)).Value.Should()
            .Be(precise.PresetId);
        (await selection.ResolveStoredSelectionAsync(OcrScope.Page)).Value.Should()
            .Be(fast.PresetId);
        Result<OcrPresetSelections> raw = await selection.ReadStoredSelectionsAsync();
        raw.Value.RegionPresetId.Should().Be(precise.PresetId.ToString());
    }

    [Fact]
    public async Task Parallel_store_instances_do_not_lose_each_others_scope_writes()
    {
        await using Context context = await Context.CreateAsync();
        HostServices services = context.Services;
        OcrPreset preset = (await services.OcrPresets.CreatePresetAsync("Scoped", null,
            OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
        OcrPreset second = (await services.OcrPresets.CreatePresetAsync("Second", null,
            OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
        // Distinct store instances over the same file, as every view-model creates its own —
        // the shared canonical-path gate must serialize the read-modify-write cycles so a
        // parallel region-scope save cannot drop a concurrent document-scope save.
        OcrPresetSelectionService first = new(
            new OcrPresetSelectionStore(context.SettingsPath), services.OcrPresets, services.Library);
        OcrPresetSelectionService secondService = new(
            new OcrPresetSelectionStore(context.SettingsPath), services.OcrPresets, services.Library);

        await Task.WhenAll(
            first.SaveSelectionsAsync(new Dictionary<OcrScope, OcrPresetId?>
            {
                [OcrScope.Document] = preset.PresetId, [OcrScope.Page] = preset.PresetId
            }),
            secondService.SaveSelectionsAsync(new Dictionary<OcrScope, OcrPresetId?>
            {
                [OcrScope.Region] = second.PresetId
            }));

        OcrPresetSelections merged =
            (await new OcrPresetSelectionStore(context.SettingsPath).ReadAsync(
                (await services.Library.GetCurrentLibraryAsync()).Value.LibraryId)).Value;
        merged.DocumentPresetId.Should().Be(preset.PresetId.ToString());
        merged.PagePresetId.Should().Be(preset.PresetId.ToString());
        merged.RegionPresetId.Should().Be(second.PresetId.ToString());
    }

    [Fact]
    public async Task Stale_stored_id_survives_unrelated_scope_saves()
    {
        await using Context context = await Context.CreateAsync();
        HostServices services = context.Services;
        OcrPreset preset = (await services.OcrPresets.CreatePresetAsync("Scoped", null,
            OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
        OcrPresetSelectionService selection = new(
            new OcrPresetSelectionStore(context.SettingsPath), services.OcrPresets, services.Library);

        (await selection.SaveSelectionAsync(OcrScope.Document, preset.PresetId)).IsSuccess
            .Should().BeTrue();
        (await services.OcrPresets.ArchivePresetAsync(preset.PresetId)).IsSuccess.Should().BeTrue();

        // A save touching only another scope must leave the stale document choice stored —
        // the settings VM simply excludes unavailable scopes from the write dictionary.
        OcrPreset live = (await services.OcrPresets.CreatePresetAsync("Live", null,
            OcrEngineIds.Mock, OcrModelIds.MockBasic, null, "{}", false)).Value;
        (await selection.SaveSelectionsAsync(new Dictionary<OcrScope, OcrPresetId?>
        {
            [OcrScope.Region] = live.PresetId
        })).IsSuccess.Should().BeTrue();
        (await selection.ReadStoredSelectionsAsync()).Value.DocumentPresetId.Should()
            .Be(preset.PresetId.ToString());
        (await selection.ResolveStoredSelectionAsync(OcrScope.Document)).IsFailure.Should().BeTrue();
    }

    /// <summary>
    ///     Seeds an ocr_presets row stamped with another library's id. The library_metadata
    ///     parent row is deliberately absent — one identity per database is the real contract,
    ///     so the insert runs with foreign keys off for exactly this row.
    /// </summary>
    private static async Task InsertForeignPresetRowAsync(HostServices services,
        OcrPresetId presetId, LibraryId libraryId)
    {
        await using SqliteConnection connection = services.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("pragma foreign_keys = off;");
        await connection.ExecuteAsync(
            "insert into ocr_presets (preset_id, library_id, name, description, current_version_id, archived, created_at, updated_at) " +
            "values (@PresetId, @LibraryId, 'Foreign', null, null, 0, @Now, @Now);",
            new
            {
                PresetId = presetId.ToString(), LibraryId = libraryId.ToString(),
                Now = DateTimeOffset.UtcNow.ToString("O")
            });
    }

    /// <summary>
    ///     Reports a fixed foreign <see cref="LibraryId" /> as the current library so preset and
    ///     selection services sharing this runtime database exercise the cross-library branch —
    ///     the real identity service binds one library per database by design.
    /// </summary>
    private sealed class ForeignLibraryIdentity(LibraryId currentLibraryId)
        : ILibraryIdentityService
    {
        public LibraryId CurrentLibraryId { get; } = currentLibraryId;
        public Task<Result<LibraryMetadata>> CreateLibraryAsync(string displayName,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<LibraryMetadata>.Success(Metadata()));
        }

        public Task<Result<LibraryMetadata>> GetCurrentLibraryAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<LibraryMetadata>.Success(Metadata()));
        }

        public Task<Result<LibraryMetadata>> RenameLibraryAsync(string newDisplayName,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<LibraryMetadata>.Success(Metadata()));
        }

        public Task<Result> ValidateLibraryIdAsync(LibraryId expectedLibraryId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(expectedLibraryId == CurrentLibraryId
                ? Result.Success()
                : Result.Failure(AppErrorCodes.ValidationFailed, "Library identity mismatch."));
        }

        private LibraryMetadata Metadata()
        {
            return new LibraryMetadata(CurrentLibraryId, "Foreign library", 1,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        }
    }

    private sealed class Context(string root, string settingsPath, HostServices services)
        : IAsyncDisposable
    {
        public HostServices Services { get; } = services;
        public string SettingsPath { get; } = settingsPath;

        public static async Task<Context> CreateAsync()
        {
            string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
                $"patchouli-ocr-selection-{Guid.NewGuid():N}")).FullName;
            string db = Path.Combine(root, "runtime.sqlite");
            string settingsPath = Path.Combine(root, "settings.json");
            PatchouliAppSettings settings = PatchouliAppSettings.Default();
            HostServices services = await HostServices.CreateAsync(db, settings with
            {
                Runtime = settings.Runtime with
                {
                    RuntimeDatabasePath = db,
                    DefaultSyncRoot = Path.Combine(root, "sync"),
                    DefaultStagingRoot = Path.Combine(root, "staging"),
                    LogDirectory = Path.Combine(root, "logs")
                }
            }, settingsPath);
            (await services.Library.CreateLibraryAsync("OCR preset selection")).IsSuccess.Should().BeTrue();
            return new Context(root, settingsPath, services);
        }

        public async ValueTask DisposeAsync()
        {
            await Services.ShutdownAsync();
            TestTempFileCleanup.DeleteDirectoryForcingExtensionUnload(root);
        }
    }
}
