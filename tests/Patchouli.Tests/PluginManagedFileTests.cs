using System.IO.Pipelines;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Host.Plugins;
using Patchouli.Mcp;
using Patchouli.Testing;
using Xunit;

namespace Patchouli.Tests;

/// <summary>
/// Plugin host-managed files (issue #8): manifest [[files]] declaration validation, the
/// PluginManagedFileService plan/status/confirmed-download pipeline (destinations under
/// {root}/{pluginId}, per-file cross-process lock, verified atomic install), the files/ mount
/// namespace served without local paths, the own-plugin local-path/status surface, and the
/// host-controlled --patchouli-managed-files launch arguments. All transport is an in-memory
/// handler — no test ever reaches the network or starts a real process.
/// </summary>
public sealed class PluginManagedFileTests : IDisposable
{
    private const string PluginId = "kwadraten.kuzushi-classifier";

    private static readonly string TestRoot =
        Path.Combine(RepositoryTestEnvironment.TestRoot, "plugins-managed-files");

    private readonly string _root = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
    private readonly string _filesRoot;
    private readonly List<IDisposable> _cleanup = [];

    public PluginManagedFileTests()
    {
        _filesRoot = Path.Combine(_root, "models", "plugins");
    }

    public void Dispose()
    {
        foreach (IDisposable item in _cleanup)
        {
            item.Dispose();
        }

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public int Calls;
        public readonly List<string> RequestedUrls = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            RequestedUrls.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request, cancellationToken));
        }
    }

    private sealed class RecordingInteraction(
        Func<PluginManagedFilesPlan, Func<IProgress<double>?, CancellationToken, Task<Result>>,
            CancellationToken, Task<Result>> body) : IPluginManagedFileInteraction
    {
        public int Calls;
        public PluginManagedFilesPlan? LastPlan;

        public RecordingInteraction(bool confirm) : this(async (plan, download, token) =>
            confirm
                ? await download(new Progress<double>(), token)
                : Result.Failure(AppErrorCodes.ValidationFailed, "User declined the download."))
        {
        }

        public Task<Result> ConfirmAndDownloadAsync(PluginManagedFilesPlan plan,
            Func<IProgress<double>?, CancellationToken, Task<Result>> download,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            LastPlan = plan;
            return body(plan, download, cancellationToken);
        }
    }

    /// <summary>A stream whose reads only finish under cancellation — mid-download cancel coverage.</summary>
    private sealed class NeverEndingStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    private static string Sha256Of(byte[] content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    private string NewPackage(string? filesSection = null)
    {
        string directory = Path.Combine(_root, "packages", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "schemas"));
        Directory.CreateDirectory(Path.Combine(directory, ".agents"));
        File.WriteAllText(Path.Combine(directory, ".agents", "plugin-usage.md"), "# usage\n");
        File.WriteAllText(Path.Combine(directory, "schemas", "status.json"),
            """{"type":"object","required":["ok"],"properties":{"ok":{"type":"boolean"}}}""");
        File.WriteAllText(Path.Combine(directory, PluginManifestLoader.ManifestFileName),
            Manifest(filesSection));
        return directory;
    }

    private static string Manifest(string? filesSection)
    {
        return """
               id = "kwadraten.kuzushi-classifier"
               version = "0.1.0"
               schema = "patchouli.plugin/1"
               host_api = "patchouli.plugins/1"
               name = "Kuzushi Classifier"
               description = "Classifier plugin"
               readme = ".agents/plugin-usage.md"

               [runtime]
               transport = "stdio"
               command = "dotnet"
               args = ["KuzushiClassifierApp.Plugin.dll", "--serve"]

               [[vfs.nodes]]
               path = ""
               kind = "directory"
               verbs = ["find","fetch"]

               [[vfs.nodes]]
               path = "status.json"
               kind = "file"
               verbs = ["fetch"]
               media = "application/json"
               """ + filesSection;
    }

    private static string Sha256Literal => Sha256Of("payload"u8.ToArray());

    private const string ValidFiles =
        """

        [[files]]
        path = "models/layout.onnx"
        download_url = "https://example.test/layout.onnx"
        bytes = 7
        sha256 = "__SHA__"
        version = "2026.11"

        [[files]]
        path = "models/vocab.txt"
        """;

    private static string FilesToml(string path, params string[] extraFields)
    {
        return "\n[[files]]\npath = \"" + path + "\"\n" + string.Join("\n", extraFields);
    }

    private Result<PluginDefinition> LoadDefinition(string? filesSection = null)
    {
        string package = NewPackage(filesSection);
        return PluginManifestLoader.Load(
            Path.Combine(package, PluginManifestLoader.ManifestFileName));
    }

    private PluginDefinition DefinitionWithFiles(params string[] paths)
    {
        Result<PluginDefinition> loaded = LoadDefinition(string.Concat(
            paths.Select(path => FilesToml(path,
                $"download_url = \"https://example.test/{path.Replace("/", "%2F")}\""))));
        loaded.IsSuccess.Should().BeTrue(loaded.ErrorMessage);
        return loaded.Value;
    }

    private PluginManagedFileService Service(StubHandler? handler = null)
    {
        PluginManagedFileService service = new(_filesRoot,
            handler is null ? null : new HttpMessageInvoker(handler));
        _cleanup.Add(service);
        return service;
    }

    // ---------- Manifest declaration ----------

    [Fact]
    public void Manifest_without_files_declares_no_managed_files()
    {
        Result<PluginDefinition> loaded = LoadDefinition();

        loaded.IsSuccess.Should().BeTrue(loaded.ErrorMessage);
        loaded.Value.ManagedFiles.Should().BeEmpty();
    }

    [Fact]
    public void Files_section_loads_declarations_and_keeps_verbs_unchanged()
    {
        Result<PluginDefinition> loaded =
            LoadDefinition(ValidFiles.Replace("__SHA__", Sha256Literal.ToUpperInvariant(),
                StringComparison.Ordinal));

        loaded.IsSuccess.Should().BeTrue(loaded.ErrorMessage);
        PluginDefinition definition = loaded.Value;
        definition.ManagedFiles.Should().HaveCount(2);
        PluginManagedFile first = definition.ManagedFiles[0];
        first.Path.Should().Be("models/layout.onnx");
        first.DownloadUrl.Should().Be("https://example.test/layout.onnx");
        first.ExpectedBytes.Should().Be(7);
        first.Sha256.Should().Be(Sha256Literal); // normalized to lowercase hex
        first.Version.Should().Be("2026.11");
        definition.ManagedFiles[1].Path.Should().Be("models/vocab.txt");
        definition.ManagedFiles[1].DownloadUrl.Should().BeNull();
        definition.ManagedFiles[1].ExpectedBytes.Should().BeNull();
        definition.ManagedFiles[1].Sha256.Should().BeNull();
        definition.ManagedFiles[1].Version.Should().BeNull();
        // Managed files never add a registry verb.
        definition.DeclaredVerbs.Should().BeEquivalentTo("find", "fetch");
        definition.FindManagedFile("models/vocab.txt").Should().NotBeNull();
        definition.FindManagedFile("models/missing.bin").Should().BeNull();
    }

    [Fact]
    public void Files_section_rejects_bad_paths_and_duplicates()
    {
        foreach (string path in new[]
                 {
                     "../escape.bin", "a\\b.bin", "a%20b.bin", "a//b.bin", "dir/",
                     "results/{result_id}.bin", "a?b.bin", "a#b.bin", ""
                 })
        {
            Result<PluginDefinition> loaded = LoadDefinition(FilesToml(path));
            loaded.IsFailure.Should().BeTrue($"files path '{path}' must be rejected");
        }

        Result<PluginDefinition> duplicates =
            LoadDefinition(FilesToml("models/a.bin") + FilesToml("models/a.bin"));
        duplicates.IsFailure.Should().BeTrue();
        duplicates.ErrorMessage.Should().Contain("Duplicate");
    }

    [Fact]
    public void Files_section_rejects_bad_metadata_fields()
    {
        (string Field, string Expected)[] cases =
        [
            ("download_url = \"ftp://example.test/x\"", "http or https"),
            ("download_url = \"models/x.bin\"", "http or https"),
            ("download_url = \"\"", "non-empty"),
            ("download_url = 5", "non-empty"),
            ("bytes = 0", "positive integer"),
            ("bytes = -3", "positive integer"),
            ("bytes = \"7\"", "positive integer"),
            ("bytes = 1.5", "positive integer"),
            ("sha256 = \"abc\"", "64-character hex"),
            ("sha256 = \"zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz\"",
                "64-character hex"),
            ("sha256 = \"\"", "non-empty"),
            ("version = \"\"", "non-empty"),
            ("version = 7", "non-empty")
        ];
        foreach ((string field, string expected) in cases)
        {
            Result<PluginDefinition> loaded = LoadDefinition(FilesToml("models/x.bin", field));
            loaded.IsFailure.Should().BeTrue($"files field '{field}' must be rejected");
            loaded.ErrorMessage.Should().Contain(expected, $"files field '{field}'");
        }
    }

    // ---------- Service: destinations, plan, status ----------

    [Fact]
    public async Task Destinations_stay_under_the_plugin_managed_root()
    {
        await using PluginManagedFileService service = Service();
        PluginDefinition definition = DefinitionWithFiles("models/layout.onnx", "vocab.txt");

        service.ManagedRootFor(PluginId).Should().Be(
            Path.Combine(Path.GetFullPath(_filesRoot), PluginId));
        string destination = service.DestinationFor(PluginId, "models/layout.onnx");
        destination.Should().Be(Path.Combine(Path.GetFullPath(_filesRoot), PluginId, "models",
            "layout.onnx"));

        PluginManagedFilesPlan plan = await service.PlanAsync(definition);
        plan.RequiredFiles.Should().HaveCount(2);
        plan.Files.Should().HaveCount(2); // nothing downloaded yet: all missing
        string managedRoot = service.ManagedRootFor(PluginId) + Path.DirectorySeparatorChar;
        plan.Files.Select(file => file.DestinationPath).Should().OnlyContain(path =>
            path.StartsWith(managedRoot, StringComparison.Ordinal));
        plan.TotalExpectedBytes.Should().Be(0); // no bytes declared in this fixture
    }

    [Fact]
    public async Task Status_distinguishes_missing_mismatch_and_ready()
    {
        await using PluginManagedFileService service = Service();
        Result<PluginDefinition> loaded = LoadDefinition(
            FilesToml("models/a.bin", "download_url = \"https://example.test/a.bin\"",
                "bytes = 4") +
            FilesToml("models/b.bin", "download_url = \"https://example.test/b.bin\"",
                "bytes = 4", $"sha256 = \"{Sha256Of("good"u8.ToArray())}\""));
        loaded.IsSuccess.Should().BeTrue(loaded.ErrorMessage);
        PluginDefinition definition = loaded.Value;

        string root = service.ManagedRootFor(PluginId);
        Directory.CreateDirectory(Path.Combine(root, "models"));
        File.WriteAllBytes(Path.Combine(root, "models", "a.bin"), "bad!"u8.ToArray());
        File.WriteAllBytes(Path.Combine(root, "models", "b.bin"), "good"u8.ToArray());

        IReadOnlyList<PluginManagedFileStatus> status = service.DescribeStatus(definition);
        status.Should().HaveCount(2);
        PluginManagedFileStatus a = status[0];
        a.Path.Should().Be("models/a.bin");
        a.Exists.Should().BeTrue();
        a.Bytes.Should().Be(4);
        a.ExpectedBytes.Should().Be(4);
        a.Missing.Should().BeFalse();
        // a.bin declares no digest: same size verifies as ready
        a.Ready.Should().BeTrue();
        a.Mismatch.Should().BeFalse();
        PluginManagedFileStatus b = status[1];
        b.Ready.Should().BeTrue();

        File.WriteAllBytes(Path.Combine(root, "models", "b.bin"), "evil"u8.ToArray());
        status = service.DescribeStatus(definition);
        status[1].Ready.Should().BeFalse();
        status[1].Mismatch.Should().BeTrue();
        status[1].Sha256.Should().Be(Sha256Of("good"u8.ToArray())); // declared pin, never the file's bytes

        PluginManagedFilesPlan plan = await service.PlanAsync(definition);
        plan.Files.Should().ContainSingle();
        plan.Files[0].RelativePath.Should().Be("models/b.bin");
        plan.Files[0].OverwritesExisting.Should().BeTrue();
        plan.Files[0].ExistingLength.Should().Be(4);
        plan.RequiredFiles.Should().HaveCount(2);
    }

    // ---------- Ensure: confirmation boundary ----------

    [Fact]
    public async Task Missing_files_fail_actionably_without_interaction()
    {
        StubHandler handler = new((_, _) => throw new InvalidOperationException("no network"));
        await using PluginManagedFileService service = Service(handler);
        PluginDefinition definition = DefinitionWithFiles("models/a.bin");

        Result ensured = await service.EnsureAsync(definition);

        ensured.IsFailure.Should().BeTrue();
        ensured.ErrorCode.Should().Be(AppErrorCodes.NotFound);
        ensured.ErrorMessage.Should().Contain("Confirm the download");
        handler.Calls.Should().Be(0);
        File.Exists(service.DestinationFor(PluginId, "models/a.bin")).Should().BeFalse();
    }

    [Fact]
    public async Task Declined_confirmation_downloads_nothing()
    {
        StubHandler handler = new((_, _) => throw new InvalidOperationException("no network"));
        await using PluginManagedFileService service = Service(handler);
        service.Interaction = new RecordingInteraction(false);
        PluginDefinition definition = DefinitionWithFiles("models/a.bin");

        Result ensured = await service.EnsureAsync(definition);

        ensured.IsFailure.Should().BeTrue();
        ensured.ErrorMessage.Should().Contain("declined");
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Confirmed_download_installs_verified_files_and_reports_progress()
    {
        byte[] payload = "weights"u8.ToArray();
        StubHandler handler = new((request, _) =>
        {
            request.RequestUri!.ToString().Should().Be("https://example.test/models%2Flayout.onnx");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
        });
        await using PluginManagedFileService service = Service(handler);
        RecordingInteraction interaction = new(true);
        service.Interaction = interaction;
        Result<PluginDefinition> loaded = LoadDefinition(FilesToml("models/layout.onnx",
            "download_url = \"https://example.test/models%2Flayout.onnx\"",
            $"bytes = {payload.Length}",
            $"sha256 = \"{Sha256Of(payload)}\""));
        loaded.IsSuccess.Should().BeTrue(loaded.ErrorMessage);

        Result ensured = await service.EnsureAsync(loaded.Value);

        ensured.IsSuccess.Should().BeTrue(ensured.ErrorMessage);
        interaction.Calls.Should().Be(1);
        interaction.LastPlan!.Files.Should().ContainSingle();
        string destination = service.DestinationFor(PluginId, "models/layout.onnx");
        File.ReadAllBytes(destination).Should().Equal(payload);
        Directory.EnumerateFiles(Path.GetDirectoryName(destination)!, "*.part")
            .Should().BeEmpty("partials are always removed");

        // A second ensure re-verifies on disk and never asks again.
        Result again = await service.EnsureAsync(loaded.Value);
        again.IsSuccess.Should().BeTrue();
        interaction.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Mismatched_file_is_replaced_only_after_confirmed_plan()
    {
        await using PluginManagedFileService service = Service(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("fixed"u8.ToArray())
            }));
        // The declared byte pin makes the pre-existing "stale-bytes" (11 bytes) a mismatch.
        Result<PluginDefinition> loaded = LoadDefinition(FilesToml("models/a.bin",
            "download_url = \"https://example.test/a.bin\"", "bytes = 5"));
        loaded.IsSuccess.Should().BeTrue(loaded.ErrorMessage);
        PluginDefinition definition = loaded.Value;
        string destination = service.DestinationFor(PluginId, "models/a.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, "stale-bytes"u8.ToArray());

        PluginManagedFilesPlan plan = await service.PlanAsync(definition);
        plan.Files.Should().ContainSingle();
        plan.Files[0].OverwritesExisting.Should().BeTrue();
        plan.Files[0].ExistingLength.Should().Be(11);

        // The same destination without the confirmed flag is a conflict, never an overwrite.
        PluginManagedFilesPlan bare = new(PluginId, PluginId,
            [plan.Files[0] with { OverwritesExisting = false, ExistingLength = null }]);
        Result refused = await service.DownloadAsync(bare);
        refused.IsFailure.Should().BeTrue();
        refused.ErrorCode.Should().Be(AppErrorCodes.Conflict);
        File.ReadAllBytes(destination).Should().Equal("stale-bytes"u8.ToArray());

        Result installed = await service.DownloadAsync(plan);
        installed.IsSuccess.Should().BeTrue(installed.ErrorMessage);
        File.ReadAllBytes(destination).Should().Equal("fixed"u8.ToArray());
    }

    [Fact]
    public async Task Download_verify_size_and_digest_before_install()
    {
        StubHandler wrongBytes = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("wrong"u8.ToArray())
        });
        await using PluginManagedFileService service = Service(wrongBytes);
        service.Interaction = new RecordingInteraction(true);
        Result<PluginDefinition> loaded = LoadDefinition(FilesToml("models/a.bin",
            "download_url = \"https://example.test/a.bin\"",
            "bytes = 9", $"sha256 = \"{Sha256Of("expected!"u8.ToArray())}\""));
        loaded.IsSuccess.Should().BeTrue(loaded.ErrorMessage);

        Result ensured = await service.EnsureAsync(loaded.Value);

        ensured.IsFailure.Should().BeTrue();
        ensured.ErrorCode.Should().Be(AppErrorCodes.NetworkError); // the confirmed download itself failed
        File.Exists(service.DestinationFor(PluginId, "models/a.bin")).Should().BeFalse();
    }

    [Fact]
    public async Task Declaration_without_url_fails_clearly_when_download_needed()
    {
        await using PluginManagedFileService service = Service();
        service.Interaction = new RecordingInteraction(true);
        PluginDefinition definition = DefinitionWithFiles("models/a.bin");
        // Strip the URL: a declaration may legitimately ship no download location.
        PluginDefinition noUrl = definition with
        {
            ManagedFiles = [definition.ManagedFiles[0] with { DownloadUrl = null }]
        };

        Result ensured = await service.EnsureAsync(noUrl);

        ensured.IsFailure.Should().BeTrue();
        ensured.ErrorMessage.Should().Contain("download_url");
    }

    [Fact]
    public async Task Cancellation_mid_download_leaves_no_partial_or_target()
    {
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NeverEndingStream())
        });
        await using PluginManagedFileService service = Service(handler);
        PluginDefinition definition = DefinitionWithFiles("models/a.bin");
        PluginManagedFilesPlan plan = await service.PlanAsync(definition);
        using CancellationTokenSource cancellation = new();

        Task<Result> download = service.DownloadAsync(plan, cancellationToken: cancellation.Token);
        await Task.Delay(200);
        await cancellation.CancelAsync();

        Func<Task> act = () => download;
        await act.Should().ThrowAsync<OperationCanceledException>();
        string destination = service.DestinationFor(PluginId, "models/a.bin");
        File.Exists(destination).Should().BeFalse();
        Directory.Exists(Path.GetDirectoryName(destination)).Should().BeTrue();
        Directory.EnumerateFiles(Path.GetDirectoryName(destination)!, "*.part").Should().BeEmpty();
    }

    // ---------- HostPluginApi: files/ surface + own-plugin methods ----------

    private string RegisterPlugin(string directory, string? filesSection = null,
        string[]? verbs = null)
    {
        string package = NewPackage(filesSection);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "dev-plugins.json"), JsonSerializer.Serialize(new[]
        {
            new
            {
                manifest_path = Path.Combine(package, PluginManifestLoader.ManifestFileName),
                enabled = true,
                allowed_verbs = verbs ?? ["find", "fetch", "send"]
            }
        }));
        return package;
    }

    private sealed class ApiDisposer(HostPluginApi api) : IDisposable
    {
        public void Dispose()
        {
            api.DisposeAsync().AsTask().Wait();
        }
    }

    private HostPluginApi NewRegisteredApi(string? filesSection = null,
        FakePluginLauncher? launcher = null)
    {
        PluginManagedFileService service = Service();
        string registry = Path.Combine(_root, "registry", Guid.NewGuid().ToString("N"));
        RegisterPlugin(registry, filesSection);
        HostPluginApi api = new(Path.Combine(registry, "dev-plugins.json"),
            launcher, managedFiles: service);
        _cleanup.Add(new ApiDisposer(api));
        return api;
    }

    [Fact]
    public void Fetch_files_status_never_leaks_local_paths()
    {
        HostPluginApi api = NewRegisteredApi(ValidFiles.Replace("__SHA__", Sha256Literal,
            StringComparison.Ordinal));

        Result<McpPluginFetchContent?> listing = api.TryFetchStatic(PluginId, "files/");
        listing.IsSuccess.Should().BeTrue(listing.ErrorMessage);
        listing.Value.Should().NotBeNull();
        using JsonDocument document = JsonDocument.Parse(listing.Value!.Content);
        JsonElement files = document.RootElement.GetProperty("files");
        files.GetArrayLength().Should().Be(2);
        JsonElement first = files[0];
        first.GetProperty("path").GetString().Should().Be("models/layout.onnx");
        first.GetProperty("ready").GetBoolean().Should().BeFalse();
        first.GetProperty("exists").GetBoolean().Should().BeFalse();
        first.GetProperty("missing").GetBoolean().Should().BeTrue();
        first.GetProperty("mismatch").GetBoolean().Should().BeFalse();
        first.GetProperty("bytes").ValueKind.Should().Be(JsonValueKind.Null);
        first.GetProperty("expected_bytes").GetInt64().Should().Be(7);
        first.GetProperty("sha256").GetString().Should().Be(Sha256Literal);
        first.GetProperty("download_url").GetString().Should().Be("https://example.test/layout.onnx");
        first.GetProperty("version").GetString().Should().Be("2026.11");
        listing.Value.Content.Should().NotContain(_root); // no absolute path leaks

        Result<McpPluginFetchContent?> single = api.TryFetchStatic(PluginId, "files/models/layout.onnx");
        single.IsSuccess.Should().BeTrue(single.ErrorMessage);
        JsonElement status = JsonDocument.Parse(single.Value!.Content).RootElement;
        status.GetProperty("path").GetString().Should().Be("models/layout.onnx");
        status.ValueKind.Should().Be(JsonValueKind.Object);
        status.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            "path", "ready", "exists", "bytes", "sha256", "expected_bytes", "missing", "mismatch",
            "download_url", "version");

        api.TryFetchStatic(PluginId, "files").IsSuccess.Should().BeTrue();
        api.TryFetchStatic(PluginId, "files/models/nope.bin").IsFailure.Should().BeTrue();
        api.TryFetchStatic(PluginId, "files/models/").IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Root_listing_and_find_show_files_namespace_only_when_declared()
    {
        HostPluginApi api = NewRegisteredApi(ValidFiles.Replace("__SHA__", Sha256Literal,
            StringComparison.Ordinal));

        Result<McpPluginFetchContent?> root = api.TryFetchStatic(PluginId, "");
        root.IsSuccess.Should().BeTrue(root.ErrorMessage);
        using JsonDocument document = JsonDocument.Parse(root.Value!.Content);
        document.RootElement.GetProperty("entries").EnumerateArray()
            .Select(entry => entry.GetProperty("uri").GetString())
            .Should().Contain("files/");
    }

    [Fact]
    public async Task Files_namespace_find_lists_declarations()
    {
        HostPluginApi api = NewRegisteredApi(ValidFiles.Replace("__SHA__", Sha256Literal,
            StringComparison.Ordinal));

        Result<McpPluginFindPage> page = await api.FindAsync(PluginId, "files/", null, 20, null);
        page.IsSuccess.Should().BeTrue(page.ErrorMessage);
        page.Value.Entries.Select(entry => entry.Uri).Should().BeEquivalentTo(
            $"patchouli://plugin/{PluginId}/files/models/layout.onnx",
            $"patchouli://plugin/{PluginId}/files/models/vocab.txt");

        Result<McpPluginFindPage> queried = await api.FindAsync(PluginId, "files/", "q", 20, null);
        queried.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Plugin_without_declarations_has_no_files_namespace()
    {
        HostPluginApi api = NewRegisteredApi();

        api.TryFetchStatic(PluginId, "files/").IsFailure.Should().BeTrue();
        api.TryFetchStatic(PluginId, "files/x.bin").IsFailure.Should().BeTrue();

        Result<McpPluginFetchContent?> root = api.TryFetchStatic(PluginId, "");
        using JsonDocument document = JsonDocument.Parse(root.Value!.Content);
        document.RootElement.GetProperty("entries").EnumerateArray()
            .Select(entry => entry.GetProperty("uri").GetString())
            .Should().NotContain("files/");
    }

    [Fact]
    public void Local_path_is_exposed_only_for_declared_verified_files()
    {
        HostPluginApi api = NewRegisteredApi(ValidFiles.Replace("__SHA__", Sha256Literal,
            StringComparison.Ordinal));

        api.TryManagedFileLocalPath(PluginId, "models/nope.bin").IsFailure.Should().BeTrue();
        api.TryManagedFileLocalPath(PluginId, "models/layout.onnx").IsFailure
            .Should().BeTrue(); // declared but not downloaded yet
        api.TryManagedFileLocalPath("other.plugin", "models/layout.onnx").IsFailure.Should().BeTrue();

        PluginManagedFileService service = Service();
        string destination = service.DestinationFor(PluginId, "models/layout.onnx");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, "payload"u8.ToArray()); // declared bytes=7 sha256=payload

        Result<string> path = api.TryManagedFileLocalPath(PluginId, "models/layout.onnx");
        path.IsSuccess.Should().BeTrue(path.ErrorMessage);
        path.Value.Should().Be(destination);
    }

    [Fact]
    public void Managed_file_status_dto_covers_all_declarations()
    {
        HostPluginApi api = NewRegisteredApi(ValidFiles.Replace("__SHA__", Sha256Literal,
            StringComparison.Ordinal));

        Result<PluginManagedFilesStatus> status = api.TryManagedFileStatus(PluginId);
        status.IsSuccess.Should().BeTrue(status.ErrorMessage);
        status.Value.PluginId.Should().Be(PluginId);
        status.Value.Files.Should().HaveCount(2);
        status.Value.AllReady.Should().BeFalse();
        status.Value.UnreadyCount.Should().Be(2);
        status.Value.Files.Select(file => file.Path)
            .Should().Equal("models/layout.onnx", "models/vocab.txt");

        api.TryManagedFileStatus("other.plugin").IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Ensure_managed_files_runs_the_confirmed_service()
    {
        PluginManagedFileService service = Service();
        service.Interaction = new RecordingInteraction(false);
        string registry = Path.Combine(_root, "registry", Guid.NewGuid().ToString("N"));
        RegisterPlugin(registry, ValidFiles.Replace("__SHA__", Sha256Literal,
            StringComparison.Ordinal));
        HostPluginApi api = new(Path.Combine(registry, "dev-plugins.json"), managedFiles: service);
        _cleanup.Add(new ApiDisposer(api));

        Result ensured = await api.EnsureManagedFilesAsync(PluginId);
        ensured.IsFailure.Should().BeTrue(); // declined
        ensured.ErrorMessage.Should().Contain("declined");

        Result unknown = await api.EnsureManagedFilesAsync("other.plugin");
        unknown.IsFailure.Should().BeTrue();
        unknown.ErrorCode.Should().Be(AppErrorCodes.NotFound);
    }

    [Fact]
    public async Task Process_launch_injects_managed_files_arguments_first()
    {
        FakePluginLauncher launcher = new();
        HostPluginApi api = NewRegisteredApi(
            ValidFiles.Replace("__SHA__", Sha256Literal, StringComparison.Ordinal), launcher);

        // A live fetch on a plugin-owned node forces the process start.
        _ = await api.FetchAsync(PluginId, "status.json");

        launcher.Specs.Should().ContainSingle();
        PluginProcessSpec spec = launcher.Specs[0];
        spec.FileName.Should().Be("dotnet");
        spec.ManagedFilesRoot.Should().NotBeNullOrEmpty();
        spec.ManagedFiles.Select(map => map.RelativePath)
            .Should().Equal("models/layout.onnx", "models/vocab.txt");
        spec.ManagedFiles.Should().OnlyContain(map =>
            Path.IsPathFullyQualified(map.AbsolutePath) &&
            map.AbsolutePath.StartsWith(spec.ManagedFilesRoot! + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) &&
            // '/' separators are normalized to the OS separator (on Unix '/' is the separator).
            map.AbsolutePath.Contains('/') == !OperatingSystem.IsWindows());

        IReadOnlyList<string> args = spec.EffectiveArguments;
        args[0].Should().Be("--patchouli-managed-files");
        args[1].Should().Be(spec.ManagedFilesRoot);
        args[2].Should().Be("--patchouli-managed-file");
        args[3].Should().Be($"models/layout.onnx|{spec.ManagedFiles[0].AbsolutePath}");
        args[4].Should().Be("--patchouli-managed-file");
        args[5].Should().Be($"models/vocab.txt|{spec.ManagedFiles[1].AbsolutePath}");
        // The plugin's own declared args always follow the host-controlled arguments.
        args.Skip(6).Should().Equal("KuzushiClassifierApp.Plugin.dll", "--serve");
    }

    [Fact]
    public async Task Plugin_without_files_launches_with_declared_args_only()
    {
        FakePluginLauncher launcher = new();
        HostPluginApi api = NewRegisteredApi(null, launcher);

        _ = await api.FetchAsync(PluginId, "status.json");

        launcher.Specs.Should().ContainSingle();
        launcher.Specs[0].ManagedFilesRoot.Should().BeNull();
        launcher.Specs[0].EffectiveArguments.Should().Equal(
            "KuzushiClassifierApp.Plugin.dll", "--serve");
    }

    /// <summary>Captures launch specs; the fake process answers the MCP handshake over pipes.</summary>
    private sealed class FakePluginLauncher : IPluginProcessLauncher
    {
        public List<PluginProcessSpec> Specs { get; } = [];

        public IPluginChildProcess Start(PluginProcessSpec spec)
        {
            Specs.Add(spec);
            return new FakePluginProcess();
        }
    }

    private sealed class FakePluginProcess : IPluginChildProcess
    {
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _server;
        private int _hasExited;

        public FakePluginProcess()
        {
            _server = Task.Run(ServeAsync);
        }

        public Stream StandardInput => _input.Writer.AsStream();
        public Stream StandardOutput => _output.Reader.AsStream();
        public Stream StandardError => Stream.Null;
        public bool HasExited => _hasExited != 0;
        public int? ExitCode => HasExited ? 0 : null;

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            return _exited.Task.WaitAsync(cancellationToken);
        }

        private async Task ServeAsync()
        {
            try
            {
                using StreamReader reader = new(_input.Reader.AsStream(), Encoding.UTF8, false,
                    4096, true);
                await using Stream writer = _output.Writer.AsStream();
                while (await reader.ReadLineAsync(_lifetime.Token) is { } line)
                {
                    using JsonDocument request = JsonDocument.Parse(line);
                    JsonElement root = request.RootElement;
                    if (!root.TryGetProperty("id", out JsonElement id))
                    {
                        continue; // notifications are ignored
                    }

                    object result = root.GetProperty("method").GetString() == "initialize"
                        ? new { protocolVersion = "2025-06-18", capabilities = new { } }
                        : new
                        {
                            tools = PluginStdioClient.RequiredTools.Select(name => new { name }).ToArray()
                        };
                    await writer.WriteAsync(Encoding.UTF8.GetBytes(
                            JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }) + "\n"),
                        _lifetime.Token);
                    await writer.FlushAsync(_lifetime.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                await _output.Writer.CompleteAsync();
                Interlocked.Exchange(ref _hasExited, 1);
                _exited.TrySetResult();
            }
        }

        public void KillTree()
        {
            _lifetime.Cancel();
            Interlocked.Exchange(ref _hasExited, 1);
            _exited.TrySetResult();
        }

        public void Dispose()
        {
            KillTree();
            _lifetime.Dispose();
        }
    }
}
