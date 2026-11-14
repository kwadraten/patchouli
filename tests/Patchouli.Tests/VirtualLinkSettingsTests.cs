using FluentAssertions;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class VirtualLinkSettingsTests
{
    [Fact]
    public async Task Search_setting_defaults_on_and_persists_independently_of_registration()
    {
        using TemporaryAppSettingsFile settings = new();
        await using MainWindowViewModel main = new(settingsPath: settings.Path);
        FakeRegistration registration = new();
        VirtualLinkSettingsViewModel panel = new(main, registration);
        panel.ParseLinksInSearchBar.Should().BeTrue();
        panel.ParseLinksInSearchBar = false;
        main.AppOptions.Ui.ParseVirtualLinksInSearchBar.Should().BeFalse();
        await main.SettingsStore.FlushAsync();
        PatchouliAppSettings.Load(settings.Path).Ui.ParseVirtualLinksInSearchBar.Should().BeFalse();
        registration.ChangeCount.Should().Be(0);

        panel.RegisterProtocol = true;
        await panel.RegistrationTask;
        registration.Registered.Should().BeTrue();
        panel.RegisterProtocol.Should().BeTrue();
        panel.ParseLinksInSearchBar.Should().BeFalse();
        panel.RegisterProtocol = false;
        await panel.RegistrationTask;
        registration.Registered.Should().BeFalse();
        main.AppOptions.Ui.ParseVirtualLinksInSearchBar.Should().BeFalse();
    }

    [Fact]
    public async Task Failed_registration_reverts_switch_to_actual_state_and_shows_error()
    {
        using TemporaryAppSettingsFile settings = new();
        await using MainWindowViewModel main = new(settingsPath: settings.Path);
        FakeRegistration registration = new() { Fail = true };
        VirtualLinkSettingsViewModel panel = new(main, registration);
        panel.RegisterProtocol = true;
        await panel.RegistrationTask;
        panel.RegisterProtocol.Should().BeFalse();
        panel.RegistrationStatus.Should().Contain("操作失败").And.Contain("拒绝访问");
        panel.CanChangeRegistration.Should().BeTrue();
    }

    [Fact]
    public async Task Settings_group_contains_the_named_system_integration_subpage()
    {
        using TemporaryAppSettingsFile settings = new();
        await using MainWindowViewModel main = new(settingsPath: settings.Path);
        SettingsSectionGroupViewModel group = main.Settings.Categories
            .Single(category => category.Title == "行为").Content.Should()
            .BeOfType<SettingsSectionGroupViewModel>().Subject;
        SettingsSectionEntryViewModel entry =
            group.Entries.Single(candidate => candidate.Id == "system_integration");
        entry.Title.Should().Be("链接与系统");
        entry.Content.Should().BeSameAs(main.Settings.SystemIntegrationSettings);
        main.Settings.SystemIntegrationSettings.VirtualLinks.Should().BeSameAs(main.Settings.VirtualLinkSettings);
    }

    private sealed class FakeRegistration : IProtocolRegistrationService
    {
        public bool Registered { get; private set; }
        public bool Fail { get; init; }
        public int ChangeCount { get; private set; }

        public ProtocolRegistrationState GetState()
        {
            return new ProtocolRegistrationState(true, Registered, Registered ? "已注册" : "未注册");
        }

        public void SetRegistered(bool registered)
        {
            ChangeCount++;
            if (Fail)
            {
                throw new UnauthorizedAccessException("拒绝访问");
            }

            Registered = registered;
        }
    }
}
