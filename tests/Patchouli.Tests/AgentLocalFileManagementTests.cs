using Avalonia.Headless;
using FluentAssertions;
using Patchouli.Host.Agent;
using Patchouli.Host.Composition;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class AgentLocalFileManagementTests
{
    [Fact]
    public async Task The_settings_entry_measures_and_clears_scratch_files_without_deleting_the_conversation()
    {
        using TemporaryAppSettingsFile settings = new();
        using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(App));
        await session.Dispatch(async () =>
        {
            await using MainWindowViewModel main = new(dialogs: new ConfirmingDialogs(), settingsPath: settings.Path);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            AgentSessionSnapshot conversation = await services.AgentSessions.CreateAsync(
                AgentSessionLaunchParameters.Create("test"));
            string directory = services.AgentSessions.GetWorkingDirectory(conversation.SessionId);
            try
            {
                File.WriteAllText(Path.Combine(directory, "scratch.txt"), "workspace-content");
                LocalFileManagementSettingsViewModel management = main.Settings.LocalFileManagement;
                await management.LoadAsync();
                ManagedLocationViewModel location = management.Locations.Single(row => row.IsAgentWorkspace);
                location.Path.Should().Be(services.AgentSessions.WorkspacesRoot);
                location.SizeBytes.Should().BeGreaterThan(0);
                location.ItemCount.Should().BeGreaterThan(0);
                location.CanClear.Should().BeTrue();
                location.CanDownload.Should().BeFalse();
                await location.ClearCommand.ExecuteAsync();
                Directory.Exists(directory).Should().BeFalse();
                location.SizeBytes.Should().Be(0);
                (await services.AgentSessions.ListSessionsAsync()).Should()
                    .Contain(snapshot => snapshot.SessionId == conversation.SessionId);
            }
            finally
            {
                await services.AgentSessions.PurgeAsync(conversation.SessionId);
            }
        }, CancellationToken.None);
    }

    private sealed class ConfirmingDialogs : IDialogService
    {
        public Task ShowDialogAsync(object viewModel)
        {
            return Task.CompletedTask;
        }

        public Task<TResult?> ShowDialogAsync<TResult>(object viewModel)
        {
            return Task.FromResult((TResult?)(object)ConfirmDialogResult.Confirm);
        }
    }
}
