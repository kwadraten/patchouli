using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Patchouli.Core.Actions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Core.Documents;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Composition;
using Patchouli.Host.Extensions;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Mcp;
using Patchouli.UI;

namespace Patchouli.Tests;

/// <summary>
/// Issue #28 independent tests: runs/*/control endpoint descriptions enumerate the
/// domain's internal implementations. Service-level tests drive
/// <see cref="McpCommandService.FetchAsync"/> with a stub
/// <see cref="IMcpImplementationCatalogApi"/>; host-level tests verify the real
/// <see cref="HostServices.McpImplementationCatalog"/> against the host's live
/// registries (OCR adapters, extension catalog, metadata sources) and end-to-end
/// through <see cref="HostServices.McpCommands"/>.
/// </summary>
public sealed class Issue28McpEnumerationTests
{
    // ---------- service level (stub catalog) ----------

    [Theory]
    [InlineData("ocr")]
    [InlineData("translation")]
    [InlineData("metadata")]
    public async Task Control_description_includes_implementations_from_catalog(string domain)
    {
        StubCatalog catalog = new()
        {
            Entries =
            [
                new McpImplementationEntry($"{domain}-one", "One"),
                new McpImplementationEntry($"{domain}-two", "Two", IsDefault: true)
            ]
        };
        McpCommandService commands = CreateService(catalog);

        McpFetchResult entry = await FetchControlAsync(commands, domain);

        entry.Error.Should().BeNull();
        using JsonDocument content = ParseContent(entry);
        content.RootElement.GetProperty("endpoint").GetString().Should().Be(RunEndpointCatalog.Uri(domain));
        content.RootElement.GetProperty("messages").GetArrayLength().Should().BeGreaterThan(0);
        List<string> ids = content.RootElement.GetProperty("implementations").EnumerateArray()
            .Select(element => element.GetProperty("id").GetString()).ToList()!;
        ids.Should().Equal($"{domain}-one", $"{domain}-two");
        catalog.RequestedDomains.Should().Equal(domain);
    }

    [Fact]
    public async Task Control_description_without_catalog_keeps_the_old_shape()
    {
        McpCommandService commands = CreateService(null);

        McpFetchResult entry = await FetchControlAsync(commands, "ocr");

        entry.Error.Should().BeNull();
        using JsonDocument content = ParseContent(entry);
        content.RootElement.TryGetProperty("implementations", out _).Should().BeFalse();
        content.RootElement.GetProperty("messages").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Catalog_failure_fails_the_fetch_as_unavailable()
    {
        StubCatalog catalog = new()
        {
            Entries = null,
            Failure = Result<IReadOnlyList<McpImplementationEntry>>.Failure(
                AppErrorCodes.DatabaseError, "catalog exploded")
        };
        McpCommandService commands = CreateService(catalog);

        McpCommandResult<McpFetchMeta, McpFetchResult> result = await commands.FetchAsync(
            new McpFetchRequest([RunEndpointCatalog.Uri("metadata")], null, null));

        McpFetchResult entry = result.Envelope!.Entries.Should().ContainSingle().Subject;
        entry.Content.Should().BeNull();
        McpToolError.TryGetCode(entry.Error, out McpErrorCode code).Should().BeTrue();
        code.Should().Be(McpErrorCode.Unavailable);
    }

    // ---------- host level (real registries) ----------

    [Fact]
    public async Task Host_ocr_enumeration_matches_the_adapter_registry()
    {
        await using HostContext context = await HostContext.CreateAsync();
        HostServices services = context.Services;

        Result<IReadOnlyList<McpImplementationEntry>> result =
            await services.McpImplementationCatalog.ListAsync("ocr");
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        IReadOnlyList<McpImplementationEntry> entries = result.Value;

        string configured = services.StartupSettings.OcrEngines.DocumentOcrEngine.Trim().ToLowerInvariant();
        List<string> registryIds = services.OcrAdapters.ListCapabilities()
            .Select(capability => capability.EngineId).ToList();
        entries.Select(entry => entry.Id).Should().BeEquivalentTo(registryIds.Concat(["default"]));
        McpImplementationEntry alias = entries.Single(entry => entry.Id == "default");
        alias.Description.Should().Contain(configured);
        // The configured engine (when registered) is the single is_default entry.
        string[] expectedDefaults = registryIds.Contains(configured) ? [configured] : [];
        entries.Where(entry => entry.IsDefault).Select(entry => entry.Id)
            .Should().Equal(expectedDefaults);
    }

    [Fact]
    public async Task Host_translation_enumeration_lists_bundled_extensions_without_activation()
    {
        await using HostContext context = await HostContext.CreateAsync();
        HostServices services = context.Services;

        Result<IReadOnlyList<McpImplementationEntry>> result =
            await services.McpImplementationCatalog.ListAsync("translation");
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        List<string> ids = result.Value.Select(entry => entry.Id).ToList();

        IReadOnlyList<ExtensionDescriptor> live = await services.TranslationExtensions
            .ListExtensionsByKindAsync(ExtensionKind.Translation);
        ids.Should().BeEquivalentTo(live.Select(descriptor => descriptor.Id));
        // The bundled packages are enumerated from their declared metadata; none of
        // them has been activated in this fresh host.
        ids.Should().Contain([
            "translation.deepl-free", "translation.deeplx", "translation.google-free",
            "translation.llm", "translation.microsoft-free", "translation.tencent-free"
        ]);
        string configured = services.LlmSettings.Translation.EngineId;
        string[] expectedDefaults = string.IsNullOrWhiteSpace(configured) ? [] : [configured];
        result.Value.Where(entry => entry.IsDefault).Select(entry => entry.Id)
            .Should().Equal(expectedDefaults);
    }

    [Fact]
    public async Task Host_metadata_enumeration_matches_the_source_registry_with_schemes()
    {
        await using HostContext context = await HostContext.CreateAsync();
        HostServices services = context.Services;

        Result<IReadOnlyList<McpImplementationEntry>> result =
            await services.McpImplementationCatalog.ListAsync("metadata");
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);

        result.Value.Select(entry => entry.Id).Should()
            .BeEquivalentTo(services.MetadataSources.Sources.Select(source => source.Id));
        foreach (McpImplementationEntry entry in result.Value)
        {
            MetadataSourceDefinition source = services.MetadataSources.Sources
                .Single(candidate => candidate.Id == entry.Id);
            entry.IdentifierSchemes.Should().NotBeNull()
                .And.BeEquivalentTo(source.SupportedSchemes);
            entry.Active.Should().Be(source.DefaultEnabled);
        }

        result.Value.Single(entry => entry.Id == "crossref").IdentifierSchemes.Should().Contain("doi");
    }

