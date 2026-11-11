using System.IO.Compression;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NSec.Cryptography;
using Patchouli.Core.Downloads;
using Patchouli.Core.Results;
using Patchouli.Host.Downloads;
using Patchouli.Host.Plugins;
using Patchouli.Testing;

namespace Patchouli.Tests;

public sealed class PluginStoreTests
{
    private const string PluginId = "example.test-plugin";
    private const string IndexUrl = "https://example.test/index.json";
    private const string SignatureUrl = "https://example.test/index.json.minisig";
    private const string ArtifactUrl = "https://example.test/plugin.zip";

    [Fact]
    public async Task Registered_install_streams_signed_bytes_and_awaits_the_real_installation()
    {
        using SigningKey key = new();
        byte[] package = Package("0.1.0");
        FakeHttpHandler catalog = new();
        SetRelease(catalog, key, "0.1.0", package);
        using HttpClient catalogClient = new(catalog);
        using GatedAssetStream body = new(package);
        using HttpClient downloadClient = new(new GatedAssetHttpHandler(ArtifactUrl, body));
        await using DownloadTaskService downloads = new();
        string root = NewRoot();
        FakeLauncher launcher = new();
        using PluginStore store = CreateStore(root, key, catalogClient, downloadClient, launcher,
            downloads: downloads);
        (await store.RefreshAsync(true)).IsSuccess.Should().BeTrue();

        Task<Result<PluginDefinition>> pending = store.InstallAsync(PluginId);
        try
        {
            await body.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
            DownloadTaskStatus running = downloads.List().Should().ContainSingle().Subject;
            running.State.Should().Be(DownloadTaskService.RunningState);
            running.CanRetry.Should().BeFalse();
            DownloadFileProgress file = running.Files.Should().ContainSingle().Subject;
            file.RelativePath.Should().Be("plugin.zip");
            file.DownloadedBytes.Should().BeGreaterThan(0).And.BeLessThan(package.Length);
            file.TotalBytes.Should().Be(package.Length, "the verified catalog supplies the signed total");
            pending.IsCompleted.Should().BeFalse();
            launcher.Starts.Should().Be(0, "protocol validation cannot start before the archive is complete");
            PluginRegistry.Load(PluginRegistry.RegistryPath(root)).Entries.Should().BeEmpty();
        }
        finally
        {
            body.Release.TrySetResult();
        }

        Result<PluginDefinition> installed = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        installed.IsSuccess.Should().BeTrue(installed.ErrorMessage);
        installed.Value.Version.Should().Be("0.1.0");
        launcher.Starts.Should().Be(1, "the fake child still performs the real MCP protocol handshake");
        PluginRegistryEntry registered = PluginRegistry.Load(PluginRegistry.RegistryPath(root))
            .Entries.Should().ContainSingle().Subject;
        PluginManifestLoader.Load(registered.ManifestPath).Value.Version.Should().Be("0.1.0");
        registered.SourceRevision.Should().Be(new string('a', 40));
        DownloadTaskStatus completed = downloads.List().Should().ContainSingle().Subject;
        completed.State.Should().Be(DownloadTaskService.SucceededState);
        completed.CanRetry.Should().BeFalse();
        completed.Files.Single().DownloadedBytes.Should().Be(package.Length);
        completed.Files.Single().TotalBytes.Should().Be(package.Length);
    }

    [Fact]
    public async Task Cancelling_a_registered_install_returns_the_typed_cancellation_and_only_removes_its_staging()
    {
        using SigningKey key = new();
        byte[] package = Package("0.1.0");
        FakeHttpHandler catalog = new();
        SetRelease(catalog, key, "0.1.0", package);
        using HttpClient catalogClient = new(catalog);
        using GatedAssetStream body = new(package);
        using HttpClient downloadClient = new(new GatedAssetHttpHandler(ArtifactUrl, body));
        await using DownloadTaskService downloads = new();
        string root = NewRoot();
        string staging = Path.Combine(root, "plugins", ".staging");
        string unrelated = Path.Combine(staging, "another-operation");
        Directory.CreateDirectory(unrelated);
        string sentinel = Path.Combine(unrelated, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "unrelated");
        FakeLauncher launcher = new();
        using PluginStore store = CreateStore(root, key, catalogClient, downloadClient, launcher,
            downloads: downloads);
        (await store.RefreshAsync(true)).IsSuccess.Should().BeTrue();

        Task<Result<PluginDefinition>> pending = store.InstallAsync(PluginId);
        try
        {
            await body.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
            DownloadTaskStatus running = downloads.List().Should().ContainSingle().Subject;
            running.Files.Single().DownloadedBytes.Should().BeGreaterThan(0);
            running.Files.Single().TotalBytes.Should().Be(package.Length);
            Directory.GetDirectories(staging).Should().HaveCount(2);
            downloads.Cancel(running.TaskId).IsSuccess.Should().BeTrue();
            Result<PluginDefinition> cancelled = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            cancelled.IsFailure.Should().BeTrue();
            cancelled.ErrorCode.Should().Be("cancelled");
            DownloadTaskStatus terminal = downloads.List().Should().ContainSingle().Subject;
            terminal.State.Should().Be(DownloadTaskService.CancelledState);
            terminal.CanRetry.Should().BeFalse();
            terminal.Files.Single().State.Should().Be("cancelled");
            launcher.Starts.Should().Be(0);
            PluginRegistry.Load(PluginRegistry.RegistryPath(root)).Entries.Should().BeEmpty();
            Directory.GetDirectories(staging).Should().Equal(unrelated);
            (await File.ReadAllTextAsync(sentinel)).Should().Be("unrelated");
            Directory.Exists(Path.Combine(root, "plugins", PluginId, "0.1.0")).Should().BeFalse();
        }
        finally
        {
            body.Release.TrySetResult();
        }
    }

