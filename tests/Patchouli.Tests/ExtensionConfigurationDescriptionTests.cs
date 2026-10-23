using FluentAssertions;
using Patchouli.Extensions;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Extensions;
using Patchouli.Infrastructure.Bibliography.MetadataLookup;
using Patchouli.Infrastructure.Translation;

namespace Patchouli.Tests;

public sealed class ExtensionConfigurationDescriptionTests
{
    [Theory]
    [InlineData("translation.google-free", "Google Free", ExtensionKind.Translation)]
    [InlineData("metadata.crossref", "Crossref", ExtensionKind.Metadata)]
    public async Task Cold_built_in_uses_embedded_schema_without_check_or_generation_activation(string id, string name,
        ExtensionKind kind)
    {
        string runtime = NewRuntimeDirectory();
        CountingChecker checker = new();
        CountingLoader loader = new();
        IReadOnlyList<ExtensionPackageSnapshot> packages = BuiltInTranslationExtensions.Packages
            .Concat(BuiltInMetadataExtensions.Packages).ToArray();
        ExtensionPackageSnapshot bundled = packages.Single(package => package.Id == id);
        try
        {
            await using IExtensionManager inner = CreateManager(runtime, checker, loader);
            ObservedManager manager = new(inner);
            (await manager.ReadSourceAsync(id)).Sources.Should().BeEmpty();
            ExtensionConfigurationDescription description = await ExtensionConfigurationDescriptionService.ReadAsync(
                manager, packages, id);

            description.Source.Should().Be("built-in schema");
            description.HasTrustedSchema.Should().BeTrue();
            description.SchemaMatchesSource.Should().BeTrue();
            description.Fields.Should().NotBeEmpty();
            description.Name.Should().Be(name);
            description.Kind.Should().Be(kind);
            description.SchemaHostApi.Should().Be(ExtensionHostApi.Current);
            description.ToolchainIdentity.Should().Be(ExtensionArtifact.runtimeToolchainIdentity());
            checker.CallCount.Should().Be(0);
            loader.CallCount.Should().Be(0);
            manager.CheckCalls.Should().Be(0);
            manager.AcquireCalls.Should().Be(0);
            manager.ActivateCalls.Should().Be(0);
            manager.List().Should().NotContain(item => item.IsActive);

            Dictionary<string, string> changedSources = new(bundled.Sources, StringComparer.Ordinal);
            changedSources["main.fsx"] += "\n// source fingerprint changed\n";
            await manager.SaveSourceAsync(id, changedSources);
            ExtensionConfigurationDescription changed = await ExtensionConfigurationDescriptionService.ReadAsync(
                manager, packages, id);
            changed.SourceFingerprint.Should().NotBe(bundled.Fingerprint);
            changed.HasTrustedSchema.Should().BeFalse();
            changed.Fields.Should().BeEmpty();
            checker.CallCount.Should().Be(0);
            loader.CallCount.Should().Be(0);
            manager.CheckCalls.Should().Be(0);
            manager.AcquireCalls.Should().Be(0);
            manager.ActivateCalls.Should().Be(0);
        }
        finally
        {
            DeleteRuntimeDirectory(runtime);
        }
    }

    [Fact]
    public async Task Warm_active_schema_is_reused_and_damaged_working_source_is_not_mislabeled_as_active()
    {
        string runtime = NewRuntimeDirectory();
        const string id = "translation.active-description-test";
        ExtensionPackageSnapshot package = Package(id, "translation");
        CountingChecker checker = new();
        CountingLoader loader = new();
        try
        {
            await using IExtensionManager inner = CreateManager(runtime, checker, loader);
            ObservedManager manager = new(inner);
            await manager.SaveSourceAsync(id, package.Sources);
            ExtensionCheckResult activated = await manager.ActivateAsync(id, package);
            activated.Succeeded.Should().BeTrue();
            int checks = checker.CallCount;
            int loads = loader.CallCount;
            int managerChecks = manager.CheckCalls;
            manager.List().Single(item => item.Id == id).Fingerprint.Should().Be(package.Fingerprint);

            ExtensionConfigurationDescription warm = await ExtensionConfigurationDescriptionService.ReadAsync(
                manager, [], id);
            warm.HasTrustedSchema.Should().BeTrue();
            warm.SchemaMatchesSource.Should().BeTrue();
            checker.CallCount.Should().Be(checks);
            loader.CallCount.Should().Be(loads);
            manager.CheckCalls.Should().Be(managerChecks);
            manager.AcquiredIds.Should().ContainSingle().Which.Should().Be(id);

            await manager.SaveSourceAsync(id, new Dictionary<string, string>
            {
                ["main.fsx"] = "let extension = ("
            });
            ExtensionConfigurationDescription changed = await ExtensionConfigurationDescriptionService.ReadAsync(
                manager, [], id);

            changed.HasTrustedSchema.Should().BeTrue();
            changed.SchemaMatchesSource.Should().BeFalse();
            changed.Source.Should().Contain("working source differs");
            changed.SourceFingerprint.Should().NotBe(package.Fingerprint);
            changed.SchemaFingerprint.Should().Be(package.Fingerprint);
            changed.Diagnostics.Should()
                .Contain(diagnostic => diagnostic.Severity == ExtensionDiagnosticSeverity.Error);
            checker.CallCount.Should().Be(checks);
            loader.CallCount.Should().Be(loads);
            manager.CheckCalls.Should().Be(managerChecks);
            manager.AcquiredIds.Should().HaveCount(2).And.OnlyContain(acquiredId => acquiredId == id);
        }
        finally
        {
            DeleteRuntimeDirectory(runtime);
        }
    }

