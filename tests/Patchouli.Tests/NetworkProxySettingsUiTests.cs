using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Patchouli.Core.Credentials;
using Patchouli.Core.Results;
using Patchouli.Core.Settings;
using Patchouli.Host.Composition;
using Patchouli.Host.Network;
using Patchouli.UI;
using Patchouli.UI.Controls;
using Patchouli.UI.ViewModels;
using Patchouli.UI.ViewModels.Settings;
using Patchouli.UI.Views;

namespace Patchouli.Tests;

/// <summary>
/// 「网络代理」settings section (issue #56). No test touches the real network: connection probes go
/// through the section's tester seam and every host HTTP handler is replaced.
/// </summary>
[Collection("RetainedTabUI")]
[Trait("Category", "ui")]
public sealed class NetworkProxySettingsUiTests(AvaloniaSharedSessionFixture headless)
{
    private const string Address = "http://proxy.corp.invalid:8080";
    private const string Username = "proxy-user-name";
    private const string Password = "proxy-secret-pw";

    [Fact]
    public async Task Bypass_field_is_labelled_as_excluded_hosts_not_a_proxy_switch()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = CreateMain(settings);
            await main.OpenSettingsAsync("system_integration");
            NetworkProxySettingsViewModel section = main.Settings.NetworkProxySettings;
            SettingsPage page = new();
            Window window = new() { Width = 1000, Height = 900, Content = page };
            window.Show();
            try
            {
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                RetainedContentHost host = page.GetVisualDescendants().OfType<RetainedContentHost>().First();
                Control body = host.DataTemplates.Single(template => template.Match(section)).Build(section)!;
                body.DataContext = section;
                window.Content = body;
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();

                section.SelectedMode = section.ModeOptions.Single(option => option.Value == NetworkProxyModes.Manual);
                Dispatcher.UIThread.RunJobs();
                List<string> labels = body.GetVisualDescendants().OfType<TextBlock>()
                    .Where(block => block.IsEffectivelyVisible).Select(block => block.Text ?? "").ToList();
                labels.Should().Contain("需要排除的网址");
                labels.Should().NotContain("不走代理");

                TextBox bypass = body.GetVisualDescendants().OfType<TextBox>()
                    .Single(box => box.Name == "ProxyBypassTextBox");
                bypass.PlaceholderText.Should().Be("每行一个主机名或域名，如 *.example.com");
            }
            finally
            {
                window.Close();
            }

            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Manual_fields_are_visible_only_in_manual_mode_and_invalid_input_is_never_saved()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = CreateMain(settings);
            await main.OpenSettingsAsync("system_integration");
            NetworkProxySettingsViewModel section = main.Settings.NetworkProxySettings;
            SettingsPage page = new();
            Window window = new() { Width = 1000, Height = 900, Content = page };
            window.Show();
            try
            {
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                RetainedContentHost host = page.GetVisualDescendants().OfType<RetainedContentHost>().First();
                Control body = host.DataTemplates.Single(template => template.Match(section)).Build(section)!;
                body.DataContext = section;
                window.Content = body;
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();

                Control manualPanel = body.GetVisualDescendants().OfType<Control>()
                    .Single(control => control.Name == "ManualProxyPanel");
                section.SelectedMode.Value.Should().Be(NetworkProxyModes.System);
                manualPanel.IsVisible.Should().BeFalse();

                section.SelectedMode = section.ModeOptions.Single(option => option.Value == NetworkProxyModes.Manual);
                Dispatcher.UIThread.RunJobs();
                manualPanel.IsVisible.Should().BeTrue();
                body.GetVisualDescendants().OfType<TextBox>()
                    .Single(box => box.Name == "ProxyPasswordTextBox").PasswordChar.Should().Be('•');

                section.ValidationText.Should().Be("请填写代理地址");
                await section.SaveAsync();
                section.SaveState.Should().Be(SettingsSaveState.Failed);
                section.IsDirty.Should().BeTrue();
                main.AppOptions.Network.ProxyMode.Should().Be(NetworkProxyModes.System);

                section.ProxyAddress = "ftp://proxy.invalid:21";
                section.ValidationText.Should().Be("仅支持 http/https/socks5/socks5h");
                section.ProxyAddress = "http://user:pw@proxy.invalid:8080";
                section.ValidationText.Should().Be("地址中不能包含用户名或密码");
                section.ProxyAddress = "not a url";
                section.ValidationText.Should().Be("代理地址格式无效");
                await section.SaveAsync();
                section.ValidationState.Should().Be(SettingsValidationState.Invalid);
                section.IsDirty.Should().BeTrue("an invalid draft stays dirty instead of being cleared");
                PatchouliAppSettings.Load(settings.Path).Network.ProxyMode.Should().Be(NetworkProxyModes.System);

                section.ProxyAddress = "socks5h://proxy.invalid:1080";
                section.BypassText = "*.corp.invalid\n\n localhost:8080 ";
                section.ValidationText.Should().BeEmpty();
                await section.SaveAsync();
                section.IsDirty.Should().BeFalse();
                NetworkAppSettings saved = PatchouliAppSettings.Load(settings.Path).Network;
                saved.ProxyMode.Should().Be(NetworkProxyModes.Manual);
                saved.ProxyAddress.Should().Be("socks5h://proxy.invalid:1080");
                saved.BypassList.Should().Equal("*.corp.invalid", "localhost:8080");

                section.SelectedMode = section.ModeOptions.Single(option => option.Value == NetworkProxyModes.None);
                Dispatcher.UIThread.RunJobs();
                manualPanel.IsVisible.Should().BeFalse();
            }
            finally
            {
                window.Close();
            }

            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Password_lives_only_in_the_credential_store_stays_masked_after_reload_and_can_be_cleared()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            RecordingPolicy policy = new();
            await using MainWindowViewModel main = CreateMain(settings, policy);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            HostServices services = await main.ServicesAsync();
            NetworkProxySettingsViewModel section = main.Settings.NetworkProxySettings;
            section.SelectedMode = section.ModeOptions.Single(option => option.Value == NetworkProxyModes.Manual);
            section.ProxyAddress = Address;
            section.ProxyUsername = Username;
            section.ProxyPasswordInput = Password;
            await section.SaveAsync();