    [Fact]
    public void Minisign_verifies_content_and_trusted_comment()
    {
        using SigningKey key = new();
        byte[] content = "signed plugin index\n"u8.ToArray();
        string signature = key.Sign(content);

        PluginIndexSignature.Verify(content, signature, [key.PublicKey]).IsSuccess.Should().BeTrue();
        byte[] changed = content.ToArray();
        changed[0] ^= 1;
        PluginIndexSignature.Verify(changed, signature, [key.PublicKey]).IsFailure.Should().BeTrue();
        string changedComment = signature.Replace("timestamp:1", "timestamp:2", StringComparison.Ordinal);
        PluginIndexSignature.Verify(content, changedComment, [key.PublicKey]).IsFailure.Should().BeTrue();
        using SigningKey other = new();
        PluginIndexSignature.Verify(content, signature, [other.PublicKey]).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Signed_catalog_uses_only_a_verified_cache()
    {
        using SigningKey key = new();
        byte[] package = Package("0.1.0");
        FakeHttpHandler http = new();
        SetRelease(http, key, "0.1.0", package);
        string root = NewRoot();
        using HttpClient catalogClient = new(http);
        using HttpClient downloadClient = new(http);
        PluginStore store = CreateStore(root, key, catalogClient, downloadClient, new FakeLauncher());

        Result<PluginStoreSnapshot> online = await store.RefreshAsync(true);
        online.IsSuccess.Should().BeTrue(online.ErrorMessage);
        online.Value.FromCache.Should().BeFalse();
        online.Value.Items.Should().ContainSingle();
        online.Value.Items[0].Catalog.Version.Should().Be("0.1.0");

        http.Failure = new HttpRequestException("offline");
        Result<PluginStoreSnapshot> offline = await store.RefreshAsync(true);
        offline.IsSuccess.Should().BeTrue(offline.ErrorMessage);
        offline.Value.FromCache.Should().BeTrue();
        offline.Value.Warning.Should().Contain("verified cache");

        string invalidRoot = NewRoot();
        using PluginStore invalid = CreateStore(invalidRoot, key, catalogClient, downloadClient, new FakeLauncher());
        http.Failure = null;
        http.Routes[IndexUrl] = Encoding.UTF8.GetBytes("{\"tampered\":true}");
        Result<PluginStoreSnapshot> rejected = await invalid.RefreshAsync(true);
        rejected.IsFailure.Should().BeTrue();
        PluginRegistry.Load(PluginRegistry.RegistryPath(invalidRoot)).Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Install_update_disable_enable_and_rollback_share_the_registry()
    {
        using SigningKey key = new();
        FakeHttpHandler http = new();
        SetRelease(http, key, "0.1.0", Package("0.1.0"));
        string root = NewRoot();
        FakeLauncher launcher = new();
        using HttpClient catalogClient = new(http);
        using HttpClient downloadClient = new(http);
        using PluginStore store = CreateStore(root, key, catalogClient, downloadClient, launcher);
        (await store.RefreshAsync(true)).IsSuccess.Should().BeTrue();

        Result<PluginDefinition> installed = await store.InstallAsync(PluginId);
        installed.IsSuccess.Should().BeTrue(installed.ErrorMessage);
        launcher.Starts.Should().Be(1);
        PluginRegistryEntry current = PluginRegistry.Load(PluginRegistry.RegistryPath(root)).Entries.Single();
        current.Source.Should().Be("community");
        current.SourceRevision.Should().Be(new string('a', 40));
        current.AllowedVerbs.Should().BeEquivalentTo("find", "fetch");

        (await store.SetEnabledAsync(PluginId, false)).IsSuccess.Should().BeTrue();
        PluginRegistry.Load(PluginRegistry.RegistryPath(root)).Entries.Should().BeEmpty();
        PluginRegistry.Load(PluginRegistry.RegistryPath(root), true).Entries.Single().Enabled.Should().BeFalse();
        (await store.SetEnabledAsync(PluginId, true)).IsSuccess.Should().BeTrue();

        SetRelease(http, key, "0.2.0", Package("0.2.0"));
        (await store.RefreshAsync(true)).IsSuccess.Should().BeTrue();
        Result<PluginStoreSnapshot> beforeUpdate = await store.RefreshAsync(false);
        beforeUpdate.Value.Items.Single().UpdateAvailable.Should().BeTrue();
        (await store.InstallAsync(PluginId)).IsSuccess.Should().BeTrue();
        Result<PluginStoreSnapshot> updated = await store.RefreshAsync(false);
        updated.Value.Items.Single().InstalledVersion.Should().Be("0.2.0");
        updated.Value.Items.Single().RollbackVersion.Should().Be("0.1.0");

        Result<PluginDefinition> rollback = await store.ActivateAsync(PluginId, "0.1.0");
        rollback.IsSuccess.Should().BeTrue(rollback.ErrorMessage);
        PluginManifestLoader.Load(PluginRegistry.Load(PluginRegistry.RegistryPath(root)).Entries.Single()
            .ManifestPath).Value.Version.Should().Be("0.1.0");
        launcher.Starts.Should().Be(3);
        Result<PluginStoreSnapshot> rolledBack = await store.RefreshAsync(false);
        rolledBack.Value.Items.Single().RollbackVersion.Should().BeNull();
        (await store.ActivateAsync(PluginId, "0.2.0")).IsSuccess.Should().BeTrue();
        launcher.Starts.Should().Be(4);
    }

    [Fact]
    public async Task Hash_mismatch_and_zip_traversal_never_register_or_launch()
    {
        using SigningKey key = new();
        FakeHttpHandler http = new();
        byte[] package = Package("0.1.0");
        SetRelease(http, key, "0.1.0", package);
        string root = NewRoot();
        FakeLauncher launcher = new();
        using HttpClient catalogClient = new(http);
        using HttpClient downloadClient = new(http);
        using PluginStore store = CreateStore(root, key, catalogClient, downloadClient, launcher);
        (await store.RefreshAsync(true)).IsSuccess.Should().BeTrue();

        byte[] changed = package.ToArray();
        changed[^1] ^= 1;
        http.Routes[ArtifactUrl] = changed;
        Result<PluginDefinition> badHash = await store.InstallAsync(PluginId);
        badHash.IsFailure.Should().BeTrue();
        badHash.ErrorMessage.Should().Contain("SHA-256");
        launcher.Starts.Should().Be(0);
        PluginRegistry.Load(PluginRegistry.RegistryPath(root)).Entries.Should().BeEmpty();

        byte[] traversal = Package("0.1.0", "../escape.txt");
        SetRelease(http, key, "0.1.0", traversal);
        (await store.RefreshAsync(true)).IsSuccess.Should().BeTrue();
        Result<PluginDefinition> badPath = await store.InstallAsync(PluginId);
        badPath.IsFailure.Should().BeTrue();
        badPath.ErrorMessage.Should().Contain("Unsafe ZIP entry");
        launcher.Starts.Should().Be(0);
        PluginRegistry.Load(PluginRegistry.RegistryPath(root)).Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Uninstall_removes_every_version_and_the_registration()
    {
        using SigningKey key = new();
        FakeHttpHandler http = new();
        SetRelease(http, key, "0.1.0", Package("0.1.0"));
        string root = NewRoot();
        FakeLauncher launcher = new();
        using HttpClient catalogClient = new(http);
        using HttpClient downloadClient = new(http);
        List<(int ManagedRecords, int EnabledManaged, bool PackagePresent)> callbackStates = [];
        using PluginStore store = CreateStore(root, key, catalogClient, downloadClient, launcher,
            pluginId =>
            {
                PluginRegistrySnapshot registry =
                    PluginRegistry.Load(PluginRegistry.RegistryPath(root), true);
                callbackStates.Add((
                    registry.Entries.Count(entry =>
                        entry.ManifestPath.Contains(pluginId, StringComparison.Ordinal)),
                    registry.Entries.Count(entry =>
                        entry.ManifestPath.Contains(pluginId, StringComparison.Ordinal) &&
                        entry.Enabled),
                    Directory.Exists(Path.Combine(root, "plugins", pluginId, "0.2.0"))));
                return Task.CompletedTask;
            });
        (await store.RefreshAsync(true)).IsSuccess.Should().BeTrue();
        (await store.InstallAsync(PluginId)).IsSuccess.Should().BeTrue();
        SetRelease(http, key, "0.2.0", Package("0.2.0"));
        (await store.RefreshAsync(true)).IsSuccess.Should().BeTrue();
        (await store.InstallAsync(PluginId)).IsSuccess.Should().BeTrue();
        Directory.Exists(Path.Combine(root, "plugins", PluginId, "0.1.0")).Should().BeTrue();
        Directory.Exists(Path.Combine(root, "plugins", PluginId, "0.2.0")).Should().BeTrue();

        string foreignRoot = Path.Combine(root, "external-plugin");
        WriteManifest(foreignRoot, "other.dev-plugin", "9.9.9");
        string registryPath = PluginRegistry.RegistryPath(root);
        List<PluginRegistryEntry> entries = PluginRegistry.Load(registryPath, true).Entries.ToList();
        entries.Add(new PluginRegistryEntry(
            Path.Combine(foreignRoot, PluginManifestLoader.ManifestFileName), true, ["fetch"]));
        PluginRegistry.Save(registryPath, entries);
        string stray = Path.Combine(root, "plugins", PluginId, "operator-notes.txt");
        File.WriteAllText(stray, "keep me");

        Result uninstalled = await store.UninstallAsync(PluginId);

        uninstalled.IsSuccess.Should().BeTrue(uninstalled.ErrorMessage);
        callbackStates.Should().HaveCount(2);
        callbackStates[0].Should().Be((1, 0, true));
        callbackStates[1].Should().Be((0, 0, false));
        Directory.Exists(Path.Combine(root, "plugins", PluginId, "0.1.0")).Should().BeFalse();
        Directory.Exists(Path.Combine(root, "plugins", PluginId, "0.2.0")).Should().BeFalse();
        File.Exists(stray).Should().BeTrue("uninstall never deletes unknown siblings");
        Directory.Exists(foreignRoot).Should().BeTrue();
        PluginRegistryEntry surviving = PluginRegistry.Load(registryPath, true).Entries.Should()
            .ContainSingle().Which;
        PluginManifestLoader.Load(surviving.ManifestPath).Value.Id.Should().Be("other.dev-plugin");

        Result<PluginStoreSnapshot> snapshot = await store.RefreshAsync(false);
        snapshot.IsSuccess.Should().BeTrue(snapshot.ErrorMessage);
        PluginStoreItem item = snapshot.Value.Items.Single();
        item.InstalledVersion.Should().BeNull();
        item.IsManaged.Should().BeFalse();
        item.InstalledVersions.Should().BeEmpty();
        item.RollbackVersion.Should().BeNull();

        (await store.InstallAsync(PluginId)).IsSuccess.Should().BeTrue();
        PluginRegistry.Load(registryPath).Entries.Should().HaveCount(2);
    }

    [Fact]
    public async Task Uninstall_a_disabled_plugin_still_removes_everything()
    {
        using SigningKey key = new();
        FakeHttpHandler http = new();
        SetRelease(http, key, "0.1.0", Package("0.1.0"));
        string root = NewRoot();
        using HttpClient catalogClient = new(http);
        using HttpClient downloadClient = new(http);
        using PluginStore store = CreateStore(root, key, catalogClient, downloadClient, new FakeLauncher());
        (await store.RefreshAsync(true)).IsSuccess.Should().BeTrue();
        (await store.InstallAsync(PluginId)).IsSuccess.Should().BeTrue();
        (await store.SetEnabledAsync(PluginId, false)).IsSuccess.Should().BeTrue();

        Result uninstalled = await store.UninstallAsync(PluginId);

        uninstalled.IsSuccess.Should().BeTrue(uninstalled.ErrorMessage);
        Directory.Exists(Path.Combine(root, "plugins", PluginId)).Should().BeFalse();
        PluginRegistry.Load(PluginRegistry.RegistryPath(root), true).Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Uninstall_stops_the_running_plugin_and_revokes_its_mount()
    {
        using SigningKey key = new();
        FakeHttpHandler http = new();
        SetRelease(http, key, "0.1.0", Package("0.1.0"));
        string root = NewRoot();
        FakeLauncher launcher = new();
        using HttpClient catalogClient = new(http);
        using HttpClient downloadClient = new(http);
        await using HostPluginApi api = new(PluginRegistry.RegistryPath(root), launcher);
        Action boundRefresh = api.RefreshRegistry;
        using PluginStore store = CreateStore(root, key, catalogClient, downloadClient, launcher,
            _ => Task.Run(boundRefresh));
        (await store.RefreshAsync(true)).IsSuccess.Should().BeTrue();
        (await store.InstallAsync(PluginId)).IsSuccess.Should().BeTrue();

        api.Mounts.Should().ContainSingle(mount => mount.PluginId == PluginId);
        _ = await api.FindAsync(PluginId, "", "anything", 10, null);
        launcher.Processes.Should().HaveCount(2);
        launcher.Processes[1].HasExited.Should().BeFalse();

        Result uninstalled = await store.UninstallAsync(PluginId);

        uninstalled.IsSuccess.Should().BeTrue(uninstalled.ErrorMessage);
        launcher.Processes[1].HasExited.Should().BeTrue();
        api.Mounts.Should().BeEmpty();
        api.AllowsVerb(PluginId, "fetch").Should().BeFalse();
    }

    [Fact]
    public async Task Uninstall_refuses_dev_external_and_traversal_targets()
    {
        using SigningKey key = new();
        FakeHttpHandler http = new();
        SetRelease(http, key, "0.1.0", Package("0.1.0"));
        string root = NewRoot();
        using HttpClient catalogClient = new(http);
        using HttpClient downloadClient = new(http);
        using PluginStore store = CreateStore(root, key, catalogClient, downloadClient, new FakeLauncher());
        (await store.RefreshAsync(true)).IsSuccess.Should().BeTrue();
        (await store.InstallAsync(PluginId)).IsSuccess.Should().BeTrue();

        (await store.UninstallAsync("../escape")).IsFailure.Should().BeTrue();
        (await store.UninstallAsync("nested/id")).IsFailure.Should().BeTrue();
        (await store.UninstallAsync("")).IsFailure.Should().BeTrue();
        (await store.UninstallAsync("example.missing-plugin")).ErrorCode.Should()
            .Be(AppErrorCodes.NotFound);
        Directory.Exists(Path.Combine(root, "plugins", PluginId, "0.1.0")).Should().BeTrue();

        string registryPath = PluginRegistry.RegistryPath(root);
        string devRoot = Path.Combine(root, "dev-plugin");
        WriteManifest(devRoot, "dev.local-plugin", "1.0.0");
        List<PluginRegistryEntry> entries = PluginRegistry.Load(registryPath, true).Entries.ToList();
        entries.Add(new PluginRegistryEntry(
            Path.Combine(devRoot, PluginManifestLoader.ManifestFileName), true, ["fetch"]));
        PluginRegistry.Save(registryPath, entries);
        Result dev = await store.UninstallAsync("dev.local-plugin");
        dev.IsFailure.Should().BeTrue();
        dev.ErrorCode.Should().Be(AppErrorCodes.InvalidArgument);
        Directory.Exists(devRoot).Should().BeTrue();
        PluginRegistry.Load(registryPath, true).Entries.Should().HaveCount(2);

        string elsewhere = Path.Combine(root, "elsewhere");
        WriteManifest(elsewhere, "evil.external-plugin", "1.0.0");
        entries = PluginRegistry.Load(registryPath, true).Entries.ToList();
        entries.Add(new PluginRegistryEntry(
            Path.Combine(elsewhere, PluginManifestLoader.ManifestFileName),
            true, ["fetch"], "community", "https://example.test/x", new string('b', 40)));
        PluginRegistry.Save(registryPath, entries);
        Result external = await store.UninstallAsync("evil.external-plugin");
        external.IsFailure.Should().BeTrue();
        external.ErrorCode.Should().Be(AppErrorCodes.InvalidArgument);
        Directory.Exists(elsewhere).Should().BeTrue();
        PluginRegistry.Load(registryPath, true).Entries.Should().HaveCount(3);
    }

    [Fact]
    public async Task Uninstall_refuses_a_reparse_point_inside_the_package()
    {
        using SigningKey key = new();
        FakeHttpHandler http = new();
        SetRelease(http, key, "0.1.0", Package("0.1.0"));
        string root = NewRoot();
        using HttpClient catalogClient = new(http);
        using HttpClient downloadClient = new(http);
        using PluginStore store = CreateStore(root, key, catalogClient, downloadClient, new FakeLauncher());
        (await store.RefreshAsync(true)).IsSuccess.Should().BeTrue();
        (await store.InstallAsync(PluginId)).IsSuccess.Should().BeTrue();

        string external = Path.Combine(root, "outside-target");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "data.txt"), "external");
        string link = Path.Combine(root, "plugins", PluginId, "0.1.0", "linked");
        try
        {
            Directory.CreateSymbolicLink(link, external);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Result uninstalled = await store.UninstallAsync(PluginId);

        uninstalled.IsFailure.Should().BeTrue();
        uninstalled.ErrorMessage.Should().Contain("reparse");
        File.Exists(Path.Combine(external, "data.txt")).Should().BeTrue();
        Directory.Exists(Path.Combine(root, "plugins", PluginId, "0.1.0")).Should().BeTrue();
        PluginRegistry.Load(PluginRegistry.RegistryPath(root), true).Entries.Single()
            .Enabled.Should().BeTrue("a refused uninstall changes nothing");
    }

    [Fact]
    public async Task Uninstall_refuses_a_linked_registered_version_and_keeps_unknown_links()
    {
        using SigningKey key = new();
        FakeHttpHandler http = new();
        SetRelease(http, key, "0.1.0", Package("0.1.0"));
        string root = NewRoot();
        using HttpClient catalogClient = new(http);
        using HttpClient downloadClient = new(http);
        using PluginStore store = CreateStore(root, key, catalogClient, downloadClient, new FakeLauncher());
        (await store.RefreshAsync(true)).IsSuccess.Should().BeTrue();
        (await store.InstallAsync(PluginId)).IsSuccess.Should().BeTrue();

        string registryPath = PluginRegistry.RegistryPath(root);
        string external = Path.Combine(root, "linked-target");
        WriteManifest(external, PluginId, "9.9.9");
        File.WriteAllText(Path.Combine(external, ".patchouli-install.json"),
            "{\"repository\":\"https://example.test\",\"source_revision\":\"" + new string('a', 40) + "\"}");
        string link = Path.Combine(root, "plugins", PluginId, "9.9.9");
        string strayLink = Path.Combine(root, "plugins", PluginId, "random-link");
        try
        {
            Directory.CreateSymbolicLink(link, external);
            Directory.CreateSymbolicLink(strayLink, external);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        List<PluginRegistryEntry> entries = PluginRegistry.Load(registryPath, true).Entries.ToList();
        entries.Add(new PluginRegistryEntry(
            Path.Combine(link, PluginManifestLoader.ManifestFileName), true, ["fetch"],
            "community", "https://example.test/x", new string('a', 40)));
        PluginRegistry.Save(registryPath, entries);

        Result uninstalled = await store.UninstallAsync(PluginId);

        uninstalled.IsFailure.Should().BeTrue();
        uninstalled.ErrorMessage.Should().Contain("reparse");
        File.Exists(Path.Combine(external, PluginManifestLoader.ManifestFileName)).Should().BeTrue();
        Directory.Exists(Path.Combine(root, "plugins", PluginId, "0.1.0")).Should().BeTrue();
        PluginRegistry.Load(registryPath, true).Entries.Should().HaveCount(2);
    }

    [Fact]
    public async Task Uninstall_refuses_when_an_ancestor_is_a_reparse_point()
    {
        using SigningKey key = new();
        FakeHttpHandler http = new();
        SetRelease(http, key, "0.1.0", Package("0.1.0"));
        string root = NewRoot();
        string realPlugins = Path.Combine(root, "real-plugins");
        string linkedPlugins = Path.Combine(root, "plugins");
        try
        {
            Directory.CreateSymbolicLink(linkedPlugins, realPlugins);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        using HttpClient catalogClient = new(http);
        using HttpClient downloadClient = new(http);
        using PluginStore store = CreateStore(root, key, catalogClient, downloadClient, new FakeLauncher());
        string versionDirectory = Path.Combine(realPlugins, PluginId, "0.1.0");
        WriteManifest(versionDirectory, PluginId, "0.1.0");
        string registryPath = PluginRegistry.RegistryPath(root);
        PluginRegistry.Save(registryPath,
        [
            new PluginRegistryEntry(
                Path.Combine(linkedPlugins, PluginId, "0.1.0",
                    PluginManifestLoader.ManifestFileName),
                true, ["fetch"], "community", "https://example.test/x", new string('a', 40))
        ]);

        Result uninstalled = await store.UninstallAsync(PluginId);

        uninstalled.IsFailure.Should().BeTrue();
        uninstalled.ErrorMessage.Should().Contain("reparse");
        Directory.Exists(versionDirectory).Should().BeTrue();
        PluginRegistry.Load(registryPath, true).Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task Uninstall_refuses_when_the_callback_replaces_the_root_with_a_link()
    {
        using SigningKey key = new();
        FakeHttpHandler http = new();
        SetRelease(http, key, "0.1.0", Package("0.1.0"));
        string root = NewRoot();
        FakeLauncher launcher = new();
        using HttpClient catalogClient = new(http);
        using HttpClient downloadClient = new(http);
        string pluginRoot = Path.Combine(root, "plugins", PluginId);
        string backup = Path.Combine(root, "moved-plugin-root");
        string sentinel = Path.Combine(root, "sentinel-tree", PluginId);
        int calls = 0;
        using PluginStore store = CreateStore(root, key, catalogClient, downloadClient, launcher,
            _ =>
            {
                if (++calls == 1)
                {
                    Directory.Move(pluginRoot, backup);
                    Directory.CreateSymbolicLink(pluginRoot,
                        Path.Combine(root, "sentinel-tree", PluginId));
                }

                return Task.CompletedTask;
            });
        (await store.RefreshAsync(true)).IsSuccess.Should().BeTrue();
        (await store.InstallAsync(PluginId)).IsSuccess.Should().BeTrue();
        WriteManifest(Path.Combine(sentinel, "0.1.0"), PluginId, "0.1.0");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "probe"), sentinel);
            Directory.Delete(Path.Combine(root, "probe"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Result uninstalled = await store.UninstallAsync(PluginId);

        uninstalled.IsFailure.Should().BeTrue();
        uninstalled.ErrorMessage.Should().Contain("reparse");
        PluginRegistry.Load(PluginRegistry.RegistryPath(root), true).Entries.Single()
            .Enabled.Should().BeFalse("the disabled registration stays for a retry");
        Directory.Exists(Path.Combine(sentinel, "0.1.0")).Should().BeTrue();
        Directory.Exists(Path.Combine(backup, "0.1.0")).Should().BeTrue();
    }

    [Fact]
    public async Task Uninstall_rejects_a_record_whose_version_directory_is_not_semver()
    {
        using SigningKey key = new();
        FakeHttpHandler http = new();
        SetRelease(http, key, "0.1.0", Package("0.1.0"));
        string root = NewRoot();
        using HttpClient catalogClient = new(http);
        using HttpClient downloadClient = new(http);
        using PluginStore store = CreateStore(root, key, catalogClient, downloadClient, new FakeLauncher());
        (await store.RefreshAsync(true)).IsSuccess.Should().BeTrue();

        string corrupted = Path.Combine(root, "plugins", PluginId, "not-a-version");
        WriteManifest(corrupted, PluginId, "0.1.0");
        PluginRegistry.Save(PluginRegistry.RegistryPath(root),
        [
            new PluginRegistryEntry(
                Path.Combine(corrupted, PluginManifestLoader.ManifestFileName),
                true, ["fetch"], "community", "https://example.test/x", new string('a', 40))
        ]);

        Result<PluginStoreSnapshot> snapshot = await store.RefreshAsync(false);
        snapshot.IsSuccess.Should().BeTrue(snapshot.ErrorMessage);
        PluginStoreItem item = snapshot.Value.Items.Single();
        item.IsManaged.Should().BeFalse();
        _ = item.UpdateAvailable;

        Result uninstalled = await store.UninstallAsync(PluginId);
        uninstalled.IsFailure.Should().BeTrue();
        uninstalled.ErrorCode.Should().Be(AppErrorCodes.InvalidArgument);
        Directory.Exists(corrupted).Should().BeTrue();
        PluginRegistry.Load(PluginRegistry.RegistryPath(root), true).Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task Uninstall_works_offline_and_makes_no_catalog_calls()
    {
        using SigningKey key = new();
        FakeHttpHandler http = new();
        SetRelease(http, key, "0.1.0", Package("0.1.0"));
        string root = NewRoot();
        using HttpClient catalogClient = new(http);
        using HttpClient downloadClient = new(http);
        using PluginStore store = CreateStore(root, key, catalogClient, downloadClient, new FakeLauncher());
        (await store.RefreshAsync(true)).IsSuccess.Should().BeTrue();
        (await store.InstallAsync(PluginId)).IsSuccess.Should().BeTrue();

        int requests = http.Requests;
        http.Failure = new HttpRequestException("offline");
        Result uninstalled = await store.UninstallAsync(PluginId);

        uninstalled.IsSuccess.Should().BeTrue(uninstalled.ErrorMessage);
        http.Requests.Should().Be(requests);
        Directory.Exists(Path.Combine(root, "plugins", PluginId)).Should().BeFalse();
    }

    [Fact]
    public async Task Uninstall_cancelled_before_mutation_changes_nothing()
    {
        using SigningKey key = new();
        FakeHttpHandler http = new();
        SetRelease(http, key, "0.1.0", Package("0.1.0"));
        string root = NewRoot();
        using HttpClient catalogClient = new(http);
        using HttpClient downloadClient = new(http);
        using PluginStore store = CreateStore(root, key, catalogClient, downloadClient, new FakeLauncher());
        (await store.RefreshAsync(true)).IsSuccess.Should().BeTrue();
        (await store.InstallAsync(PluginId)).IsSuccess.Should().BeTrue();

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        Result result = await store.UninstallAsync(PluginId, cancelled.Token);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("cancelled");
        Directory.Exists(Path.Combine(root, "plugins", PluginId, "0.1.0")).Should().BeTrue();
        PluginRegistry.Load(PluginRegistry.RegistryPath(root), true).Entries.Single()
            .Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Uninstall_io_failure_leaves_a_disabled_record_and_retry_completes()
    {
        using SigningKey key = new();
        FakeHttpHandler http = new();
        SetRelease(http, key, "0.1.0", Package("0.1.0"));
        string root = NewRoot();
        using HttpClient catalogClient = new(http);
        using HttpClient downloadClient = new(http);
        using PluginStore store = CreateStore(root, key, catalogClient, downloadClient, new FakeLauncher());
        (await store.RefreshAsync(true)).IsSuccess.Should().BeTrue();
        (await store.InstallAsync(PluginId)).IsSuccess.Should().BeTrue();

        string locked = Path.Combine(root, "plugins", PluginId, "0.1.0", "README.md");
        await using FileStream handle = new(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        Result failed = await store.UninstallAsync(PluginId);

        failed.IsFailure.Should().BeTrue();
        failed.ErrorMessage.Should().Contain("cleanup is incomplete");
        PluginRegistryEntry record = PluginRegistry.Load(PluginRegistry.RegistryPath(root), true)
            .Entries.Single();
        record.Enabled.Should().BeFalse("the grant stays revoked across the failed cleanup");
        Directory.Exists(Path.Combine(root, "plugins", PluginId, "0.1.0")).Should().BeTrue();

        handle.Dispose();
        Result retried = await store.UninstallAsync(PluginId);

        retried.IsSuccess.Should().BeTrue(retried.ErrorMessage);
        Directory.Exists(Path.Combine(root, "plugins", PluginId)).Should().BeFalse();
        PluginRegistry.Load(PluginRegistry.RegistryPath(root), true).Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Uninstall_retries_after_a_damaged_or_missing_manifest()
    {
        using SigningKey key = new();
        FakeHttpHandler http = new();
        SetRelease(http, key, "0.1.0", Package("0.1.0"));
        string root = NewRoot();
        using HttpClient catalogClient = new(http);
        using HttpClient downloadClient = new(http);
        using PluginStore store = CreateStore(root, key, catalogClient, downloadClient, new FakeLauncher());
        (await store.RefreshAsync(true)).IsSuccess.Should().BeTrue();
        (await store.InstallAsync(PluginId)).IsSuccess.Should().BeTrue();

        File.Delete(Path.Combine(root, "plugins", PluginId, "0.1.0",
            PluginManifestLoader.ManifestFileName));

        Result<PluginStoreSnapshot> snapshot = await store.RefreshAsync(false);
        snapshot.IsSuccess.Should().BeTrue(snapshot.ErrorMessage);
        PluginStoreItem item = snapshot.Value.Items.Single();
        item.IsManaged.Should().BeTrue();
        item.InstalledVersion.Should().Be("0.1.0");

        Result uninstalled = await store.UninstallAsync(PluginId);

        uninstalled.IsSuccess.Should().BeTrue(uninstalled.ErrorMessage);
        Directory.Exists(Path.Combine(root, "plugins", PluginId)).Should().BeFalse();
        PluginRegistry.Load(PluginRegistry.RegistryPath(root), true).Entries.Should().BeEmpty();
    }

    private static void WriteManifest(string directory, string pluginId, string version)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, PluginManifestLoader.ManifestFileName), $$"""
              schema = "patchouli.plugin/1"
              host-api = "patchouli.plugins/1"
              id = "{{pluginId}}"
              version = "{{version}}"
              name = "Fixture plugin"
              description = "Fixture plugin"
              readme = "README.md"

              [runtime]
              transport = "stdio"
              command = "fake"
              args = []

              [library]
              grants = []

              [[vfs.nodes]]
              path = ""
              kind = "directory"
              verbs = ["find"]
              """);
        File.WriteAllText(Path.Combine(directory, "README.md"), "# Fixture\n");
    }

    private static PluginStore CreateStore(string root, SigningKey key, HttpClient catalog, HttpClient download,
        IPluginProcessLauncher launcher, Func<string, Task>? registryChanged = null,
        DownloadTaskService? downloads = null)
    {
        return new PluginStore(catalog, download, new PluginStoreOptions
        {
            DataDirectory = root,
            IndexUrl = IndexUrl,
            SignatureUrl = SignatureUrl,
            PublicKeys = [key.PublicKey],
            CurrentPlatform = "win-x64"
        }, launcher, registryChanged: registryChanged, downloads: downloads);
    }

    private static void SetRelease(FakeHttpHandler http, SigningKey key, string version, byte[] package)
    {
        byte[] index = Index(version, package);
        http.Routes[IndexUrl] = index;
        http.Routes[SignatureUrl] = Encoding.UTF8.GetBytes(key.Sign(index));
        http.Routes[ArtifactUrl] = package;
    }

    private static byte[] Index(string version, byte[] package)
    {
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "patchouli.community.index/1",
            generated_at = "2026-11-07T00:00:00Z",
            plugins = new[]
            {
                new
                {
                    id = PluginId,
                    name = "Test plugin",
                    description = "Integration fixture",
                    version,
                    license = "MIT",
                    repository = "https://github.com/example/plugin",
                    source_revision = new string('a', 40),
                    requested_verbs = new[] { "find", "fetch" },
                    library_grants = Array.Empty<string>(),
                    artifacts = new[]
                    {
                        new
                        {
                            platform = "win-x64",
                            url = ArtifactUrl,
                            size = package.Length,
                            sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(package))
                                .ToLowerInvariant()
                        }
                    }
                }
            }
        });
    }

    private static byte[] Package(string version, string? extraPath = null)
    {
        using MemoryStream output = new();
        using (ZipArchive archive = new(output, ZipArchiveMode.Create, true))
        {
            Add(archive, "patchouli-plugin.toml", $$"""
                                                    schema = "patchouli.plugin/1"
                                                    host-api = "patchouli.plugins/1"
                                                    id = "example.test-plugin"
                                                    version = "{{version}}"
                                                    name = "Test plugin"
                                                    description = "Integration fixture"
                                                    readme = "README.md"

                                                    [runtime]
                                                    transport = "stdio"
                                                    command = "fake"
                                                    args = []

                                                    [library]
                                                    grants = []

                                                    [[vfs.nodes]]
                                                    path = ""
                                                    kind = "directory"
                                                    verbs = ["find", "fetch"]
                                                    summary = "root"

                                                    [[vfs.nodes]]
                                                    path = "status.json"
                                                    kind = "file"
                                                    verbs = ["fetch"]
                                                    media = "application/json"
                                                    schema = "schemas/status.json"
                                                    """);
            Add(archive, "README.md", "# Test plugin\n");
            Add(archive, "schemas/status.json",
                """{"type":"object","required":["ok"],"properties":{"ok":{"type":"boolean"}}}""");
            if (extraPath is not null)
            {
                Add(archive, extraPath, "escape");
            }
        }

        return output.ToArray();
    }

    private static void Add(ZipArchive archive, string path, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path);
        using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string NewRoot()
    {
        string path = Path.Combine(RepositoryTestEnvironment.TestRoot, "plugin-store", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class SigningKey : IDisposable
    {
        private readonly Key _key = Key.Create(SignatureAlgorithm.Ed25519);
        private readonly byte[] _keyId = [1, 2, 3, 4, 5, 6, 7, 8];

        public string PublicKey { get; private set; } = "";

        public SigningKey()
        {
            PublicKey = Convert.ToBase64String("Ed"u8.ToArray().Concat(_keyId)
                .Concat(_key.PublicKey.Export(KeyBlobFormat.RawPublicKey)).ToArray());
        }

        public string Sign(byte[] content)
        {
            byte[] digest = HashAlgorithm.Blake2b_512.Hash(content);
            byte[] signature = SignatureAlgorithm.Ed25519.Sign(_key, digest);
            const string trusted = "timestamp:1\tfile:index.json\thashed";
            byte[] global = SignatureAlgorithm.Ed25519.Sign(_key,
                signature.Concat(Encoding.UTF8.GetBytes(trusted)).ToArray());
            return "untrusted comment: test signature\n" +
                   Convert.ToBase64String("ED"u8.ToArray().Concat(_keyId).Concat(signature).ToArray()) +
                   "\ntrusted comment: " + trusted + "\n" + Convert.ToBase64String(global) + "\n";
        }

        public void Dispose()
        {
            _key.Dispose();
        }
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Routes { get; } = new(StringComparer.Ordinal);
        public HttpRequestException? Failure { get; set; }
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            if (Failure is not null)
            {
                throw Failure;
            }

            string url = request.RequestUri!.AbsoluteUri;
            if (!Routes.TryGetValue(url, out byte[]? content))
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            }

            HttpResponseMessage response = new(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            };
            return Task.FromResult(response);
        }
    }

    private sealed class FakeLauncher : IPluginProcessLauncher
    {
        public int Starts { get; private set; }
        public List<FakeProcess> Processes { get; } = [];

        public IPluginChildProcess Start(PluginProcessSpec spec)
        {
            Starts++;
            FakeProcess process = new();
            Processes.Add(process);
            return process;
        }
    }

    private sealed class FakeProcess : IPluginChildProcess
    {
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Stream _standardInput;
        private readonly Stream _standardOutput;
        private readonly MemoryStream _standardError = new();
        private readonly Task _server;
        private int _hasExited;

        public FakeProcess()
        {
            _standardInput = _input.Writer.AsStream();
            _standardOutput = _output.Reader.AsStream();
            _server = Task.Run(ServeAsync);
        }

        public Stream StandardInput => _standardInput;
        public Stream StandardOutput => _standardOutput;
        public Stream StandardError => _standardError;
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
                using StreamReader reader = new(_input.Reader.AsStream(), Encoding.UTF8, false, 4096, true);
                await using Stream writer = _output.Writer.AsStream();
                while (await reader.ReadLineAsync(_lifetime.Token) is { } line)
                {
                    using JsonDocument request = JsonDocument.Parse(line);
                    JsonElement root = request.RootElement;
                    if (!root.TryGetProperty("id", out JsonElement id))
                    {
                        continue;
                    }

                    string method = root.GetProperty("method").GetString()!;
                    object result = method == "initialize"
                        ? new { protocolVersion = "2025-06-18", capabilities = new { } }
                        : new
                        {
                            tools = PluginStdioClient.RequiredTools.Select(name => new { name }).ToArray()
                        };
                    string response = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
                    await writer.WriteAsync(Encoding.UTF8.GetBytes(response + "\n"), _lifetime.Token);
                    await writer.FlushAsync(_lifetime.Token);
                }
            }
            catch (OperationCanceledException)
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
            _standardInput.Dispose();
            _standardOutput.Dispose();
            _standardError.Dispose();
            _lifetime.Dispose();
            _ = _server.Exception;
        }
    }
}
