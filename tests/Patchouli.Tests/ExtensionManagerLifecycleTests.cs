using System.Collections.Concurrent;
using FluentAssertions;
using Patchouli.Extensions;
using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Bibliography.MetadataLookup;

namespace Patchouli.Tests;

public sealed class ExtensionManagerLifecycleTests
{
    [Fact]
    [Trait("Category", "fcs")]
    public async Task Failed_candidate_keeps_the_current_generation_active()
    {
        MemoryExtensionFileStore store = new();
        CountingGenerationLoader loader = new();
        await using IExtensionManager manager = CreateRealCheckerStateMachineManager(store, loader);
        ExtensionPackageSnapshot active = Package("let marker = 1");
        ExtensionPackageSnapshot invalid = Package("let marker: int = \"not an integer\"");

        ExtensionCheckResult activated = await manager.ActivateAsync("test.extension", active, CancellationToken.None);
        ExtensionCheckResult rejected = await manager.ActivateAsync("test.extension", invalid, CancellationToken.None);

        activated.Succeeded.Should().BeTrue();
        rejected.Succeeded.Should().BeFalse();
        await using IExtensionLease lease = (await manager.AcquireAsync("test.extension", CancellationToken.None))!;
        lease.Fingerprint.Should().Be(active.Fingerprint);
        lease.Translation.Descriptor.Id.Should().Be("test.extension");
    }

    [Fact]
    public async Task Concurrent_candidate_activations_leave_one_complete_generation_active()
    {
        MemoryExtensionFileStore store = new();
        CountingGenerationLoader loader = new();
        RecordingSourceChecker checker = new();
        await using IExtensionManager manager = CreateStateMachineManager(store, loader, checker);
        ExtensionPackageSnapshot first = Package("let marker = 1");
        ExtensionPackageSnapshot second = Package("let marker = 2");

        ExtensionCheckResult[] results = await Task.WhenAll(
            manager.ActivateAsync(first.Id, first, CancellationToken.None),
            manager.ActivateAsync(second.Id, second, CancellationToken.None));

        results.Should().OnlyContain(result => result.Succeeded);
        await using IExtensionLease active = (await manager.AcquireAsync(first.Id, CancellationToken.None))!;
        active.Fingerprint.Should().BeOneOf(first.Fingerprint, second.Fingerprint);
        active.Translation.Descriptor.Id.Should().Be(first.Id);
        loader.Generations.Should().HaveCount(2);
        loader.Generations.Count(generation => generation.DisposeCount == 0).Should().Be(1);
        checker.Calls.Select(call => call.Fingerprint).Should()
            .BeEquivalentTo([first.Fingerprint, second.Fingerprint]);
    }

    [Fact]
    [Trait("Category", "compiled-extension")]
    public async Task Active_lease_remains_pinned_to_its_generation_after_new_version_activation()
    {
        string runtimeDirectory = Path.Combine(Path.GetTempPath(), $"patchouli-compiled-lease-{Guid.NewGuid():N}");
        try
        {
            await using IExtensionManager manager = new ExtensionManager(runtimeDirectory,
                TestExtensionResources.Unavailable(), new FileExtensionStore(runtimeDirectory),
                new CompiledExtensionGenerationLoader(runtimeDirectory));
            ExtensionPackageSnapshot first = Package("let marker = 1", "1.0.0");
            ExtensionPackageSnapshot replacement = Package("let marker = 2", "2.0.0");

            (await manager.ActivateAsync(first.Id, first, CancellationToken.None)).Succeeded.Should().BeTrue();
            IExtensionLease lease = (await manager.AcquireAsync(first.Id, CancellationToken.None))!;
            string originalFingerprint = lease.Fingerprint;
            lease.Descriptor.Version.Should().Be("1.0.0");

            (await manager.ActivateAsync(replacement.Id, replacement, CancellationToken.None)).Succeeded.Should()
                .BeTrue();
            lease.Fingerprint.Should().Be(originalFingerprint);
            lease.Descriptor.Version.Should().Be("1.0.0");

            await lease.DisposeAsync();

            await using IExtensionLease current = (await manager.AcquireAsync(first.Id, CancellationToken.None))!;
            current.Fingerprint.Should().Be(replacement.Fingerprint);
            current.Descriptor.Version.Should().Be("2.0.0");
        }
        finally
        {
            if (Directory.Exists(runtimeDirectory))
            {
                Directory.Delete(runtimeDirectory, true);
            }
        }
    }

