using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Host.Downloads;
using Patchouli.Host.ManagedFiles;
using Patchouli.Infrastructure.Rendering;
using Patchouli.Llm;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.UI.ViewModels.Settings;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

/// <remarks>
/// All local-file management tests run against real FSX declarations only — no network, no
/// downloads; planted files live inside the test environment's own roots.
/// </remarks>
[Collection("Avalonia")]
public sealed class LocalFileManagementSettingsTests
{
    [Fact]
    public async Task Layout_models_use_the_same_download_controls_and_managed_directories_as_other_local_models()
    {
        using TemporaryAppSettingsFile settings = new();
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<bool>(async () =>
        {
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            LocalFileManagementSettingsViewModel management = main.Settings.LocalFileManagement;
            await management.LoadAsync();
            ManagedLocationViewModel[] models = management.Locations.Where(row =>
                row.DownloadKind is ModelDownloadKind.DocLayoutYolo or ModelDownloadKind.PpDocLayoutV3).ToArray();

            models.Should().HaveCount(2);
            foreach (ManagedLocationViewModel model in models)
            {
                model.Path.Should().StartWith(services.OcrStorage.ModelsRoot + Path.DirectorySeparatorChar);
                model.CanDownload.Should().BeTrue();
                model.CanClear.Should().BeTrue();
            }

            string yoloPath = services.OcrStorage.GetLayoutModelPath(OcrLayoutModelIds.DocLayoutYolo);
            models.Single(row => row.DownloadKind == ModelDownloadKind.DocLayoutYolo).Path.Should()
                .Be(Path.GetDirectoryName(yoloPath));
            string paddlePath = services.OcrStorage.GetLayoutModelPath(OcrLayoutModelIds.PpDocLayoutV3);
            models.Single(row => row.DownloadKind == ModelDownloadKind.PpDocLayoutV3).Path.Should()
                .Be(Path.GetDirectoryName(paddlePath));
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Clearing_a_declaration_group_deletes_only_declared_targets()
    {
        using TemporaryAppSettingsFile settings = new();
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<bool>(async () =>
        {
            ConfirmDialogs dialogs = new();
            await using MainWindowViewModel main = new(dialogs: dialogs,
                settingsPath: settings.Path, appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            LocalFileManagementSettingsViewModel management = main.Settings.LocalFileManagement;
            await management.LoadAsync();
            ManagedLocationViewModel row = management.Locations.First(location =>
                location.Group is { Files.Count: > 0 });

            // Plant one real declared target plus unrelated user content in the same directory.
            string declaredPath = row.Group!.Files[0].DestinationPath;
            Directory.CreateDirectory(row.Path);
            await File.WriteAllBytesAsync(declaredPath, [1]);
            string unrelated = Path.Combine(row.Path, "my-notes.txt");
            await File.WriteAllTextAsync(unrelated, "keep");
            string subfolder = Path.Combine(row.Path, "user-subdir");
            Directory.CreateDirectory(subfolder);
            await File.WriteAllTextAsync(Path.Combine(subfolder, "nested.txt"), "keep");

            await management.ClearLocationAsync(row);

            File.Exists(declaredPath).Should()
                .BeFalse("the confirmed declared target was deleted");
            File.Exists(unrelated).Should()
                .BeTrue("declaration-driven clear never touches unrelated user files");
            Directory.Exists(subfolder).Should()
                .BeTrue("no directory is ever recursively removed");
            File.Exists(Path.Combine(subfolder, "nested.txt")).Should().BeTrue();
            ConfirmDialogViewModel confirm = dialogs.Shown.OfType<ConfirmDialogViewModel>()
                .Should().ContainSingle().Subject;
            confirm.Message.Should().Contain(row.Group.Files[0].RelativePath,
                "the prompt lists the declared targets, not the whole folder");
            confirm.Message.Should().NotContain("my-notes.txt");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_stale_context_after_the_clear_prompt_never_deletes()
    {
        using TemporaryAppSettingsFile settings = new();
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<bool>(async () =>
        {
            SwitchingDialogs dialogs = new();
            await using MainWindowViewModel main = new(dialogs: dialogs,
                settingsPath: settings.Path, appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            LocalFileManagementSettingsViewModel management = main.Settings.LocalFileManagement;
            await management.LoadAsync();
            ManagedLocationViewModel row = management.Locations.First(location =>
                location.Group is { Files.Count: > 0 });
            string declaredPath = row.Group!.Files[0].DestinationPath;
            Directory.CreateDirectory(row.Path);
            await File.WriteAllBytesAsync(declaredPath, [1]);

            // The library switches while the confirmation is open — the captured context is
            // stale and nothing may be deleted.
            MainWindowViewModel stale = main;
            TemporaryAppSettingsFile switched = settings;
            dialogs.Switch = async () =>
            {
                stale.RuntimeDatabasePath = switched.CreateDatabasePath("switched");
                await stale.OpenDatabaseCommand.ExecuteAsync();
            };
            await management.ClearLocationAsync(row);

            File.Exists(declaredPath).Should()
                .BeTrue("a stale confirmation context must never authorize a delete");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public void Declaration_driven_clear_targets_only_declared_files_in_a_shared_directory()
    {
        string shared = Path.Combine(Path.GetTempPath(), $"managed-clear-{Guid.NewGuid():N}");
        Directory.CreateDirectory(shared);
        try
        {
            string declaredA = Path.Combine(shared, "a.onnx");
            string declaredB = Path.Combine(shared, "b.onnx");
            string unrelated = Path.Combine(shared, "user-notes.txt");
            string subfolder = Path.Combine(shared, "extras");
            File.WriteAllBytes(declaredA, [1]);
            File.WriteAllBytes(declaredB, [2]);
            File.WriteAllText(unrelated, "keep");
            Directory.CreateDirectory(subfolder);
            File.WriteAllText(Path.Combine(subfolder, "nested.txt"), "keep");

            // Two owners contribute declarations into the same directory — the merged
            // group's eligible set is still exactly the union of declared targets.
            ManagedInventoryGroup group = new("Shared", null, shared,
                [
                    new ManagedFileDownload("a.onnx", "https://example.test/a", declaredA),
                    new ManagedFileDownload("b.onnx", "https://example.test/b", declaredB)
                ],
                []);

            IReadOnlyList<LocalFileManagementSettingsViewModel.DeclaredTarget> targets =
                LocalFileManagementSettingsViewModel.DeclaredExistingTargets(group);
            targets.Select(target => target.Path).Should()
                .BeEquivalentTo(declaredA, declaredB);
            targets.Select(target => target.Path).Should()
                .NotContain(unrelated, "an arbitrary user file in a shared folder is not a target");

            int deleted = LocalFileManagementSettingsViewModel.DeleteDeclaredTargets(targets);
            deleted.Should().Be(2);
            File.Exists(declaredA).Should().BeFalse();
            File.Exists(declaredB).Should().BeFalse();
            File.Exists(unrelated).Should().BeTrue();
            Directory.Exists(subfolder).Should().BeTrue();
            File.Exists(Path.Combine(subfolder, "nested.txt")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(shared, true);
        }
    }

    [Fact]
    public void Declared_clear_refuses_a_replaced_user_file_even_if_size_and_timestamp_are_preserved()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
            $"declared-clear-replaced-{Guid.NewGuid():N}")).FullName;
        try
        {
            string target = Path.Combine(root, "model.onnx");
            File.WriteAllBytes(target, [1, 2, 3, 4]);
            DateTime writeTime = File.GetLastWriteTimeUtc(target);
            ManagedInventoryGroup group = new("Test", null, root,
                [new ManagedFileDownload("model.onnx", "https://example.test/model", target)], []);
            IReadOnlyList<LocalFileManagementSettingsViewModel.DeclaredTarget> confirmed =
                LocalFileManagementSettingsViewModel.DeclaredExistingTargets(group);
            File.WriteAllBytes(target, [4, 3, 2, 1]);
            File.SetLastWriteTimeUtc(target, writeTime);

            LocalFileManagementSettingsViewModel.DeleteDeclaredTargets(confirmed).Should().Be(0);

            File.ReadAllBytes(target).Should().Equal(4, 3, 2, 1);
        }
        finally
        {
            TestTempFileCleanup.DeleteDirectoryWithRetry(root);
        }
    }

    [Fact]
    public async Task Registered_plugin_declarations_appear_as_a_unified_inventory_row()
    {
        using TemporaryAppSettingsFile settings = new();
        string root = Path.GetDirectoryName(settings.Path)!;
        TestAppPaths appPaths = new(root);
        RegisterPlugin(root, PluginManifest(true));
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<bool>(async () =>
        {
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: appPaths);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            LocalFileManagementSettingsViewModel management = main.Settings.LocalFileManagement;
            await management.LoadAsync();

            string managedRoot = services.PluginFiles.ManagedRootFor(TestPluginId);
            ManagedLocationViewModel row = management.Locations.Should()
                .ContainSingle(location => string.Equals(location.Path, managedRoot,
                    StringComparison.OrdinalIgnoreCase)).Subject;
            row.Group.Should().NotBeNull();
            row.Name.Should().Contain("Classifier");
            // Both nested declarations stay in the single root row; nothing is created on disk.
            row.Group!.Files.Select(file => file.RelativePath).Should().BeEquivalentTo(
                "models/layout.onnx", "models/vocab.txt");
            row.Group.Missing.Should().HaveCount(2);
            row.CanDownload.Should().BeTrue();
            row.CanClear.Should().BeTrue("declaration rows are individually clearable");
            Directory.Exists(managedRoot).Should().BeFalse(
                "inventory reads never create managed directories");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Disabled_and_invalid_plugins_never_appear_in_the_inventory()
    {
        using TemporaryAppSettingsFile settings = new();
        string root = Path.GetDirectoryName(settings.Path)!;
        TestAppPaths appPaths = new(root);
        const string disabledId = "test.disabled";
        const string noFilesId = "test.no-files";
        const string noGrantId = "test.no-grant";
        string valid = WritePluginPackage(root, "valid", PluginManifest());
        string disabled = WritePluginPackage(root, "disabled", PluginManifest(id: disabledId));
        string broken = WritePluginPackage(root, "broken", "not = [a valid manifest\n");
        string noFiles = WritePluginPackage(root, "nofiles",
            PluginManifest(id: noFilesId, includeFiles: false));
        string noGrant = WritePluginPackage(root, "nogrant", PluginManifest(id: noGrantId));
        WriteRegistry(root,
            (Path.Combine(valid, Host.Plugins.PluginManifestLoader.ManifestFileName), true,
                ["find", "fetch"]),
            (Path.Combine(disabled, Host.Plugins.PluginManifestLoader.ManifestFileName), false,
                ["find", "fetch"]),
            (Path.Combine(broken, Host.Plugins.PluginManifestLoader.ManifestFileName), true,
                ["find", "fetch"]),
            (Path.Combine(noFiles, Host.Plugins.PluginManifestLoader.ManifestFileName), true,
                ["find", "fetch"]),
            (Path.Combine(noGrant, Host.Plugins.PluginManifestLoader.ManifestFileName), true, []));
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<bool>(async () =>
        {
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: appPaths);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            LocalFileManagementSettingsViewModel management = main.Settings.LocalFileManagement;
            await management.LoadAsync();

            bool HasRow(string pluginId)
            {
                return management.Locations.Any(location =>
                    location.Path.StartsWith(services.PluginFiles.ManagedRootFor(pluginId),
                        StringComparison.OrdinalIgnoreCase));
            }

            HasRow(TestPluginId).Should().BeTrue("the enabled valid plugin is listed");
            HasRow(disabledId).Should().BeFalse("a disabled plugin is never listed");
            HasRow(noFilesId).Should().BeFalse(
                "a plugin declaring no managed files produces no row");
            HasRow(noGrantId).Should().BeTrue(
                "inventory must not require runtime verb grants — managing host files is not " +
                "plugin execution");
            management.Locations.Count(location =>
                    location.Path.StartsWith(
                        Path.GetDirectoryName(services.PluginFiles.ManagedRootFor("x"))!,
                        StringComparison.OrdinalIgnoreCase))
                .Should().Be(2,
                    "only the enabled valid plugin and the no-grant plugin produce rows — " +
                    "disabled and invalid manifests never reach the inventory");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Declining_a_plugin_download_writes_nothing()
    {
        using TemporaryAppSettingsFile settings = new();
        string root = Path.GetDirectoryName(settings.Path)!;
        TestAppPaths appPaths = new(root);
        RegisterPlugin(root, PluginManifest(true));
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<bool>(async () =>
        {
            ConfirmDialogs dialogs = new() { Result = ConfirmDialogResult.Cancel };
            await using MainWindowViewModel main = new(dialogs: dialogs,
                settingsPath: settings.Path, appPaths: appPaths);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            LocalFileManagementSettingsViewModel management = main.Settings.LocalFileManagement;
            await management.LoadAsync();
            ManagedLocationViewModel row = management.Locations.Single(location =>
                location.Path.Equals(services.PluginFiles.ManagedRootFor(TestPluginId),
                    StringComparison.OrdinalIgnoreCase));

            await management.DownloadModelsAsync(row);

            services.DownloadTasks.List().Should().BeEmpty("a declined prompt registers no task");
            Directory.Exists(row.Path).Should().BeFalse("declining writes nothing to disk");
            dialogs.Shown.OfType<ConfirmDialogViewModel>().Should().ContainSingle();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Confirmed_plugin_download_installs_verified_bytes_and_registers_one_task()
    {
        using TemporaryAppSettingsFile settings = new();
        string root = Path.GetDirectoryName(settings.Path)!;
        TestAppPaths appPaths = new(root);
        RegisterPlugin(root, PluginManifest(true, true));
        byte[] payload = "payload"u8.ToArray();
        HostInfrastructureOptions infrastructure = new()
        {
            HttpHandlerFactory = _ => new PluginBytesHandler(payload)
        };
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<bool>(async () =>
        {
            ConfirmDialogs dialogs = new();
            await using MainWindowViewModel main = new(dialogs: dialogs,
                settingsPath: settings.Path, appPaths: appPaths, infrastructure: infrastructure);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            LocalFileManagementSettingsViewModel management = main.Settings.LocalFileManagement;
            await management.LoadAsync();
            ManagedLocationViewModel row = management.Locations.Single(location =>
                location.Path.Equals(services.PluginFiles.ManagedRootFor(TestPluginId),
                    StringComparison.OrdinalIgnoreCase));

            await management.DownloadModelsAsync(row);

            File.ReadAllBytes(Path.Combine(row.Path, "models", "layout.onnx")).Should()
                .Equal(payload, "the confirmed download installs verified bytes");
            File.ReadAllBytes(Path.Combine(row.Path, "models", "vocab.txt")).Should()
                .Equal(payload, "every declared file downloads, not only the first");
            DownloadTaskStatus task = services.DownloadTasks.List().Should().ContainSingle(
                "the confirmed download registers exactly one DownloadTask").Subject;
            task.State.Should().Be(DownloadTaskService.SucceededState,
                "a fully satisfiable declaration set completes successfully");
            management.Status.Should().Contain("下载完成");
            row.ItemCount.Should().BeGreaterThan(0, "a successful download refreshes the row stats");
            row.SizeBytes.Should().BeGreaterThan(0);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_plugin_declaration_without_a_download_url_fails_actionably()
    {
        using TemporaryAppSettingsFile settings = new();
        string root = Path.GetDirectoryName(settings.Path)!;
        TestAppPaths appPaths = new(root);
        RegisterPlugin(root, PluginManifest(true));
        byte[] payload = "payload"u8.ToArray();
        HostInfrastructureOptions infrastructure = new()
        {
            HttpHandlerFactory = _ => new PluginBytesHandler(payload)
        };
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<bool>(async () =>
        {
            ConfirmDialogs dialogs = new();
            await using MainWindowViewModel main = new(dialogs: dialogs,
                settingsPath: settings.Path, appPaths: appPaths, infrastructure: infrastructure);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            LocalFileManagementSettingsViewModel management = main.Settings.LocalFileManagement;
            await management.LoadAsync();
            ManagedLocationViewModel row = management.Locations.Single(location =>
                location.Path.Equals(services.PluginFiles.ManagedRootFor(TestPluginId),
                    StringComparison.OrdinalIgnoreCase));

            await management.DownloadModelsAsync(row);

            DownloadTaskStatus task = services.DownloadTasks.List().Should().ContainSingle()
                .Subject;
            task.State.Should().Be(DownloadTaskService.FailedState,
                "a declaration with no download_url can never be satisfied");
            task.ErrorMessage.Should().Contain("download_url",
                "the failure names the missing declaration field");
            management.Status.Should().Contain("下载未完成");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_plugin_declaration_removed_before_the_click_downloads_nothing()
    {
        using TemporaryAppSettingsFile settings = new();
        string root = Path.GetDirectoryName(settings.Path)!;
        TestAppPaths appPaths = new(root);
        string package = RegisterPlugin(root, PluginManifest());
        byte[] payload = "payload"u8.ToArray();
        HostInfrastructureOptions infrastructure = new()
        {
            HttpHandlerFactory = _ => new PluginBytesHandler(payload)
        };
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<bool>(async () =>
        {
            ConfirmDialogs dialogs = new();
            await using MainWindowViewModel main = new(dialogs: dialogs,
                settingsPath: settings.Path, appPaths: appPaths, infrastructure: infrastructure);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            LocalFileManagementSettingsViewModel management = main.Settings.LocalFileManagement;
            await management.LoadAsync();
            ManagedLocationViewModel row = management.Locations.Single(location =>
                location.Path.Equals(services.PluginFiles.ManagedRootFor(TestPluginId),
                    StringComparison.OrdinalIgnoreCase));

            // The manifest loses its [[files]] declarations between the inventory read and the
            // click — the fresh prepare must see the group gone instead of replaying the stale
            // row.
            File.WriteAllText(Path.Combine(package,
                    Host.Plugins.PluginManifestLoader.ManifestFileName),
                PluginManifest(includeFiles: false));
            ((Host.Plugins.HostPluginApi)services.Plugins).RefreshRegistry();

            Result<ManagedInventoryDownload> prepared =
                await services.ManagedFileInventory.PrepareDownloadAsync(row.Path);
            prepared.IsFailure.Should().BeTrue();
            prepared.ErrorCode.Should().Be(AppErrorCodes.NotFound);

            await management.DownloadModelsAsync(row);
            services.DownloadTasks.List().Should().BeEmpty(
                "a removed declaration never triggers a stale download");
            dialogs.Shown.OfType<ConfirmDialogViewModel>().Should().BeEmpty(
                "no prompt is shown for a vanished group");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_plugin_declaration_removed_during_clear_confirmation_survives()
    {
        using TemporaryAppSettingsFile settings = new();
        string root = Path.GetDirectoryName(settings.Path)!;
        TestAppPaths appPaths = new(root);
        string package = RegisterPlugin(root, PluginManifest());
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<bool>(async () =>
        {
            MutatingDialogs dialogs = new();
            await using MainWindowViewModel main = new(dialogs: dialogs,
                settingsPath: settings.Path, appPaths: appPaths);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            LocalFileManagementSettingsViewModel management = main.Settings.LocalFileManagement;
            string managedRoot = services.PluginFiles.ManagedRootFor(TestPluginId);
            string declared = Path.Combine(managedRoot, "models", "layout.onnx");
            Directory.CreateDirectory(Path.GetDirectoryName(declared)!);
            await File.WriteAllBytesAsync(declared, "payload"u8.ToArray());
            await management.LoadAsync();
            ManagedLocationViewModel row = management.Locations.Single(location =>
                location.Path.Equals(managedRoot, StringComparison.OrdinalIgnoreCase));

            // The manifest loses its [[files]] declarations while the clear confirmation is
            // open: the freshly intersected eligible set is empty and the file survives.
            HostServices captured = services;
            string capturedPackage = package;
            dialogs.DuringConfirm = () =>
            {
                File.WriteAllText(Path.Combine(capturedPackage,
                        Host.Plugins.PluginManifestLoader.ManifestFileName),
                    PluginManifest(includeFiles: false));
                ((Host.Plugins.HostPluginApi)captured.Plugins).RefreshRegistry();
                return Task.CompletedTask;
            };
            await management.ClearLocationAsync(row);

            File.Exists(declared).Should().BeTrue(
                "a declaration removed during confirmation must never be deleted by a stale prompt");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Two_different_rows_download_concurrently_and_one_row_shares_one_prompt()
    {
        using TemporaryAppSettingsFile settings = new();
        string root = Path.GetDirectoryName(settings.Path)!;
        TestAppPaths appPaths = new(root);
        const string secondId = "test.second";
        WritePluginPackage(root, "first", PluginManifest());
        WritePluginPackage(root, "second", PluginManifest(id: secondId));
        string firstPackage = Path.Combine(root, "packages", "first");
        string secondPackage = Path.Combine(root, "packages", "second");
        WriteRegistry(root,
            (Path.Combine(firstPackage, Host.Plugins.PluginManifestLoader.ManifestFileName), true,
                ["find", "fetch"]),
            (Path.Combine(secondPackage, Host.Plugins.PluginManifestLoader.ManifestFileName), true,
                ["find", "fetch"]));
        byte[] payload = "payload"u8.ToArray();
        HostInfrastructureOptions infrastructure = new()
        {
            HttpHandlerFactory = _ => new PluginBytesHandler(payload)
        };
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<bool>(async () =>
        {
            BlockingDialogs dialogs = new();
            await using MainWindowViewModel main = new(dialogs: dialogs,
                settingsPath: settings.Path, appPaths: appPaths, infrastructure: infrastructure);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            LocalFileManagementSettingsViewModel management = main.Settings.LocalFileManagement;
            await management.LoadAsync();
            ManagedLocationViewModel first = management.Locations.Single(location =>
                location.Path.Equals(services.PluginFiles.ManagedRootFor(TestPluginId),
                    StringComparison.OrdinalIgnoreCase));
            ManagedLocationViewModel second = management.Locations.Single(location =>
                location.Path.Equals(services.PluginFiles.ManagedRootFor(secondId),
                    StringComparison.OrdinalIgnoreCase));

            // The first row's prompt stays open — a second, different directory must still be
            // admitted (no global is-downloading gate).
            List<Task> running = [];
            try
            {
                Task firstDownload = management.DownloadModelsAsync(first);
                running.Add(firstDownload);
                await WaitForDialogs(dialogs, 1);

                Task secondDownload = management.DownloadModelsAsync(second);
                running.Add(secondDownload);
                await WaitForDialogs(dialogs, 2);
                dialogs.Shown.Count.Should().Be(2,
                    "a second directory is admitted while the first prompt is still open");

                // A concurrent call for the SAME row joins the in-flight operation instead of
                // prompting again.
                Task duplicate = management.DownloadModelsAsync(first);
                running.Add(duplicate);
                await Task.Delay(100);
                dialogs.Shown.Count.Should().Be(2,
                    "a duplicate admission shares the in-flight prompt, never a second one");
            }
            finally
            {
                dialogs.Release();
                await Task.WhenAll(running);
            }

            services.DownloadTasks.List().Should().HaveCount(2,
                "one task per directory, not per call");
            File.Exists(Path.Combine(first.Path, "models", "layout.onnx")).Should().BeTrue();
            File.Exists(Path.Combine(second.Path, "models", "layout.onnx")).Should().BeTrue();
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Clearing_a_plugin_row_deletes_only_declared_targets()
    {
        using TemporaryAppSettingsFile settings = new();
        string root = Path.GetDirectoryName(settings.Path)!;
        TestAppPaths appPaths = new(root);
        RegisterPlugin(root, PluginManifest(true));
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<bool>(async () =>
        {
            ConfirmDialogs dialogs = new();
            await using MainWindowViewModel main = new(dialogs: dialogs,
                settingsPath: settings.Path, appPaths: appPaths);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            LocalFileManagementSettingsViewModel management = main.Settings.LocalFileManagement;
            string managedRoot = services.PluginFiles.ManagedRootFor(TestPluginId);
            string declared = Path.Combine(managedRoot, "models", "layout.onnx");
            Directory.CreateDirectory(Path.GetDirectoryName(declared)!);
            await File.WriteAllBytesAsync(declared, "payload"u8.ToArray());
            string unrelated = Path.Combine(managedRoot, "keep.txt");
            await File.WriteAllTextAsync(unrelated, "keep");
            string nested = Path.Combine(managedRoot, "models", "user-dir");
            Directory.CreateDirectory(nested);

            await management.LoadAsync();
            ManagedLocationViewModel row = management.Locations.Single(location =>
                location.Path.Equals(managedRoot, StringComparison.OrdinalIgnoreCase));
            await management.ClearLocationAsync(row);

            File.Exists(declared).Should().BeFalse("the declared target is removed");
            File.Exists(unrelated).Should().BeTrue("unrelated files survive the clear");
            Directory.Exists(nested).Should().BeTrue("unrelated nested directories survive");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Page_render_cache_row_shows_directory_size_file_count_and_settings()
    {
        using TemporaryAppSettingsFile settings = new();
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<bool>(async () =>
        {
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            string cacheRoot = (await services.ManagedFileInventory.DescribePageRenderCacheAsync()).Value.Directory;
            PlantRender(cacheRoot, 2048);
            LocalFileManagementSettingsViewModel management = main.Settings.LocalFileManagement;

            await management.LoadAsync();

            management.HasPageRenderCache.Should().BeTrue();
            management.PageRenderCacheDirectory.Should().Be(cacheRoot);
            Path.GetFileName(cacheRoot).Should().Be("page-renders");
            management.PageRenderCacheFileCount.Should().Be(1);
            management.PageRenderCacheBytes.Should().Be(2048);
            management.PageRenderCacheSizeDisplay.Should().Be("2.00 KB");
            management.RenderCacheLimitMiB.Should().Be(512, "the default disk limit is 512 MiB");
            management.RenderCachePngQuality.Should().Be(100);
            management.Locations.Should().NotContain(row => string.Equals(row.Path, cacheRoot,
                StringComparison.OrdinalIgnoreCase), "the render cache has its own clear path, not a generic row");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Settings_page_renders_the_page_render_cache_card()
    {
        using TemporaryAppSettingsFile settings = new();
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<bool>(async () =>
        {
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            string cacheRoot = (await services.ManagedFileInventory.DescribePageRenderCacheAsync()).Value.Directory;
            PlantRender(cacheRoot, 2048);
            SettingsPage page = new() { DataContext = main.Settings };
            Window window = new() { Width = 1280, Height = 900, Content = page };
            window.Show();
            try
            {
                await main.Settings.SelectSectionAsync("local_files");
                await main.Settings.LocalFileManagement.LoadAsync();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();

                TextBlock[] texts = window.GetVisualDescendants().OfType<TextBlock>()
                    .Where(block => block.IsEffectivelyVisible).ToArray();
                texts.Should().Contain(block => block.Text == "页面渲染缓存");
                texts.Should().Contain(block => block.Text == cacheRoot);
                texts.Should().Contain(block => block.Text == "2.00 KB");
                texts.Should().Contain(block => block.Text == "1 个文件");
                NumericUpDown[] spinners = window.GetVisualDescendants().OfType<NumericUpDown>()
                    .Where(spinner => spinner.IsEffectivelyVisible).ToArray();
                spinners.Should().Contain(spinner => spinner.Value == 512m && spinner.Minimum == 32m &&
                                                     spinner.Maximum == 16384m);
                spinners.Should().Contain(spinner => spinner.Value == 100m && spinner.Minimum == 1m &&
                                                     spinner.Maximum == 100m);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Render_cache_limit_and_quality_persist_and_reach_the_host()
    {
        using TemporaryAppSettingsFile settings = new();
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<bool>(async () =>
        {
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            LocalFileManagementSettingsViewModel management = main.Settings.LocalFileManagement;
            await management.LoadAsync();
            PatchouliAppSettings before = main.AppOptions;

            // Rapid spinner edits coalesce: only the final values are committed.
            management.RenderCacheLimitMiB = 640;
            management.RenderCacheLimitMiB = 768;
            management.RenderCacheLimitMiB = 1024;
            management.RenderCachePngQuality = 80;

            main.AppOptions.RenderCache.Should().Be(new RenderCacheAppSettings(1024L * 1024 * 1024, 80));
            main.AppOptions.Ui.Should().Be(before.Ui, "a RenderCache edit never replaces other categories");
            await WaitUntil(async () =>
            {
                Result<PageRenderCacheDescription> host =
                    await services.ManagedFileInventory.DescribePageRenderCacheAsync();
                return host.Value.DiskLimitBytes == 1024L * 1024 * 1024 && host.Value.PngQuality == 80;
            }, "the committed RenderCache category applies the limit and quality to the host");
            await WaitUntil(() => Task.FromResult(
                    PatchouliAppSettings.Load(settings.Path).RenderCache ==
                    new RenderCacheAppSettings(1024L * 1024 * 1024, 80)),
                "the debounced writer persists the RenderCache section");

            // Out-of-range values clamp to the host's supported range.
            management.RenderCacheLimitMiB = 1;
            management.RenderCachePngQuality = 0;
            main.AppOptions.RenderCache.Should().Be(new RenderCacheAppSettings(
                PageRenderService.MinDiskCacheByteLimit, 1));
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Clearing_the_render_cache_requires_confirmation()
    {
        using TemporaryAppSettingsFile settings = new();
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<bool>(async () =>
        {
            ConfirmDialogs dialogs = new() { Result = ConfirmDialogResult.Cancel };
            await using MainWindowViewModel main = new(dialogs: dialogs,
                settingsPath: settings.Path, appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            string cacheRoot = (await services.ManagedFileInventory.DescribePageRenderCacheAsync()).Value.Directory;
            string planted = PlantRender(cacheRoot, 16);
            LocalFileManagementSettingsViewModel management = main.Settings.LocalFileManagement;
            await management.LoadAsync();

            await management.ClearPageRenderCacheAsync();

            File.Exists(planted).Should().BeTrue("a cancelled prompt deletes nothing");
            dialogs.Shown.OfType<ConfirmDialogViewModel>().Should().ContainSingle()
                .Which.ConfirmDanger.Should().BeTrue();

            dialogs.Result = ConfirmDialogResult.Confirm;
            await management.ClearPageRenderCacheAsync();

            File.Exists(planted).Should().BeFalse("a confirmed clear goes through the host cache API");
            management.PageRenderCacheFileCount.Should().Be(0, "the row refreshes after the clear");
            management.PageRenderCacheBytes.Should().Be(0);
            management.Status.Should().Contain("已清理页面渲染缓存");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_library_switch_during_the_render_cache_prompt_never_clears()
    {
        using TemporaryAppSettingsFile settings = new();
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<bool>(async () =>
        {
            SwitchingDialogs dialogs = new();
            await using MainWindowViewModel main = new(dialogs: dialogs,
                settingsPath: settings.Path, appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            string cacheRoot = (await services.ManagedFileInventory.DescribePageRenderCacheAsync()).Value.Directory;
            string planted = PlantRender(cacheRoot, 16);
            LocalFileManagementSettingsViewModel management = main.Settings.LocalFileManagement;
            await management.LoadAsync();

            MainWindowViewModel stale = main;
            TemporaryAppSettingsFile switched = settings;
            dialogs.Switch = async () =>
            {
                stale.RuntimeDatabasePath = switched.CreateDatabasePath("switched");
                await stale.OpenDatabaseCommand.ExecuteAsync();
            };
            await management.ClearPageRenderCacheAsync();

            File.Exists(planted).Should().BeTrue("a stale confirmation context must never clear the cache");
            management.Status.Should().Contain("书库已切换");
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_stale_render_cache_read_is_never_applied_after_a_library_switch()
    {
        using TemporaryAppSettingsFile settings = new();
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));
        await session.Dispatch<bool>(async () =>
        {
            await using MainWindowViewModel main = new(settingsPath: settings.Path,
                appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!));
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices staleServices = await main.ServicesAsync();
            int staleGeneration = main.LibraryGeneration;
            string cacheRoot = (await staleServices.ManagedFileInventory.DescribePageRenderCacheAsync()).Value
                .Directory;
            LocalFileManagementSettingsViewModel management = main.Settings.LocalFileManagement;
            PlantRender(cacheRoot, 4096);

            main.RuntimeDatabasePath = settings.CreateDatabasePath("switched");
            await main.OpenDatabaseCommand.ExecuteAsync();
            await management.LoadAsync();
            management.PageRenderCacheBytes.Should().Be(4096, "the current context reads the device cache");

            // A read captured under the previous library context must never overwrite it.
            PlantRender(cacheRoot, 1024);
            await management.RefreshPageRenderCacheAsync(staleServices, staleGeneration);

            management.PageRenderCacheBytes.Should().Be(4096,
                "a read captured under the previous library context is discarded");
            return true;
        }, CancellationToken.None);
    }

    private static string PlantRender(string cacheRoot, int bytes)
    {
        string path = Path.Combine(cacheRoot, Guid.NewGuid().ToString(), "planted.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static async Task WaitUntil(Func<Task<bool>> condition, string because)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!await condition())
        {
            (DateTime.UtcNow < deadline).Should().BeTrue($"timed out: {because}");
            await Task.Delay(20);
        }
    }

    private const string TestPluginId = "kwadraten.kuzushi-classifier";

    /// <summary>
    /// A minimal valid patchouli-plugin.toml with managed-file declarations; when
    /// <paramref name="nested"/> is set the second file shares the models/ directory so nested
    /// declarations stay inside the single root row.
    /// </summary>
    private static string PluginManifest(bool nested = false, bool nestedDownloadable = false,
        string? id = null, bool includeFiles = true)
    {
        string payloadSha = Sha256Of("payload"u8.ToArray());
        return $$"""
                 id = "{{id ?? TestPluginId}}"
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
                 """ + (includeFiles
            ? $$"""

                [[files]]
                path = "models/layout.onnx"
                download_url = "https://example.test/layout.onnx"
                bytes = 7
                sha256 = "{{payloadSha}}"
                version = "2026.11"
                """ + (nested && nestedDownloadable
                ? $$"""

                    [[files]]
                    path = "models/vocab.txt"
                    download_url = "https://example.test/vocab.txt"
                    bytes = 7
                    sha256 = "{{payloadSha}}"
                    """
                : nested
                    ? """

                      [[files]]
                      path = "models/vocab.txt"
                      """
                    : "")
            : "");
    }

    private static string WritePluginPackage(string root, string name, string manifest)
    {
        string directory = Path.Combine(root, "packages", name);
        Directory.CreateDirectory(Path.Combine(directory, ".agents"));
        Directory.CreateDirectory(Path.Combine(directory, "schemas"));
        File.WriteAllText(Path.Combine(directory, ".agents", "plugin-usage.md"), "# usage\n");
        File.WriteAllText(Path.Combine(directory, "schemas", "status.json"),
            """{"type":"object","required":["ok"],"properties":{"ok":{"type":"boolean"}}}""");
        File.WriteAllText(Path.Combine(directory,
            Host.Plugins.PluginManifestLoader.ManifestFileName), manifest);
        return directory;
    }

    private static void WriteRegistry(string root,
        params (string ManifestPath, bool Enabled, string[] Verbs)[] entries)
    {
        string registry = Host.Plugins.PluginRegistry.RegistryPath(
            Path.Combine(root, "data"));
        Directory.CreateDirectory(Path.GetDirectoryName(registry)!);
        File.WriteAllText(registry, System.Text.Json.JsonSerializer.Serialize(entries.Select(entry => new
        {
            manifest_path = entry.ManifestPath,
            enabled = entry.Enabled,
            allowed_verbs = entry.Verbs
        })));
    }

    private static string RegisterPlugin(string root, string manifest)
    {
        string package = WritePluginPackage(root, Guid.NewGuid().ToString("N"), manifest);
        WriteRegistry(root,
            (Path.Combine(package, Host.Plugins.PluginManifestLoader.ManifestFileName),
                true, ["find", "fetch"]));
        return package;
    }

    private static string Sha256Of(byte[] content)
    {
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));
    }

    /// <summary>Serves the pinned plugin payload for any request — offline HTTP override.</summary>
    private sealed class PluginBytesHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
        }
    }

    /// <summary>Auto-confirms every dialog and records what was shown.</summary>
    private sealed class ConfirmDialogs : IDialogService
    {
        public List<object> Shown { get; } = [];
        public object? Result { get; set; } = ConfirmDialogResult.Confirm;

        public Task ShowDialogAsync(object viewModel)
        {
            Shown.Add(viewModel);
            return Task.CompletedTask;
        }

        public Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            Shown.Add(viewModel);
            return Task.FromResult(Result is TResult typed ? typed : default);
        }
    }

    /// <summary>Auto-confirms but runs a mutation (e.g. declaration removal) while the prompt is open.</summary>
    private sealed class MutatingDialogs : IDialogService
    {
        public Func<Task>? DuringConfirm { get; set; }

        public Task ShowDialogAsync(object viewModel)
        {
            return Task.CompletedTask;
        }

        public async Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            if (DuringConfirm is not null)
            {
                await DuringConfirm();
            }

            return (TResult?)(object)ConfirmDialogResult.Confirm;
        }
    }

    /// <summary>Polls until <paramref name="count"/> dialogs are shown; bounded so a failure never hangs.</summary>
    private static async Task WaitForDialogs(BlockingDialogs dialogs, int count)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (dialogs.Shown.Count < count)
        {
            (DateTime.UtcNow < deadline).Should().BeTrue(
                $"timed out waiting for {count} confirmation dialogs (saw {dialogs.Shown.Count})");
            await Task.Delay(20);
        }
    }

    /// <summary>Holds every confirmation open until <see cref="Release"/> is called.</summary>
    private sealed class BlockingDialogs : IDialogService
    {
        private readonly TaskCompletionSource _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<object> Shown { get; } = [];

        public void Release()
        {
            _gate.TrySetResult();
        }

        public Task ShowDialogAsync(object viewModel)
        {
            Shown.Add(viewModel);
            return _gate.Task;
        }

        public async Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            Shown.Add(viewModel);
            await _gate.Task;
            return (TResult?)(object)ConfirmDialogResult.Confirm;
        }
    }

    /// <summary>Confirms the prompt but runs a library switch while it is open.</summary>
    private sealed class SwitchingDialogs : IDialogService
    {
        public Func<Task>? Switch { get; set; }

        public Task ShowDialogAsync(object viewModel)
        {
            return Task.CompletedTask;
        }

        public async Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            if (Switch is not null)
            {
                await Switch();
            }

            return (TResult?)(object)ConfirmDialogResult.Confirm;
        }
    }
}