            section.IsDirty.Should().BeFalse(section.LastError);
            section.ProxyPasswordInput.Should().BeEmpty();
            section.HasProxyPassword.Should().BeTrue();
            policy.InvalidateCount.Should().BeGreaterThan(0);
            (await services.Credentials.GetActiveSecretForProviderAsync(ProviderIds.NetworkProxy))
                .Value.Should().Be(Password);
            JsonObject root = JsonNode.Parse(await File.ReadAllTextAsync(settings.Path))!.AsObject();
            string network = root["Network"]!.ToJsonString();
            network.Should().NotContain(Password);
            root["Network"]!["HasProxyPassword"]!.GetValue<bool>().Should().BeTrue();
            main.AppOptions.Network.HasProxyPassword.Should().BeTrue();
            section.Status.Should().NotContain(Address).And.NotContain(Username).And.NotContain(Password);

            await using (MainWindowViewModel reloaded = CreateMain(settings))
            {
                NetworkProxySettingsViewModel reopened = reloaded.Settings.NetworkProxySettings;
                await reopened.LoadAsync();
                reopened.ProxyPasswordInput.Should().BeEmpty("the saved secret is never read back");
                reopened.HasProxyPassword.Should().BeTrue();
                reopened.ProxyUsername.Should().Be(Username);
                reopened.IsDirty.Should().BeFalse();
            }