    [Theory]
    [InlineData("translation.description-test", "translation")]
    [InlineData("metadata.description-test", "metadata")]
    public async Task Cold_description_is_source_only_and_never_checks_or_acquires(string id, string kind)
    {
        ExtensionPackageSnapshot package = Package(id, kind);
        CountingManager manager = new(package);

        ExtensionConfigurationDescription description = await ExtensionConfigurationDescriptionService.ReadAsync(
            manager, [], id);

        description.Id.Should().Be(id);
        description.Source.Should().Be("uninspected");
        description.HasTrustedSchema.Should().BeFalse();
        description.Fields.Should().BeEmpty();
        manager.CheckCalls.Should().Be(0);
        manager.AcquireCalls.Should().Be(0);
        manager.ActivateCalls.Should().Be(0);
    }

    private static ExtensionPackageSnapshot Package(string id, string kind)
    {
        string source = "/// <summary>Read-only description test.</summary>\n" +
                        $"/// <patchouli-extension id=\"{id}\" kind=\"{kind}\" version=\"1.0.0\" host-api=\"patchouli.extensions/3\"><name>Description test</name></patchouli-extension>\n" +
                        "let extension = ()\n";
        return new ExtensionPackageSnapshot(id, new Dictionary<string, string> { ["main.fsx"] = source });
    }

