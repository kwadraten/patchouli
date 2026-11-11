using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Extensions;
using Patchouli.Host.ManagedFiles;
using Patchouli.Host.Composition;
using Patchouli.Host.Plugins;
using Patchouli.Testing;
using Patchouli.UI;
using Xunit;

namespace Patchouli.Tests;

/// <summary>
/// The shared <see cref="ManagedFileService"/> engine behind both facades: normalized plans
/// produced by the extension and the plugin surface obey the same overwrite/conflict,
/// cancellation and per-destination locking rules, and two engines serialize writes to one
/// destination through the cross-process named lock. All transport is in-memory.
/// </summary>
public sealed class ManagedFileEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(RepositoryTestEnvironment.TestRoot,
        "managed-file-engine", Guid.NewGuid().ToString("N"));

    private readonly List<IDisposable> _cleanup = [];

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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(respond(request, cancellationToken));
        }
    }

    private ExtensionManagedFileService ExtensionService(StubHandler? handler = null)
    {
        ExtensionManagedFileService service = new(Path.Combine(_root, "models"), null,
            handler is null ? null : new HttpMessageInvoker(handler));
        _cleanup.Add(service);
        return service;
    }

    private PluginManagedFileService PluginService(StubHandler? handler = null)
    {
        PluginManagedFileService service = new(Path.Combine(_root, "plugins"),
            handler is null ? null : new HttpMessageInvoker(handler));
        _cleanup.Add(service);
        return service;
    }

    private static ConfigFieldDescriptor Field(params ExtensionManagedFile[] files)
    {
        return new ConfigFieldDescriptor("model-path", "Model files", ConfigValueKind.DirectoryPath,
            false, "", "") { ManagedFiles = files };
    }

    private static ExtensionConfigSnapshot Config()
    {
        return new ExtensionConfigSnapshot(new Dictionary<string, string>(), 0);
    }

    private static PluginDefinition Definition(params PluginManagedFile[] files)
    {
        return new PluginDefinition("demo.plugin", "1.0.0", "patchouli.plugin/1",
            "patchouli.plugins/1", "Demo", "demo", "README.md", "/pkg",
            new PluginRuntimeSpec("dotnet", []), [], [], [], files);
    }

    /// <summary>
    /// Both facades produce normalized missing plans over the same declaration state, and a
    /// confirmed download installs verified bytes through the same engine pipeline.
    /// </summary>
    [Fact]
    public async Task Both_facades_plan_and_install_through_the_shared_engine()
    {
        byte[] payload = "shared-engine"u8.ToArray();
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        });
        await using ExtensionManagedFileService extensions = ExtensionService(handler);
        await using PluginManagedFileService plugins = PluginService(handler);

        ConfigFieldDescriptor field = Field(
            new ExtensionManagedFile("model.bin", "https://example.test/model.bin",
                payload.Length, Sha256Of(payload)));
        PluginDefinition definition = Definition(
            new PluginManagedFile("data/model.bin", "https://example.test/model.bin",
                payload.Length, Sha256Of(payload), null));

        ExtensionManagedFilesPlan extensionPlan =
            await extensions.PlanAsync("test.ext", [field], Config());
        PluginManagedFilesPlan pluginPlan = await plugins.PlanAsync(definition);

        extensionPlan.Files.Should().ContainSingle();
        pluginPlan.Files.Should().ContainSingle();
        ManagedFileDownload extensionNormalized =
            ExtensionManagedFileService.ToNormalized(extensionPlan.Files[0]);
        ManagedFileDownload pluginNormalized =
            PluginManagedFileService.ToNormalized(pluginPlan.Files[0]);
        extensionNormalized.OverwritesExisting.Should().Be(pluginNormalized.OverwritesExisting);
        extensionNormalized.ExpectedBytes.Should().Be(pluginNormalized.ExpectedBytes);

        (await extensions.DownloadAsync(extensionPlan)).IsSuccess.Should().BeTrue();
        (await plugins.DownloadAsync(pluginPlan)).IsSuccess.Should().BeTrue();

        File.ReadAllBytes(extensionPlan.Files[0].DestinationPath).Should().Equal(payload);
        File.ReadAllBytes(pluginPlan.Files[0].DestinationPath).Should().Equal(payload);
    }

    /// <summary>
    /// A bare (unconfirmed) plan refuses to touch an existing mismatched destination through
    /// both facades with the same Conflict vocabulary.
    /// </summary>
    [Fact]
    public async Task Unconfirmed_overwrite_refuses_through_both_facades()
    {
        await using ExtensionManagedFileService extensions = ExtensionService();
        await using PluginManagedFileService plugins = PluginService();
        ConfigFieldDescriptor field = Field(
            new ExtensionManagedFile("model.bin", "https://example.test/model.bin", 5));
        PluginDefinition definition = Definition(
            new PluginManagedFile("model.bin", "https://example.test/model.bin", 5, null, null));

        ExtensionManagedFilesPlan extensionPlan =
            await extensions.PlanAsync("test.ext", [field], Config());
        PluginManagedFilesPlan pluginPlan = await plugins.PlanAsync(definition);

        foreach (string destination in
                 new[] { extensionPlan.Files[0].DestinationPath, pluginPlan.Files[0].DestinationPath })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllBytesAsync(destination, [1, 2, 3]);
        }

        ExtensionManagedFilesPlan bareExtension = extensionPlan with
        {
            Files = extensionPlan.Files.Select(file => file with { OverwritesExisting = false })
                .ToArray()
        };
        PluginManagedFilesPlan barePlugin = pluginPlan with
        {
            Files = pluginPlan.Files.Select(file => file with { OverwritesExisting = false })
                .ToArray()
        };

        Result extensionResult = await extensions.DownloadAsync(bareExtension);
        Result pluginResult = await plugins.DownloadAsync(barePlugin);

        extensionResult.IsFailure.Should().BeTrue();
        extensionResult.ErrorCode.Should().Be(AppErrorCodes.Conflict);
        pluginResult.IsFailure.Should().BeTrue();
        pluginResult.ErrorCode.Should().Be(AppErrorCodes.Conflict);
        File.ReadAllBytes(extensionPlan.Files[0].DestinationPath).Should().Equal(1, 2, 3);
        File.ReadAllBytes(pluginPlan.Files[0].DestinationPath).Should().Equal(1, 2, 3);
    }

    /// <summary>Caller cancellation is honored identically by both facades' download path.</summary>
    [Fact]
    public async Task Cancellation_cancels_through_both_facades()
    {
        await using ExtensionManagedFileService extensions = ExtensionService();
        await using PluginManagedFileService plugins = PluginService();
        ConfigFieldDescriptor field = Field(
            new ExtensionManagedFile("model.bin", "https://example.test/model.bin"));
        PluginDefinition definition = Definition(
            new PluginManagedFile("model.bin", "https://example.test/model.bin", null, null, null));
        ExtensionManagedFilesPlan extensionPlan =
            await extensions.PlanAsync("test.ext", [field], Config());
        PluginManagedFilesPlan pluginPlan = await plugins.PlanAsync(definition);

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            extensions.DownloadAsync(extensionPlan, cancellationToken: cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            plugins.DownloadAsync(pluginPlan, cancellationToken: cancelled.Token));
    }

    /// <summary>
    /// The named per-destination lock serializes two independent engines writing the same
    /// target: the second download waits, then observes the file the first just installed —
    /// one network call total, deterministic gate, no sleeps.
    /// </summary>
    [Fact]
    public async Task Per_destination_lock_serializes_two_engines()
    {
        byte[] payload = "locked"u8.ToArray();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubHandler handler = new((_, _) =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
        });

        string destination = Path.Combine(_root, "shared", "model.bin");
        ManagedFileService firstEngine = new(new HttpMessageInvoker(handler));
        ManagedFileService secondEngine = new(new HttpMessageInvoker(handler));
        _cleanup.Add(firstEngine);
        _cleanup.Add(secondEngine);

        ManagedFilesPlan firstPlan = new("extension:test.ext", "test.ext",
        [
            new ManagedFileDownload("model.bin", "https://example.test/model.bin", destination,
                payload.Length, Sha256Of(payload))
        ]);
        ManagedFilesPlan secondPlan = new("plugin:demo", "demo",
        [
            new ManagedFileDownload("model.bin", "https://example.test/model.bin", destination,
                payload.Length, Sha256Of(payload))
        ]);

        Task<Result> first = firstEngine.DownloadAsync(firstPlan);
        Task<Result> second;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            second = secondEngine.DownloadAsync(secondPlan);
            await Task.WhenAny(second, Task.Delay(200));
            second.IsCompleted.Should().BeFalse(
                "the second download waits on the named per-destination lock");
        }
        finally
        {
            release.SetResult();
        }

        (await first).IsSuccess.Should().BeTrue();
        (await second.WaitAsync(TimeSpan.FromSeconds(30))).IsSuccess.Should().BeTrue(
            "the second engine verifies the file the first installed inside the same lock");
        handler.Calls.Should().Be(1, "the shared lock prevents a duplicate network fetch");
        File.ReadAllBytes(destination).Should().Equal(payload);
    }

    /// <summary>
    /// The sharing-violation classifier: Windows ERROR_SHARING_VIOLATION always retries; Unix
    /// errno EAGAIN (11) / EWOULDBLOCK (35) retry only on the non-Windows branch — as raw codes
    /// or HRESULT low words — while unrelated IO failures never retry. (The classifier seam is
    /// exercised on Windows; real Unix flock behavior remains untested on this host.)
    /// </summary>
    [Theory]
    [InlineData(unchecked((int)0x80070020), true, true)]
    [InlineData(unchecked((int)0x80070020), false, true)]
    [InlineData(11, false, true)]
    [InlineData(35, false, true)]
    [InlineData(unchecked((int)0x80070000) | 11, false, true)]
    [InlineData(11, true, false)]
    [InlineData(35, true, false)]
    [InlineData(13, false, false)]
    [InlineData(5, true, false)]
    [InlineData(unchecked((int)0x80070005), false, false)]
    public void Sharing_violation_classifier_retries_only_lock_contention(int hresult,
        bool windows, bool expected)
    {
        ManagedFileService.IsSharingViolationCode(hresult, windows).Should().Be(expected);
    }

    /// <summary>
    /// The persistent lock-file backend (non-Windows selection, exercised explicitly here on
    /// Windows): two independent engine instances serialize on the coordination file, a waiter
    /// honours cancellation, and disposing the release handle frees the next acquisition.
    /// </summary>
    [Fact]
    public async Task Lock_file_backend_serializes_instances_and_cancels_waiters()
    {
        string destination = Path.Combine(_root, "lock-file", "model.bin");
        ManagedFileService firstEngine = new();
        ManagedFileService secondEngine = new();
        _cleanup.Add(firstEngine);
        _cleanup.Add(secondEngine);

        IDisposable held = await ManagedFileService.AcquireFileLockAsync(destination,
            ManagedFileService.ManagedFileLockBackend.LockFile, CancellationToken.None);
        File.Exists(destination + ".patchouli.lock").Should().BeTrue(
            "the coordination file sits beside the destination and is never unlinked");
        try
        {
            using CancellationTokenSource cancelled = new();
            Task<IDisposable> waiting = ManagedFileService.AcquireFileLockAsync(destination,
                ManagedFileService.ManagedFileLockBackend.LockFile, cancelled.Token);
            await Task.WhenAny(waiting, Task.Delay(200));
            waiting.IsCompleted.Should().BeFalse("the second instance waits on the lock file");
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
        }
        finally
        {
            held.Dispose();
        }

        // The release frees the lock: the same backend acquires again without a wait.
        using IDisposable reacquired = await ManagedFileService.AcquireFileLockAsync(destination,
                ManagedFileService.ManagedFileLockBackend.LockFile, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));
        reacquired.Should().NotBeNull();
    }

    /// <summary>
    /// Two fields of one extension resolving into the same directory merge into one inventory
    /// contribution whose fresh plan covers BOTH fields — prepare and execute must download
    /// every field's missing targets, not just the first field's.
    /// </summary>
    [Fact]
    public async Task Same_directory_extension_fields_download_every_contributing_field()
    {
        byte[] layout = "layout-bytes"u8.ToArray();
        byte[] vocab = "vocab-bytes"u8.ToArray();
        StubHandler handler = new((request, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(
                request.RequestUri!.AbsolutePath.Contains("vocab") ? vocab : layout)
        });
        string shared = Path.Combine(_root, "shared-models");
        ExtensionManagedFileService extensions = new(Path.Combine(_root, "models"),
            kind => kind == "shared-kind" ? shared : null, new HttpMessageInvoker(handler));
        _cleanup.Add(extensions);
        ConfigFieldDescriptor[] fields =
        [
            new("layout-field", "Layout", ConfigValueKind.DirectoryPath,
                false, "", "")
            {
                ManagedFiles =
                [
                    new ExtensionManagedFile("layout.onnx", "https://example.test/layout.onnx",
                        layout.Length, Sha256Of(layout), DirectoryKind: "shared-kind")
                ]
            },
            new("vocab-field", "Vocabulary", ConfigValueKind.DirectoryPath,
                false, "", "")
            {
                ManagedFiles =
                [
                    new ExtensionManagedFile("vocab.txt", "https://example.test/vocab.txt",
                        vocab.Length, Sha256Of(vocab), DirectoryKind: "shared-kind")
                ]
            }
        ];
        ExtensionConfigSnapshot config = Config();
        ExtensionConfigSnapshot resolved = extensions.ResolveDefaults("test.ext", fields, config);
        IReadOnlyList<ExtensionManagedFileGroup> groups =
            await extensions.DescribeGroupsAsync("test.ext", fields, config);
        groups.Should().HaveCount(2, "each field describes a group in the shared directory");

        ManagedFileInventoryService inventory = Inventory(extensions);
        ManagedInventoryGroup merged = inventory.BuildExtensionGroups("test.ext", "Test",
            fields, config, resolved, groups).Should().ContainSingle().Subject;
        merged.Files.Select(file => file.RelativePath).Should()
            .BeEquivalentTo("layout.onnx", "vocab.txt");
        merged.Missing.Should().HaveCount(2);

        Result<ManagedInventoryDownload> prepared =
            await inventory.PrepareDownloadAsync(merged, CancellationToken.None);
        prepared.IsSuccess.Should().BeTrue();
        prepared.Value.Plan.Files.Select(file => file.RelativePath).Should()
            .BeEquivalentTo("layout.onnx", "vocab.txt");

        Result result = await prepared.Value.Download(null, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        handler.Calls.Should().Be(2, "both fields' targets download through one transport");
        File.ReadAllBytes(Path.Combine(shared, "layout.onnx")).Should().Equal(layout);
        File.ReadAllBytes(Path.Combine(shared, "vocab.txt")).Should().Equal(vocab);
    }

    /// <summary>
    /// Two owners declaring the same destination with identical URL/bytes/digest dedupe to one
    /// shown file and one download through the first owning transport — a single HTTP request.
    /// </summary>
    [Fact]
    public async Task Identical_duplicate_declarations_download_once()
    {
        byte[] payload = "dup"u8.ToArray();
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        });
        ManagedFileService firstEngine = new(new HttpMessageInvoker(handler));
        ManagedFileService secondEngine = new(new HttpMessageInvoker(handler));
        _cleanup.Add(firstEngine);
        _cleanup.Add(secondEngine);

        string destination = Path.Combine(_root, "dupes", "model.bin");
        ManagedFileDownload file = new("model.bin", "https://example.test/model.bin",
            destination, payload.Length, Sha256Of(payload));
        ManagedFileDownload identical = file with { RelativePath = "model.bin" };
        ManagedInventoryGroup group = InventoryGroup("dupes", destination,
            Contribution(firstEngine, file), Contribution(secondEngine, identical));

        ManagedFileInventoryService inventory = Inventory(ExtensionService());
        Result<ManagedInventoryDownload> prepared =
            await inventory.PrepareDownloadAsync(group, CancellationToken.None);
        prepared.IsSuccess.Should().BeTrue();
        prepared.Value.Plan.Files.Should().ContainSingle(
            "the confirmation shows the destination once");

        Result result = await prepared.Value.Download(null, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        handler.Calls.Should().Be(1, "identical duplicates download exactly once");
        File.ReadAllBytes(destination).Should().Equal(payload);
    }

    /// <summary>
    /// Two owners declaring the same destination with different requirements conflict before
    /// confirmation: no download may run and nothing is fetched.
    /// </summary>
    [Fact]
    public async Task Conflicting_duplicate_declarations_fail_before_any_download()
    {
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("x"u8.ToArray())
        });
        ManagedFileService firstEngine = new(new HttpMessageInvoker(handler));
        ManagedFileService secondEngine = new(new HttpMessageInvoker(handler));
        _cleanup.Add(firstEngine);
        _cleanup.Add(secondEngine);

        string destination = Path.Combine(_root, "conflict", "model.bin");
        ManagedInventoryGroup group = InventoryGroup("conflict", destination,
            Contribution(firstEngine,
                new ManagedFileDownload("model.bin", "https://example.test/a", destination,
                    7, Sha256Of("payload"u8.ToArray()))),
            Contribution(secondEngine,
                new ManagedFileDownload("model.bin", "https://example.test/b", destination,
                    7, Sha256Of("other!!"u8.ToArray()))));

        ManagedFileInventoryService inventory = Inventory(ExtensionService());
        Result<ManagedInventoryDownload> prepared =
            await inventory.PrepareDownloadAsync(group, CancellationToken.None);
        prepared.IsFailure.Should().BeTrue();
        prepared.ErrorCode.Should().Be(AppErrorCodes.Conflict);
        prepared.ErrorMessage.Should().Contain("model.bin",
            "the error names the conflicting declaration");
        handler.Calls.Should().Be(0, "a conflict surfaces before any network call");
    }

    /// <summary>
    /// Scalar progress aggregates across owner batches weighted by expected bytes and never
    /// rewinds at an ownership boundary: two disjoint batches end at exactly 1.
    /// </summary>
    [Fact]
    public async Task Disjoint_owner_batches_report_monotone_progress_ending_at_one()
    {
        byte[] small = "ab"u8.ToArray();
        byte[] large = "cdef"u8.ToArray();
        StubHandler handler = new((request, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(
                request.RequestUri!.AbsolutePath.Contains("large") ? large : small)
        });
        ManagedFileService firstEngine = new(new HttpMessageInvoker(handler));
        ManagedFileService secondEngine = new(new HttpMessageInvoker(handler));
        _cleanup.Add(firstEngine);
        _cleanup.Add(secondEngine);

        string directory = Path.Combine(_root, "progress");
        ManagedInventoryGroup group = InventoryGroup("progress", directory,
            Contribution(firstEngine,
                new ManagedFileDownload("small.bin", "https://example.test/small.bin",
                    Path.Combine(directory, "small.bin"), small.Length, Sha256Of(small))),
            Contribution(secondEngine,
                new ManagedFileDownload("large.bin", "https://example.test/large.bin",
                    Path.Combine(directory, "large.bin"), large.Length, Sha256Of(large))));

        ManagedFileInventoryService inventory = Inventory(ExtensionService());
        Result<ManagedInventoryDownload> prepared =
            await inventory.PrepareDownloadAsync(group, CancellationToken.None);
        prepared.IsSuccess.Should().BeTrue();

        List<double> reported = [];
        // Synchronous recorder: production invokes progress serially, Progress<T> would defer
        // to the thread pool and race the assertions.
        Result result = await prepared.Value.Download(new RecordingProgress(reported.Add),
            CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        handler.Calls.Should().Be(2);
        reported.Should().NotBeEmpty();
        reported.Should().BeInAscendingOrder("aggregate progress never rewinds at an owner boundary");
        reported.Last().Should().Be(1);
    }

    /// <summary>
    /// Two fields of one extension declaring the same destination with different pins conflict
    /// even though the merged missing set deduped to a single row.
    /// </summary>
    [Fact]
    public async Task Same_extension_conflicting_pins_on_one_target_conflict()
    {
        byte[] payload = "payload"u8.ToArray();
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        });
        string shared = Path.Combine(_root, "conflict-models");
        ExtensionManagedFileService extensions = new(Path.Combine(_root, "models"),
            kind => kind == "shared-kind" ? shared : null, new HttpMessageInvoker(handler));
        _cleanup.Add(extensions);
        ConfigFieldDescriptor[] fields =
        [
            new("a-field", "A", ConfigValueKind.DirectoryPath,
                false, "", "")
            {
                ManagedFiles =
                [
                    new ExtensionManagedFile("model.bin", "https://example.test/model-a",
                        payload.Length, Sha256Of(payload), DirectoryKind: "shared-kind")
                ]
            },
            new("b-field", "B", ConfigValueKind.DirectoryPath,
                false, "", "")
            {
                ManagedFiles =
                [
                    new ExtensionManagedFile("model.bin", "https://example.test/model-b",
                        payload.Length, Sha256Of("other!!"u8.ToArray()),
                        DirectoryKind: "shared-kind")
                ]
            }
        ];
        ExtensionConfigSnapshot config = Config();
        ExtensionConfigSnapshot resolved = extensions.ResolveDefaults("test.ext", fields, config);
        IReadOnlyList<ExtensionManagedFileGroup> groups =
            await extensions.DescribeGroupsAsync("test.ext", fields, config);
        ManagedFileInventoryService inventory = Inventory(extensions);
        ManagedInventoryGroup merged = inventory.BuildExtensionGroups("test.ext", "Test",
            fields, config, resolved, groups).Should().ContainSingle().Subject;

        Result<ManagedInventoryDownload> prepared =
            await inventory.PrepareDownloadAsync(merged, CancellationToken.None);
        prepared.IsFailure.Should().BeTrue();
        prepared.ErrorCode.Should().Be(AppErrorCodes.Conflict,
            "differing same-extension pins on one destination conflict before download");
        handler.Calls.Should().Be(0);
    }

    /// <summary>
    /// A satisfied owner's declaration is still validated: a second owner missing a file at the
    /// same destination with different requirements conflicts and writes nothing.
    /// </summary>
    [Fact]
    public async Task Ready_owner_blocks_a_conflicting_missing_owner()
    {
        byte[] payload = "payload"u8.ToArray();
        StubHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        });
        ManagedFileService firstEngine = new(new HttpMessageInvoker(handler));
        ManagedFileService secondEngine = new(new HttpMessageInvoker(handler));
        _cleanup.Add(firstEngine);
        _cleanup.Add(secondEngine);

        string destination = Path.Combine(_root, "ready-conflict", "model.bin");
        ManagedFileDownload ready = new("model.bin", "https://example.test/a", destination,
            payload.Length, Sha256Of(payload));
        ManagedFileDownload conflicting = new("model.bin", "https://example.test/b", destination,
            payload.Length, Sha256Of("other!!"u8.ToArray()));
        ManagedInventoryGroup group = InventoryGroup("ready-conflict",
            Path.GetDirectoryName(destination)!,
            Contribution(firstEngine, [], [ready]),
            Contribution(secondEngine, [conflicting], [conflicting]));

        ManagedFileInventoryService inventory = Inventory(ExtensionService());
        Result<ManagedInventoryDownload> prepared =
            await inventory.PrepareDownloadAsync(group, CancellationToken.None);
        prepared.IsFailure.Should().BeTrue();
        prepared.ErrorCode.Should().Be(AppErrorCodes.Conflict);
        handler.Calls.Should().Be(0);
        File.Exists(destination).Should().BeFalse("a conflicting download never writes");
    }

    private sealed class RecordingProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value)
        {
            report(value);
        }
    }

    private static ManagedInventoryContribution Contribution(ManagedFileService engine,
        ManagedFileDownload[] missing, ManagedFileDownload[]? required = null)
    {
        return new ManagedInventoryContribution(
            _ => Task.FromResult(new ManagedFilesPlan("test:owner", "owner", missing)
            {
                RequiredFiles = required ?? missing
            }),
            (selected, progress, token) => engine.DownloadAsync(
                new ManagedFilesPlan("test:owner", "owner", selected), progress, token));
    }

    private static ManagedInventoryContribution Contribution(ManagedFileService engine,
        params ManagedFileDownload[] files)
    {
        return Contribution(engine, missing: files);
    }

    private static ManagedInventoryGroup InventoryGroup(string name, string directory,
        params ManagedInventoryContribution[] contributions)
    {
        return new ManagedInventoryGroup(name, null, directory, [], [])
        {
            Contributions = contributions
        };
    }

    private static OcrStorageLocations Storage()
    {
        string root = Path.Combine(Path.GetTempPath(), "unused");
        return new OcrStorageLocations(root, root, root, root, root, root, root, root);
    }

    private static ManagedFileInventoryService Inventory(ExtensionManagedFileService extensions)
    {
        return new ManagedFileInventoryService(null!, extensions, Storage(), null!, null!);
    }

    private static string Sha256Of(byte[] content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }
}