            int invalidations = policy.InvalidateCount;
            await section.ClearCredentialCommand.ExecuteAsync();
            section.HasProxyPassword.Should().BeFalse();
            section.IsDirty.Should().BeFalse();
            policy.InvalidateCount.Should().BeGreaterThan(invalidations);
            (await services.Credentials.GetActiveSecretForProviderAsync(ProviderIds.NetworkProxy))
                .IsFailure.Should().BeTrue();
            PatchouliAppSettings.Load(settings.Path).Network.HasProxyPassword.Should().BeFalse();
            (await File.ReadAllTextAsync(settings.Path)).Should().NotContain(Password);
            return 0;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(NetworkConnectionTestKinds.Ok, "连接成功")]
    [InlineData(NetworkConnectionTestKinds.Tls, "钥匙串")]
    [InlineData(NetworkConnectionTestKinds.ProxyTunnel, "代理拒绝")]
    [InlineData(NetworkConnectionTestKinds.Dns, "无法解析域名")]
    [InlineData(NetworkConnectionTestKinds.Connection, "无法建立连接")]
    [InlineData(NetworkConnectionTestKinds.Authentication, "认证失败")]
    [InlineData(NetworkConnectionTestKinds.Timeout, "连接超时")]
    [InlineData(NetworkConnectionTestKinds.Http, "服务器返回错误")]
    [InlineData(NetworkConnectionTestKinds.InvalidConfiguration, "代理设置无效")]
    [InlineData(NetworkConnectionTestKinds.Canceled, "已取消")]
    [InlineData(NetworkConnectionTestKinds.Error, "连接测试失败")]
    public async Task Connection_test_saves_and_applies_first_then_reports_short_sanitized_chinese(
        string kind, string expected)
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            RecordingPolicy policy = new();
            await using MainWindowViewModel main = CreateMain(settings, policy);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            NetworkProxySettingsViewModel section = main.Settings.NetworkProxySettings;
            NetworkAppSettings? appliedDuringTest = null;
            bool ranOffUiThread = false;
            section.UseConnectionTester((services, _) =>
            {
                ranOffUiThread = !Dispatcher.UIThread.CheckAccess();
                appliedDuringTest = services.HttpTransportPolicy.Settings;
                return Task.FromResult(new NetworkConnectionTestResult(kind == NetworkConnectionTestKinds.Ok, kind,
                    $"raw {Address} {Username} {Password} exception text", 407));
            });
            section.SelectedMode = section.ModeOptions.Single(option => option.Value == NetworkProxyModes.Manual);
            section.ProxyAddress = Address;
            section.ProxyUsername = Username;
            section.ProxyPasswordInput = Password;

            await section.TestConnectionCommand.ExecuteAsync();

            ranOffUiThread.Should().BeTrue();
            appliedDuringTest.Should().NotBeNull();
            appliedDuringTest!.ProxyAddress.Should().Be(Address, "the draft is saved and applied before probing");
            appliedDuringTest.HasProxyPassword.Should().BeTrue();
            PatchouliAppSettings.Load(settings.Path).Network.ProxyAddress.Should().Be(Address);
            section.IsDirty.Should().BeFalse();
            section.IsTesting.Should().BeFalse();
            section.ConnectionResultText.Should().Contain(expected);
            section.ConnectionResultText.Should().NotContain("proxy.corp").And.NotContain(Username)
                .And.NotContain(Password).And.NotContain("raw").And.NotContain("exception");
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Connection_test_with_an_invalid_draft_does_not_probe_or_save()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = CreateMain(settings);
            NetworkProxySettingsViewModel section = main.Settings.NetworkProxySettings;
            bool probed = false;
            section.UseConnectionTester((_, _) =>
            {
                probed = true;
                return Task.FromResult(NetworkConnectionTestResult.Succeeded());
            });
            section.SelectedMode = section.ModeOptions.Single(option => option.Value == NetworkProxyModes.Manual);
            section.ProxyAddress = "ftp://proxy.invalid";

            await section.TestConnectionCommand.ExecuteAsync();

            probed.Should().BeFalse();
            section.ConnectionResultText.Should().Be("代理设置无效或未保存");
            section.IsDirty.Should().BeTrue();
            PatchouliAppSettings.Load(settings.Path).Network.ProxyMode.Should().Be(NetworkProxyModes.System);
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_connection_result_from_the_previous_library_is_ignored_after_a_switch()
    {
        await headless.Session.Dispatch<int>(async () =>
        {
            using TemporaryAppSettingsFile settings = new();
            await using MainWindowViewModel main = CreateMain(settings);
            await main.OpenDatabaseCommand.ExecuteAsync();
            await main.Library.CreateCommand.ExecuteAsync();
            NetworkProxySettingsViewModel section = main.Settings.NetworkProxySettings;
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<NetworkConnectionTestResult> release =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            section.UseConnectionTester((_, _) =>
            {
                started.TrySetResult();
                return release.Task;
            });
            section.SelectedMode = section.ModeOptions.Single(option => option.Value == NetworkProxyModes.None);

            Task pending = section.TestConnectionCommand.ExecuteAsync();
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                section.IsTesting.Should().BeTrue();

                main.RuntimeDatabasePath = settings.CreateDatabasePath("switched");
                await main.OpenDatabaseCommand.ExecuteAsync();
                section.IsTesting.Should().BeFalse();
            }
            finally
            {
                release.TrySetResult(NetworkConnectionTestResult.Succeeded());
                await pending.WaitAsync(TimeSpan.FromSeconds(10));
            }

            section.ConnectionResultText.Should().BeEmpty("a result from the previous library host is discarded");
            section.IsTesting.Should().BeFalse();
            return 0;
        }, CancellationToken.None);
    }

    private static MainWindowViewModel CreateMain(TemporaryAppSettingsFile settings, RecordingPolicy? policy = null)
    {
        return new MainWindowViewModel(settingsPath: settings.Path,
            appPaths: new TestAppPaths(Path.GetDirectoryName(settings.Path)!),
            infrastructure: new HostInfrastructureOptions
            {
                HttpTransportPolicy = policy,
                HttpHandlerFactory = _ => new OfflineHandler()
            });
    }

    private sealed class RecordingPolicy : IHttpTransportPolicy
    {
        private NetworkAppSettings _settings = NetworkAppSettings.Default();

        public int InvalidateCount { get; private set; }

        public NetworkAppSettings Settings => Volatile.Read(ref _settings);

        public void ConfigureHandler(SocketsHttpHandler handler)
        {
            handler.UseProxy = false;
        }

        public void Update(NetworkAppSettings settings)
        {
            Volatile.Write(ref _settings, settings);
            InvalidateCount++;
        }

        public void InvalidateCredentials()
        {
            InvalidateCount++;
        }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