    [Fact]
    [Trait("Category", "compiled-extension")]
    public async Task Initialization_failure_keeps_the_previous_generation_active()
    {
        string runtimeDirectory = Path.Combine(Path.GetTempPath(), $"patchouli-compiled-init-{Guid.NewGuid():N}");
        try
        {
            await using IExtensionManager manager = new ExtensionManager(runtimeDirectory,
                TestExtensionResources.Unavailable(), new FileExtensionStore(runtimeDirectory),
                new CompiledExtensionGenerationLoader(runtimeDirectory));
            ExtensionPackageSnapshot active = Package("let marker = 1");
            (await manager.ActivateAsync(active.Id, active, CancellationToken.None)).Succeeded.Should().BeTrue();
            await using IExtensionLease previousLease =
                (await manager.AcquireAsync(active.Id, CancellationToken.None))!;
            ExtensionPackageSnapshot candidate = Package("failwith \"initialization failed\"");

            ExtensionCheckResult check = await manager.CheckAsync(candidate, CancellationToken.None);
            ExtensionCheckResult activation = await manager.ActivateAsync(candidate.Id, candidate,
                CancellationToken.None);

            check.Succeeded.Should().BeTrue(string.Join(Environment.NewLine,
                check.Diagnostics.Select(issue => issue.Message)));
            activation.Succeeded.Should().BeFalse();
            activation.Diagnostics.Should().Contain(issue => issue.Code.StartsWith("EXT", StringComparison.Ordinal));
            previousLease.Fingerprint.Should().Be(active.Fingerprint);
            await using IExtensionLease currentLease = (await manager.AcquireAsync(active.Id,
                CancellationToken.None))!;
            currentLease.Fingerprint.Should().Be(active.Fingerprint);
        }
        finally
        {
            if (Directory.Exists(runtimeDirectory))
            {
                Directory.Delete(runtimeDirectory, true);
            }
        }
    }

    [Fact]
    [Trait("Category", "fcs")]
    public async Task Built_in_crossref_package_passes_the_real_compiler_checker()
    {
        string runtimeDirectory = Path.Combine(Path.GetTempPath(), $"patchouli-crossref-fsi-{Guid.NewGuid():N}");
        try
        {
            await using IExtensionManager manager = new ExtensionManager(runtimeDirectory,
                TestExtensionResources.Unavailable(), new FileExtensionStore(runtimeDirectory),
                new CompiledExtensionGenerationLoader(runtimeDirectory));
            ExtensionPackageSnapshot package =
                BuiltInMetadataExtensions.Packages.Single(item => item.Id == "metadata.crossref");

            ExtensionCheckResult check = await manager.CheckAsync(package, CancellationToken.None);

            check.Succeeded.Should().BeTrue(string.Join(Environment.NewLine,
                check.Diagnostics.Select(issue =>
                    $"{issue.Code} {issue.Location.FileName}:{issue.Location.Line}:{issue.Location.Column}: {issue.Message}")));
        }
        finally
        {
            if (Directory.Exists(runtimeDirectory))
            {
                Directory.Delete(runtimeDirectory, true);
            }
        }
    }

    [Fact]
    public async Task Dispose_waits_for_started_source_checks_and_rejects_new_checks()
    {
        string runtimeDirectory = Path.Combine(Path.GetTempPath(),
            $"patchouli-extension-check-drain-{Guid.NewGuid():N}");
        BlockingSourceChecker checker = new();
        IExtensionManager manager = new ExtensionManager(runtimeDirectory, TestExtensionResources.Unavailable(),
            new FileExtensionStore(runtimeDirectory), new CompiledExtensionGenerationLoader(runtimeDirectory), checker);

        try
        {
            Task<ExtensionCheckResult> startedCheck = manager.CheckAsync(Package("let marker = 1"));
            await checker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Task dispose = manager.DisposeAsync().AsTask();

            dispose.IsCompleted.Should().BeFalse("manager disposal must drain the check already inside the compiler");
            ExtensionCheckResult rejected = await manager.CheckAsync(Package("let marker = 2"));
            rejected.Succeeded.Should().BeFalse();
            checker.CallCount.Should().Be(1, "checks that start after disposal begins must not reach the compiler");

            checker.Release.TrySetResult();
            (await startedCheck).Succeeded.Should().BeTrue();
            await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            checker.Release.TrySetResult();
            await manager.DisposeAsync();
            if (Directory.Exists(runtimeDirectory))
            {
                Directory.Delete(runtimeDirectory, true);
            }
        }
    }