    private static string NewRuntimeDirectory()
    {
        string directory = Path.Combine(
            TestPaths.FromRepositoryRoot(".tmp", "tests", "extension-configuration-description"),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteRuntimeDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    private static IExtensionManager CreateManager(string runtime, CountingChecker checker, CountingLoader loader)
    {
        return new ExtensionManager(runtime, TestExtensionResources.Unavailable(), new FileExtensionStore(runtime),
            loader, checker);
    }

    private sealed class CountingManager(ExtensionPackageSnapshot package) : IExtensionManager
    {
        public int CheckCalls { get; private set; }
        public int AcquireCalls { get; private set; }
        public int ActivateCalls { get; private set; }

        public Task<ExtensionCheckResult> CheckAsync(ExtensionPackageSnapshot candidate,
            CancellationToken cancellationToken = default)
        {
            CheckCalls++;
            throw new InvalidOperationException("Description reads must not check source.");
        }

        public Task<ExtensionCheckResult> LoadAsync(ExtensionPackageSnapshot candidate,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExtensionCheckResult> ActivateAsync(string id, ExtensionPackageSnapshot candidate,
            CancellationToken cancellationToken = default)
        {
            ActivateCalls++;
            throw new InvalidOperationException("Description reads must not activate source.");
        }

        public Task SaveSourceAsync(string id, IReadOnlyDictionary<string, string> sources,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExtensionPackageSnapshot> ReadSourceAsync(string id,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(package);
        }

        public ValueTask<IExtensionLease?> AcquireAsync(string id, CancellationToken cancellationToken = default)
        {
            AcquireCalls++;
            return ValueTask.FromResult<IExtensionLease?>(null);
        }

        public IReadOnlyList<ExtensionGenerationInfo> List()
        {
            return [];
        }

        public Task SaveConfigAsync(string id, ExtensionConfigSnapshot config,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExtensionConfigSnapshot> ReadConfigAsync(string id,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> RestoreAsync(string id, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> PromoteLastKnownGoodAsync(string id, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> RollbackAsync(string id, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ObservedManager(IExtensionManager inner) : IExtensionManager
    {
        public int CheckCalls { get; private set; }
        public int AcquireCalls { get; private set; }
        public int ActivateCalls { get; private set; }
        public List<string> AcquiredIds { get; } = [];

        public Task<ExtensionCheckResult> CheckAsync(ExtensionPackageSnapshot package,
            CancellationToken cancellationToken = default)
        {
            CheckCalls++;
            return inner.CheckAsync(package, cancellationToken);
        }

        public Task<ExtensionCheckResult> LoadAsync(ExtensionPackageSnapshot package,
            CancellationToken cancellationToken = default)
        {
            return inner.LoadAsync(package, cancellationToken);
        }

        public Task<ExtensionCheckResult> ActivateAsync(string id, ExtensionPackageSnapshot package,
            CancellationToken cancellationToken = default)
        {
            ActivateCalls++;
            return inner.ActivateAsync(id, package, cancellationToken);
        }

        public Task SaveSourceAsync(string id, IReadOnlyDictionary<string, string> sources,
            CancellationToken cancellationToken = default)
        {
            return inner.SaveSourceAsync(id, sources, cancellationToken);
        }

        public Task<ExtensionPackageSnapshot> ReadSourceAsync(string id,
            CancellationToken cancellationToken = default)
        {
            return inner.ReadSourceAsync(id, cancellationToken);
        }

        public ValueTask<IExtensionLease?> AcquireAsync(string id, CancellationToken cancellationToken = default)
        {
            AcquireCalls++;
            AcquiredIds.Add(id);
            return inner.AcquireAsync(id, cancellationToken);
        }

        public IReadOnlyList<ExtensionGenerationInfo> List()
        {
            return inner.List();
        }

        public Task SaveConfigAsync(string id, ExtensionConfigSnapshot config,
            CancellationToken cancellationToken = default)
        {
            return inner.SaveConfigAsync(id, config, cancellationToken);
        }

        public Task<ExtensionConfigSnapshot> ReadConfigAsync(string id,
            CancellationToken cancellationToken = default)
        {
            return inner.ReadConfigAsync(id, cancellationToken);
        }

        public Task<bool> RestoreAsync(string id, CancellationToken cancellationToken = default)
        {
            return inner.RestoreAsync(id, cancellationToken);
        }

        public Task<bool> PromoteLastKnownGoodAsync(string id, CancellationToken cancellationToken = default)
        {
            return inner.PromoteLastKnownGoodAsync(id, cancellationToken);
        }

        public Task<bool> RollbackAsync(string id, CancellationToken cancellationToken = default)
        {
            return inner.RollbackAsync(id, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return inner.DisposeAsync();
        }
    }

    private sealed class CountingChecker : IExtensionSourceChecker
    {
        public int CallCount { get; private set; }

        public Task<ExtensionCheckResult> CheckAsync(ExtensionPackageSnapshot package,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ExtensionCheckResult(true,
                [new ConfigFieldDescriptor("limit", "Limit", ConfigValueKind.Integer, false, "", "2")], []));
        }
    }

    private sealed class CountingLoader : IExtensionGenerationLoader
    {
        public int CallCount { get; private set; }

        public Task<IExtensionGeneration> LoadAsync(ExtensionPackageSnapshot package,
            CancellationToken cancellationToken)
        {
            CallCount++;
            ExtensionEntryMetadata metadata = ExtensionEntryMetadata.Parse(package);
            return Task.FromResult<IExtensionGeneration>(new TestGeneration(new TestTranslation(metadata.ToDescriptor(),
                [new ConfigFieldDescriptor("limit", "Limit", ConfigValueKind.Integer, false, "", "2")])));
        }
    }

    private sealed class TestGeneration(ITranslationExtension translation) : IExtensionGeneration
    {
        public ITranslationExtension Translation => translation;

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestTranslation(
        ExtensionDescriptor descriptor,
        IReadOnlyList<ConfigFieldDescriptor> fields) : ITranslationExtension
    {
        public ExtensionDescriptor Descriptor => descriptor;
        public IReadOnlyList<ConfigFieldDescriptor> ConfigFields => fields;

        public Task RunAsync(ITranslationJob job, IExtensionResources resources,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
