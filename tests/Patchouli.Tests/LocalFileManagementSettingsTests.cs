using Avalonia.Headless;
using FluentAssertions;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Composition;
using Patchouli.Host.Extensions;
using Patchouli.Llm;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.UI.ViewModels.Settings;

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

            // Two extensions contribute declarations into the same directory — the merged
            // group's eligible set is still exactly the union of declared targets.
            ManagedLocationFileGroup group = new("ext.a", null, shared, "Shared");
            ExtensionConfigSnapshot config =
                new(new Dictionary<string, string>(), 0);
            group.Merge(new ExtensionManagedFileGroup("ext.a", "model-path", "Model", null,
                shared,
                [new ExtensionManagedFileDownload("a.onnx", "https://example.test/a", declaredA)],
                []), config);
            group.Merge(new ExtensionManagedFileGroup("ext.b", "weights", "Weights", null,
                shared,
                [new ExtensionManagedFileDownload("b.onnx", "https://example.test/b", declaredB)],
                []), config);

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
            ManagedLocationFileGroup group = new("ext.test", null, root, "Test");
            group.Merge(new ExtensionManagedFileGroup("ext.test", "model-path", "Model", null,
                    root, [new ExtensionManagedFileDownload("model.onnx", "https://example.test/model", target)], []),
                new ExtensionConfigSnapshot(new Dictionary<string, string>(), 0));
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

    /// <summary>Auto-confirms every dialog and records what was shown.</summary>
    private sealed class ConfirmDialogs : IDialogService
    {
        public List<object> Shown { get; } = [];

        public Task ShowDialogAsync(object viewModel)
        {
            Shown.Add(viewModel);
            return Task.CompletedTask;
        }

        public Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            Shown.Add(viewModel);
            return Task.FromResult((TResult?)(object)ConfirmDialogResult.Confirm);
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