    [Fact]
    public async Task Retired_generation_stays_alive_until_its_last_lease_is_released()
    {
        MemoryExtensionFileStore store = new();
        CountingGenerationLoader loader = new();
        RecordingSourceChecker checker = new();
        await using IExtensionManager manager = CreateStateMachineManager(store, loader, checker);
        ExtensionPackageSnapshot first = Package("let marker = 1");
        ExtensionPackageSnapshot second = Package("let marker = 2");

        (await manager.ActivateAsync("test.extension", first, CancellationToken.None)).Succeeded.Should().BeTrue();
        IExtensionLease heldLease = (await manager.AcquireAsync("test.extension", CancellationToken.None))!;
        await manager.ActivateAsync("test.extension", second, CancellationToken.None);

        loader.Generations.Should().HaveCount(2);
        loader.Generations[0].DisposeCount.Should().Be(0);
        await using IExtensionLease currentLease =
            (await manager.AcquireAsync("test.extension", CancellationToken.None))!;
        currentLease.Fingerprint.Should().Be(second.Fingerprint);

        await heldLease.DisposeAsync();
        await heldLease.DisposeAsync();

        loader.Generations[0].DisposeCount.Should().Be(1);
        loader.Generations[1].DisposeCount.Should().Be(0);
        checker.Calls.Select(call => call.Fingerprint).Should().Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public async Task Load_stages_a_candidate_without_auto_activating_it()
    {
        MemoryExtensionFileStore store = new();
        CountingGenerationLoader loader = new();
        RecordingSourceChecker checker = new();
        await using IExtensionManager manager = CreateStateMachineManager(store, loader, checker);
        ExtensionPackageSnapshot package = Package("let marker = 1");

        ExtensionCheckResult loaded = await manager.LoadAsync(package, CancellationToken.None);

        loaded.Succeeded.Should().BeTrue();
        (await manager.AcquireAsync("test.extension", CancellationToken.None)).Should().BeNull();
        manager.List().Should().BeEmpty();
        loader.Generations.Should().ContainSingle();
        loader.Generations[0].DisposeCount.Should().Be(0);
        checker.Calls.Should().ContainSingle().Which.Fingerprint.Should().Be(package.Fingerprint);
    }

    [Fact]
    public async Task Loader_rejects_a_generation_whose_descriptor_identity_does_not_match_the_package()
    {
        MemoryExtensionFileStore store = new();
        CountingGenerationLoader loader = new("another.extension");
        RecordingSourceChecker checker = new();
        await using IExtensionManager manager = CreateStateMachineManager(store, loader, checker);

        ExtensionCheckResult result = await manager.ActivateAsync("test.extension", Package("let marker = 1"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().Contain(issue => issue.Code == "EXT0002");
        (await manager.AcquireAsync("test.extension", CancellationToken.None)).Should().BeNull();
        loader.Generations.Should().ContainSingle();
        loader.Generations[0].DisposeCount.Should().Be(1);
        checker.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Config_updates_apply_to_new_leases_without_changing_an_existing_run_snapshot()
    {
        MemoryExtensionFileStore store = new();
        CountingGenerationLoader loader = new();
        RecordingSourceChecker checker = new();
        await using IExtensionManager manager = CreateStateMachineManager(store, loader, checker);
        ExtensionPackageSnapshot package = Package("let marker = 1");
        (await manager.ActivateAsync("test.extension", package, CancellationToken.None)).Succeeded.Should().BeTrue();
        Dictionary<string, string> originalValues = new(StringComparer.Ordinal) { ["model"] = "first" };
        ExtensionConfigSnapshot original = new(originalValues, 1);
        await manager.SaveConfigAsync("test.extension", original, CancellationToken.None);
        originalValues["model"] = "mutated outside the manager";
        await using IExtensionLease runLease = (await manager.AcquireAsync("test.extension", CancellationToken.None))!;

        ExtensionConfigSnapshot updated = new(new Dictionary<string, string> { ["model"] = "second" }, 2);
        await manager.SaveConfigAsync("test.extension", updated, CancellationToken.None);
        await using IExtensionLease nextLease = (await manager.AcquireAsync("test.extension", CancellationToken.None))!;

        runLease.Config.Revision.Should().Be(1);
        runLease.Config.Values["model"].Should().Be("first");
        nextLease.Config.Revision.Should().Be(2);
        nextLease.Config.Values["model"].Should().Be("second");
        checker.Calls.Should().ContainSingle().Which.Fingerprint.Should().Be(package.Fingerprint);
    }

    [Fact]
    public async Task Restore_uses_the_saved_active_package_and_rollback_requires_explicit_lkg_promotion()
    {
        string runtimeDirectory = Path.Combine(Path.GetTempPath(), $"patchouli-extension-state-{Guid.NewGuid():N}");
        ExtensionPackageSnapshot first = Package("let marker = 1");
        ExtensionPackageSnapshot second = Package("let marker = 2");
        try
        {
            CountingGenerationLoader firstLoader = new();
            RecordingSourceChecker firstChecker = new();
            await using (IExtensionManager manager =
                         CreatePersistentStateMachineManager(runtimeDirectory, firstLoader, firstChecker))
            {
                (await manager.ActivateAsync("test.extension", first, CancellationToken.None)).Succeeded.Should()
                    .BeTrue();
                (await manager.PromoteLastKnownGoodAsync("test.extension", CancellationToken.None)).Should().BeTrue();
                (await manager.ActivateAsync("test.extension", second, CancellationToken.None)).Succeeded.Should()
                    .BeTrue();
            }

            CountingGenerationLoader restoredLoader = new();
            RecordingSourceChecker restoredChecker = new();
            await using IExtensionManager restored =
                CreatePersistentStateMachineManager(runtimeDirectory, restoredLoader, restoredChecker);
            (await restored.RestoreAsync("test.extension", CancellationToken.None)).Should().BeTrue();
            await using IExtensionLease active =
                (await restored.AcquireAsync("test.extension", CancellationToken.None))!;
            active.Fingerprint.Should().Be(second.Fingerprint);

            (await restored.RollbackAsync("test.extension", CancellationToken.None)).Should().BeTrue();
            await using IExtensionLease rolledBack =
                (await restored.AcquireAsync("test.extension", CancellationToken.None))!;
            rolledBack.Fingerprint.Should().Be(first.Fingerprint);
            restoredChecker.Calls.Select(call => call.Fingerprint).Should()
                .Equal(second.Fingerprint, first.Fingerprint);
        }
        finally
        {
            if (Directory.Exists(runtimeDirectory))
            {
                Directory.Delete(runtimeDirectory, true);
            }
        }
    }

    [Fact]
    public async Task Restore_rejects_a_tampered_content_addressed_snapshot()
    {
        string runtimeDirectory = Path.Combine(Path.GetTempPath(), $"patchouli-extension-tamper-{Guid.NewGuid():N}");
        ExtensionPackageSnapshot package = Package("let marker = 1");
        try
        {
            await using (IExtensionManager manager =
                         CreatePersistentStateMachineManager(runtimeDirectory,
                             new CountingGenerationLoader(), new RecordingSourceChecker()))
            {
                (await manager.ActivateAsync("test.extension", package, CancellationToken.None)).Succeeded.Should()
                    .BeTrue();
            }

            string savedSource = Path.Combine(runtimeDirectory, "extensions", "test.extension", ".generations",
                package.Fingerprint, "main.fsx");
            File.WriteAllText(savedSource, "tampered source");

            RecordingSourceChecker restoredChecker = new();
            await using IExtensionManager restored =
                CreatePersistentStateMachineManager(runtimeDirectory, new CountingGenerationLoader(),
                    restoredChecker);
            (await restored.RestoreAsync("test.extension", CancellationToken.None)).Should().BeFalse();
            (await restored.AcquireAsync("test.extension", CancellationToken.None)).Should().BeNull();
            restoredChecker.Calls.Should().BeEmpty(
                "the content fingerprint check rejects the tampered snapshot before source checking runs");
        }
        finally
        {
            if (Directory.Exists(runtimeDirectory))
            {
                Directory.Delete(runtimeDirectory, true);
            }
        }
    }

    [Fact]
    public async Task Restore_falls_back_to_the_independent_lkg_snapshot_when_active_generation_is_missing()
    {
        string runtimeDirectory =
            Path.Combine(Path.GetTempPath(), $"patchouli-extension-lkg-fallback-{Guid.NewGuid():N}");
        ExtensionPackageSnapshot package = Package("let marker = 1");
        try
        {
            await using (IExtensionManager manager =
                         CreatePersistentStateMachineManager(runtimeDirectory,
                             new CountingGenerationLoader(), new RecordingSourceChecker()))
            {
                (await manager.ActivateAsync(package.Id, package, CancellationToken.None)).Succeeded.Should().BeTrue();
                (await manager.PromoteLastKnownGoodAsync(package.Id, CancellationToken.None)).Should().BeTrue();
                (await manager.ActivateAsync(package.Id, Package("let marker = 2"), CancellationToken.None))
                    .Succeeded.Should().BeTrue();
            }

            string activeDirectory = Path.Combine(runtimeDirectory, "extensions", package.Id, ".generations");
            Directory.Delete(activeDirectory, true);

            RecordingSourceChecker restoredChecker = new();
            await using IExtensionManager restored =
                CreatePersistentStateMachineManager(runtimeDirectory, new CountingGenerationLoader(),
                    restoredChecker);
            (await restored.RestoreAsync(package.Id, CancellationToken.None)).Should().BeTrue();
            await using IExtensionLease lease = (await restored.AcquireAsync(package.Id, CancellationToken.None))!;
            lease.Fingerprint.Should().Be(package.Fingerprint);
            restoredChecker.Calls.Should().ContainSingle().Which.Fingerprint.Should().Be(package.Fingerprint);
        }
        finally
        {
            if (Directory.Exists(runtimeDirectory))
            {
                Directory.Delete(runtimeDirectory, true);
            }
        }
    }

    [Fact]
    public async Task Restore_repairs_a_damaged_active_generation_from_its_unchanged_lkg_copy()
    {
        string runtimeDirectory =
            Path.Combine(Path.GetTempPath(), $"patchouli-extension-lkg-repair-{Guid.NewGuid():N}");
        ExtensionPackageSnapshot package = Package("let marker = 1");
        try
        {
            await using (IExtensionManager manager =
                         CreatePersistentStateMachineManager(runtimeDirectory,
                             new CountingGenerationLoader(), new RecordingSourceChecker()))
            {
                (await manager.ActivateAsync(package.Id, package, CancellationToken.None)).Succeeded.Should().BeTrue();
                (await manager.PromoteLastKnownGoodAsync(package.Id, CancellationToken.None)).Should().BeTrue();
            }

            string packageDirectory = Path.Combine(runtimeDirectory, "extensions", package.Id);
            string activeSource = Path.Combine(packageDirectory, ".generations", package.Fingerprint, "main.fsx");
            string lkgSource = Path.Combine(packageDirectory, ".lkg", package.Fingerprint, "main.fsx");
            File.WriteAllText(activeSource, "tampered active generation");

            RecordingSourceChecker restoredChecker = new();
            await using IExtensionManager restored =
                CreatePersistentStateMachineManager(runtimeDirectory, new CountingGenerationLoader(),
                    restoredChecker);
            (await restored.RestoreAsync(package.Id, CancellationToken.None)).Should().BeTrue();
            await using IExtensionLease lease = (await restored.AcquireAsync(package.Id, CancellationToken.None))!;
            lease.Fingerprint.Should().Be(package.Fingerprint);
            File.ReadAllText(activeSource).Should().Be(package.Sources["main.fsx"]);
            File.ReadAllText(lkgSource).Should().Be(package.Sources["main.fsx"]);
            restoredChecker.Calls.Should().ContainSingle().Which.Fingerprint.Should().Be(package.Fingerprint);
        }
        finally
        {
            if (Directory.Exists(runtimeDirectory))
            {
                Directory.Delete(runtimeDirectory, true);
            }
        }
    }

    [Fact]
    public async Task Rollback_reads_independent_lkg_snapshot_and_refuses_a_tampered_lkg_without_replacing_active()
    {
        string runtimeDirectory =
            Path.Combine(Path.GetTempPath(), $"patchouli-extension-lkg-tamper-{Guid.NewGuid():N}");
        ExtensionPackageSnapshot first = Package("let marker = 1");
        ExtensionPackageSnapshot second = Package("let marker = 2");
        try
        {
            RecordingSourceChecker checker = new();
            await using IExtensionManager manager =
                CreatePersistentStateMachineManager(runtimeDirectory, new CountingGenerationLoader(), checker);
            (await manager.ActivateAsync(first.Id, first, CancellationToken.None)).Succeeded.Should().BeTrue();
            (await manager.PromoteLastKnownGoodAsync(first.Id, CancellationToken.None)).Should().BeTrue();
            (await manager.ActivateAsync(second.Id, second, CancellationToken.None)).Succeeded.Should().BeTrue();

            string activeSource = Path.Combine(runtimeDirectory, "extensions", first.Id, ".generations",
                first.Fingerprint, "main.fsx");
            File.Delete(activeSource);
            (await manager.RollbackAsync(first.Id, CancellationToken.None)).Should().BeTrue();
            await using IExtensionLease rolledBack = (await manager.AcquireAsync(first.Id, CancellationToken.None))!;
            rolledBack.Fingerprint.Should().Be(first.Fingerprint);
            await rolledBack.DisposeAsync();

            (await manager.ActivateAsync(second.Id, second, CancellationToken.None)).Succeeded.Should().BeTrue();
            string lkgSource = Path.Combine(runtimeDirectory, "extensions", first.Id, ".lkg",
                first.Fingerprint, "main.fsx");
            File.WriteAllText(lkgSource, "tampered independent snapshot");

            (await manager.RollbackAsync(first.Id, CancellationToken.None)).Should().BeFalse();
            await using IExtensionLease current = (await manager.AcquireAsync(first.Id, CancellationToken.None))!;
            current.Fingerprint.Should().Be(second.Fingerprint);
            checker.Calls.Select(call => call.Fingerprint).Should()
                .Equal(first.Fingerprint, second.Fingerprint, first.Fingerprint, second.Fingerprint);
        }
        finally
        {
            if (Directory.Exists(runtimeDirectory))
            {
                Directory.Delete(runtimeDirectory, true);
            }
        }
    }

    // State-machine fake-checker path: the recording checker supplies preset results while the real
    // manager lifecycle (staging, activation, leases, draining) still runs underneath it.
    private static IExtensionManager CreateStateMachineManager(MemoryExtensionFileStore store,
        CountingGenerationLoader loader, RecordingSourceChecker checker)
    {
        return new ExtensionManager(Path.Combine(Path.GetTempPath(), $"patchouli-extensions-{Guid.NewGuid():N}"),
            TestExtensionResources.Unavailable(), store, loader, checker);
    }

    // Persistent state-machine fake-checker path: same preset check boundary over the real
    // FileExtensionStore so snapshot persistence and content-hash verification still run.
    private static IExtensionManager CreatePersistentStateMachineManager(string runtimeDirectory,
        CountingGenerationLoader loader, RecordingSourceChecker checker)
    {
        return new ExtensionManager(runtimeDirectory, TestExtensionResources.Unavailable(),
            new FileExtensionStore(runtimeDirectory),
            loader, checker);
    }

    // Real-checker anchor path: the default constructor wires the real FCS-based ExtensionCompiler.
    private static IExtensionManager CreateRealCheckerStateMachineManager(MemoryExtensionFileStore store,
        CountingGenerationLoader loader)
    {
        return new ExtensionManager(Path.Combine(Path.GetTempPath(), $"patchouli-extensions-{Guid.NewGuid():N}"),
            TestExtensionResources.Unavailable(), store, loader);
    }

    private static ExtensionPackageSnapshot Package(string body, string version = "1.0.0")
    {
        return new ExtensionPackageSnapshot("test.extension", new Dictionary<string, string>
        {
            ["main.fsx"] =
                "open System.Threading.Tasks\nopen Patchouli.Extensions.Contracts\nopen Patchouli.Extensions.Sdk\n" +
                body + "\n" +
                "/// <summary>Lifecycle test extension.</summary>\n" +
                $"/// <patchouli-extension id=\"test.extension\" kind=\"translation\" version=\"{version}\" host-api=\"patchouli.extensions/3\">\n" +
                "/// <name>Test extension</name>\n/// </patchouli-extension>\n" +
                "let extension = Extension.createTranslation<unit> (fun () _ _ _ -> Task.CompletedTask)\n"
        });
    }

    // Preset check boundary for the deterministic state-machine cases only. It honors cancellation and
    // records each call with the package identity, but it never parses F# and never judges tampering;
    // the manager's real state, file, and hash verification remains responsible for those decisions.
    private sealed class RecordingSourceChecker(ExtensionCheckResult? preset = null) : IExtensionSourceChecker
    {
        private readonly ConcurrentQueue<ExtensionPackageSnapshot> _calls = new();
        private readonly ExtensionCheckResult _preset = preset ?? new ExtensionCheckResult(true, [], []);

        public IReadOnlyList<ExtensionPackageSnapshot> Calls => _calls.ToArray();

        public Task<ExtensionCheckResult> CheckAsync(ExtensionPackageSnapshot package,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _calls.Enqueue(package);
            return Task.FromResult(_preset);
        }
    }

    private sealed class BlockingSourceChecker : IExtensionSourceChecker
    {
        private int _callCount;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<ExtensionCheckResult> CheckAsync(ExtensionPackageSnapshot package,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new ExtensionCheckResult(true, [], []);
        }
    }

    private sealed class MemoryExtensionFileStore : IExtensionFileStore
    {
        private readonly Dictionary<string, ExtensionConfigSnapshot> _configs = new(StringComparer.Ordinal);

        public Task<IReadOnlyDictionary<string, string>> ReadSourcesAsync(string id,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        }

        public Task WriteSourcesAsync(string id, IReadOnlyDictionary<string, string> sources,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<ExtensionConfigSnapshot> ReadConfigAsync(string id, CancellationToken cancellationToken)
        {
            return Task.FromResult(_configs.TryGetValue(id, out ExtensionConfigSnapshot? config)
                ? config
                : new ExtensionConfigSnapshot(new Dictionary<string, string>(), 0));
        }

        public Task WriteConfigAsync(string id, ExtensionConfigSnapshot config, CancellationToken cancellationToken)
        {
            _configs[id] = config;
            return Task.CompletedTask;
        }
    }

    private sealed class CountingGenerationLoader : IExtensionGenerationLoader
    {
        private readonly string? _descriptorId;

        public CountingGenerationLoader(string? descriptorId = null)
        {
            _descriptorId = descriptorId;
        }

        public List<CountingGeneration> Generations { get; } = [];

        public Task<IExtensionGeneration> LoadAsync(ExtensionPackageSnapshot package,
            CancellationToken cancellationToken)
        {
            ExtensionDescriptor descriptor = ExtensionEntryMetadata.Parse(package).ToDescriptor();
            if (_descriptorId is not null)
            {
                descriptor = descriptor with { Id = _descriptorId };
            }

            CountingGeneration generation = new(new FakeTranslationExtension(descriptor));
            lock (Generations)
            {
                Generations.Add(generation);
            }

            return Task.FromResult<IExtensionGeneration>(generation);
        }
    }

    private sealed class CountingGeneration(ITranslationExtension translation) : IExtensionGeneration
    {
        public int DisposeCount { get; private set; }
        public ITranslationExtension Translation => translation;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeTranslationExtension(ExtensionDescriptor descriptor) : ITranslationExtension
    {
        public ExtensionDescriptor Descriptor { get; } = descriptor;
        public IReadOnlyList<ConfigFieldDescriptor> ConfigFields { get; } = [];

        public Task RunAsync(ITranslationJob job, IExtensionResources resources,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