    [Fact]
    public async Task Host_catalog_rejects_unknown_domains()
    {
        await using HostContext context = await HostContext.CreateAsync();

        Result<IReadOnlyList<McpImplementationEntry>> result =
            await context.Services.McpImplementationCatalog.ListAsync("retrieval");

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Host_control_fetch_end_to_end_carries_implementations()
    {
        await using HostContext context = await HostContext.CreateAsync();
        HostServices services = context.Services;
        // Fetching a control description is gated on the domain's send permission
        // (pre-existing policy); grant it explicitly like a user would.
        McpServerSettings current = (await services.McpSettings.GetSettingsAsync()).Value;
        McpServerSettings granted = current with
        {
            DomainPermissions =
            [
                .. current.DomainPermissions,
                new McpDomainPermission("runs/ocr", "send", true),
                new McpDomainPermission("runs/translation", "send", true),
                new McpDomainPermission("runs/metadata", "send", true)
            ]
        };
        (await services.McpSettings.SaveSettingsAsync(granted)).IsSuccess.Should().BeTrue();

        foreach (string domain in RunEndpointCatalog.Domains)
        {
            McpCommandResult<McpFetchMeta, McpFetchResult> fetch = await services.McpCommands.FetchAsync(
                new McpFetchRequest([RunEndpointCatalog.Uri(domain)], null, null));
            McpFetchResult entry = fetch.Envelope!.Entries.Should().ContainSingle(
                $"fetching the {domain} control description should succeed").Subject;
            entry.Error.Should().BeNull();
            using JsonDocument content = ParseContent(entry);
            List<string> ids = content.RootElement.GetProperty("implementations").EnumerateArray()
                .Select(element => element.GetProperty("id").GetString()).ToList()!;
            IReadOnlyList<McpImplementationEntry> catalog =
                (await services.McpImplementationCatalog.ListAsync(domain)).Value;
            ids.Should().BeEquivalentTo(catalog.Select(item => item.Id),
                $"the {domain} fetch must enumerate exactly what the catalog lists");
        }
    }

    // ---------- harness ----------

    private static async Task<McpFetchResult> FetchControlAsync(McpCommandService commands, string domain)
    {
        McpCommandResult<McpFetchMeta, McpFetchResult> result = await commands.FetchAsync(
            new McpFetchRequest([RunEndpointCatalog.Uri(domain)], null, null));
        return result.Envelope!.Entries.Should().ContainSingle().Subject;
    }

    private static JsonDocument ParseContent(McpFetchResult entry)
    {
        string content = entry.Content!;
        return JsonDocument.Parse(content.TrimStart().StartsWith('{')
            ? content
            : McpToonCodec.DecodeToJson(content));
    }

    private static McpCommandService CreateService(StubCatalog? catalog)
    {
        (_, Stub<IMcpReadApi> read) = Stub<IMcpReadApi>.Create();
        read.Handle(nameof(IMcpReadApi.GetCurrentLibraryStateAsync), _ =>
            Task.FromResult(Result<McpLibraryStateResponse>.Success(
                new McpLibraryStateResponse("lib", "lib:99"))));
        (_, Stub<IMcpWriteApi> write) = Stub<IMcpWriteApi>.Create();
        (_, Stub<IBiblatexImportService> biblatex) = Stub<IBiblatexImportService>.Create();
        (_, Stub<IItemService> items) = Stub<IItemService>.Create();
        (_, Stub<IVersionedEvidenceReader> evidence) = Stub<IVersionedEvidenceReader>.Create();
        (_, Stub<IMcpOcrCommandApi> ocrCommands) = Stub<IMcpOcrCommandApi>.Create();
        (_, Stub<IMcpTranslationRunsApi> translationRuns) = Stub<IMcpTranslationRunsApi>.Create();
        (_, Stub<IMcpMetadataRunsApi> metadataRuns) = Stub<IMcpMetadataRunsApi>.Create();
        McpServerSettings granted = McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow) with
        {
            DomainPermissions =
            [
                new McpDomainPermission("runs/ocr", "send", true),
                new McpDomainPermission("runs/translation", "send", true),
                new McpDomainPermission("runs/metadata", "send", true)
            ]
        };
        return new McpCommandService(read.Proxy, write.Proxy, biblatex.Proxy, items.Proxy,
            evidence.Proxy, permissionSettings: _ => Task.FromResult(granted),
            ocrCommands: ocrCommands.Proxy, translationRuns: translationRuns.Proxy,
            metadataRuns: metadataRuns.Proxy, implementationCatalog: catalog);
    }

    private sealed class StubCatalog : IMcpImplementationCatalogApi
    {
        public IReadOnlyList<McpImplementationEntry>? Entries { get; set; }
        public Result<IReadOnlyList<McpImplementationEntry>>? Failure { get; set; }
        public List<string> RequestedDomains { get; } = [];

        public Task<Result<IReadOnlyList<McpImplementationEntry>>> ListAsync(string domain,
            CancellationToken cancellationToken = default)
        {
            RequestedDomains.Add(domain);
            return Task.FromResult(Failure ??
                                   Result<IReadOnlyList<McpImplementationEntry>>.Success(Entries ?? []));
        }
    }

    /// <summary>Minimal DispatchProxy stub: unhandled methods throw loudly.</summary>
    private class Stub<T> : DispatchProxy where T : class
    {
        private readonly Dictionary<string, Func<object?[], object?>> _handlers = new();

        public T Proxy => (T)(object)this;

        public void Handle(string methodName, Func<object?[], object?> handler)
        {
            _handlers[methodName] = handler;
        }

        public static (T Proxy, Stub<T> Impl) Create()
        {
            T proxy = Create<T, Stub<T>>();
            return (proxy, (Stub<T>)(object)proxy);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is not null &&
                _handlers.TryGetValue(targetMethod.Name, out Func<object?[], object?>? handler))
            {
                return handler(args ?? []);
            }

            throw new NotSupportedException(
                $"Stub method not configured: {targetMethod?.DeclaringType?.Name}.{targetMethod?.Name}");
        }
    }

    private sealed class HostContext : IAsyncDisposable
    {
        private HostContext(string tempRoot, HostServices services)
        {
            TempRoot = tempRoot;
            Services = services;
        }

        public string TempRoot { get; }
        public HostServices Services { get; }

        public static async Task<HostContext> CreateAsync()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), $"patchouli-issue28-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempRoot);
            string databasePath = Path.Combine(tempRoot, "runtime.sqlite");
            PatchouliAppSettings defaults = PatchouliAppSettings.Default();
            PatchouliAppSettings settings = defaults with
            {
                Runtime = defaults.Runtime with
                {
                    RuntimeDatabasePath = databasePath,
                    DefaultSyncRoot = Path.Combine(tempRoot, "sync"),
                    DefaultStagingRoot = Path.Combine(tempRoot, "staging"),
                    LogDirectory = Path.Combine(tempRoot, "logs")
                }
            };
            HostServices services =
                await HostServices.CreateAsync(databasePath, settings, Path.Combine(tempRoot, "appsettings.json"));
            Result<Core.Library.LibraryMetadata> create =
                await services.Library.CreateLibraryAsync("Issue 28 Test");
            create.IsSuccess.Should().BeTrue(create.ErrorMessage);
            return new HostContext(tempRoot, services);
        }

        public ValueTask DisposeAsync()
        {
            SqliteTestCleanup.ReleasePoolsInDirectory(TempRoot);
            try
            {
                Directory.Delete(TempRoot, true);
            }
            catch (IOException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }
}
