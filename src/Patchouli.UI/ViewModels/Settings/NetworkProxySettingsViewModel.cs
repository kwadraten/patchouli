using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Credentials;
using Patchouli.Core.Results;
using Patchouli.Core.Settings;
using Patchouli.Host.Composition;
using Patchouli.Host.Network;
using Patchouli.Host.Settings;

namespace Patchouli.UI.ViewModels.Settings;

public sealed class NetworkProxyModeOption
{
    public NetworkProxyModeOption(string value, string displayName)
    {
        Value = value;
        DisplayName = displayName;
    }

    public string Value { get; }

    public string DisplayName { get; }

    public static IReadOnlyList<NetworkProxyModeOption> All { get; } =
    [
        new(NetworkProxyModes.System, "系统代理"),
        new(NetworkProxyModes.Manual, "手动"),
        new(NetworkProxyModes.None, "不使用代理")
    ];

    public static NetworkProxyModeOption FromValue(string? value)
    {
        string normalized = NetworkProxyModes.Normalize(value);
        return All.First(option => option.Value == normalized);
    }
}

/// <summary>
/// 「网络代理」section (issue #56). Edits the device-local "Network" settings through the draft +
/// auto-save pipeline. The proxy password is written only to the credential store under
/// <see cref="ProviderIds.NetworkProxy"/>; the saved secret is never read back into the input, and
/// neither the address, username, password nor raw exception text reaches status or results.
/// </summary>
public sealed partial class NetworkProxySettingsViewModel : SettingsSectionViewModelBase
{
    private const string CredentialDisplayName = "Network proxy password";

    private readonly MainWindowViewModel _main;
    private NetworkAppSettings _persisted;
    private bool _isDirty;
    private bool _isSyncing;
    private int _testGeneration;
    private CancellationTokenSource? _testCancellation;

    private Func<HostServices, CancellationToken, Task<NetworkConnectionTestResult>> _connectionTester =
        (services, cancellationToken) => services.NetworkConnectionTest.TestAsync(null, cancellationToken);

    public NetworkProxySettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
        _persisted = main.AppOptions.Network.Normalize();
        TestConnectionCommand = new AsyncCommand(TestConnectionAsync);
        CancelTestCommand = new AsyncCommand(CancelTestAsync);
        ClearCredentialCommand = new AsyncCommand(ClearCredentialAsync);
        SyncFromPersisted();
    }

    public IReadOnlyList<NetworkProxyModeOption> ModeOptions => NetworkProxyModeOption.All;

    [ObservableProperty]
    public partial NetworkProxyModeOption SelectedMode { get; set; } =
        NetworkProxyModeOption.FromValue(NetworkProxyModes.System);

    [ObservableProperty] public partial string ProxyAddress { get; set; } = "";

    /// <summary>Bypass hosts, one entry per line.</summary>
    [ObservableProperty]
    public partial string BypassText { get; set; } = "";

    [ObservableProperty] public partial string ProxyUsername { get; set; } = "";

    /// <summary>Write-only password input; cleared after a successful save and never filled from the store.</summary>
    [ObservableProperty]
    public partial string ProxyPasswordInput { get; set; } = "";

    [ObservableProperty] public partial bool HasProxyPassword { get; private set; }

    [ObservableProperty] public partial string ValidationText { get; private set; } = "";

    [ObservableProperty] public partial string ConnectionResultText { get; private set; } = "";

    [ObservableProperty] public partial bool IsTesting { get; private set; }

    [ExcludeFromDerivedGeneration] public bool IsManual => SelectedMode.Value == NetworkProxyModes.Manual;

    [ExcludeFromDerivedGeneration] public bool HasValidationText => ValidationText.Length > 0;

    [ExcludeFromDerivedGeneration] public bool HasConnectionResult => ConnectionResultText.Length > 0;

    [ExcludeFromDerivedGeneration] public string PasswordStateText => HasProxyPassword ? "已保存（留空则不变）" : "可选";

    public AsyncCommand TestConnectionCommand { get; }
    public AsyncCommand CancelTestCommand { get; }
    public AsyncCommand ClearCredentialCommand { get; }

    public override bool SupportsEditing => true;

    [ExcludeFromDerivedGeneration] public override bool IsDirty => _isDirty;

    [ExcludeFromDerivedGeneration] public override bool CanSave => _isDirty && !IsSaving;

    partial void OnSelectedModeChanged(NetworkProxyModeOption value)
    {
        Raise(nameof(IsManual));
        OnDraftEdited();
    }

    partial void OnProxyAddressChanged(string value)
    {
        OnDraftEdited();
    }

    partial void OnBypassTextChanged(string value)
    {
        OnDraftEdited();
    }

    partial void OnProxyUsernameChanged(string value)
    {
        OnDraftEdited();
    }

    partial void OnProxyPasswordInputChanged(string value)
    {
        OnDraftEdited();
    }

    partial void OnHasProxyPasswordChanged(bool value)
    {
        Raise(nameof(PasswordStateText));
    }

    partial void OnValidationTextChanged(string value)
    {
        Raise(nameof(HasValidationText));
    }

    partial void OnConnectionResultTextChanged(string value)
    {
        Raise(nameof(HasConnectionResult));
    }

    public override Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!IsDirty)
        {
            SyncFromPersisted();
        }

        return Task.CompletedTask;
    }

    public override async Task SaveAsync()
    {
        if (!CanSave)
        {
            return;
        }

        NetworkAppSettings draft = BuildDraft();
        string? problem = DescribeValidation(draft);
        if (problem is not null)
        {
            ValidationText = problem;
            FailSave(problem, SettingsValidationState.Invalid);
            return;
        }

        SaveState = SettingsSaveState.Saving;
        Status = "正在保存...";
        string password = ProxyPasswordInput;
        bool storePassword = !string.IsNullOrWhiteSpace(password);
        HostServices? services = null;
        try
        {
            if (storePassword)
            {
                services = await _main.ServicesAsync();
                Result<ProviderCredentialMetadata> stored = await Task.Run(() =>
                    services.Credentials.SaveAsync(ProviderIds.NetworkProxy, CredentialDisplayName, password));
                if (stored.IsFailure)
                {
                    FailSave("无法保存代理密码。", SettingsValidationState.Valid);
                    return;
                }

                draft = draft with { HasProxyPassword = true };
            }

            SettingsSaveResult saved = SettingsEqual(draft, _persisted) && !storePassword
                ? SettingsSaveResult.Success
                : await _main.UpdateAppOptionsAsync(_main.AppOptions with { Network = draft }, "Network");
            if (!saved.IsSuccess)
            {
                FailSave("无法保存网络代理设置。", SettingsValidationState.Valid);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            FailSave("书库正在切换，代理设置未保存。", SettingsValidationState.Valid);
            return;
        }
        catch (Exception)
        {
            FailSave("无法保存网络代理设置。", SettingsValidationState.Valid);
            return;
        }

        _persisted = draft;
        HasProxyPassword = draft.HasProxyPassword;
        if (storePassword)
        {
            SetWithoutDirtyTracking(() => ProxyPasswordInput = "");
            services?.HttpTransportPolicy.InvalidateCredentials();
        }

        RefreshDirtyState();
        ValidationText = "";
        LastError = null;
        ValidationState = SettingsValidationState.Valid;
        SaveState = IsDirty ? SettingsSaveState.Dirty : SettingsSaveState.Saved;
        Status = "已保存";
    }

    public override Task DiscardAsync()
    {
        SyncFromPersisted();
        LastError = null;
        SaveState = SettingsSaveState.Clean;
        ValidationState = SettingsValidationState.Unknown;
        Status = "";
        return Task.CompletedTask;
    }

    /// <summary>Drops any in-flight connection test: its result belongs to the previous Library host.</summary>
    public void NotifyLibraryContextChanged()
    {
        _testGeneration++;
        _testCancellation?.Cancel();
        IsTesting = false;
        ConnectionResultText = "";
    }

    /// <summary>Test seam: replaces the connectivity probe while keeping the save/apply/guard flow.</summary>
    internal void UseConnectionTester(
        Func<HostServices, CancellationToken, Task<NetworkConnectionTestResult>> tester)
    {
        ArgumentNullException.ThrowIfNull(tester);
        _connectionTester = tester;
    }

    internal static string DescribeTestResult(NetworkConnectionTestResult result)
    {
        string status = result.HttpStatus is { } code ? $"（HTTP {code}）" : "";
        return result.Kind switch
        {
            NetworkConnectionTestKinds.Ok => "连接成功",
            NetworkConnectionTestKinds.ProxyTunnel => $"代理拒绝建立连接{status}",
            NetworkConnectionTestKinds.Tls => "TLS 握手失败。若代理会解密 HTTPS，请在系统钥匙串或证书库中信任其 CA 证书",
            NetworkConnectionTestKinds.Dns => "无法解析域名",
            NetworkConnectionTestKinds.Connection => "无法建立连接",
            NetworkConnectionTestKinds.Authentication => $"认证失败，请检查用户名和密码{status}",
            NetworkConnectionTestKinds.Timeout => "连接超时",
            NetworkConnectionTestKinds.Http => $"服务器返回错误{status}",
            NetworkConnectionTestKinds.InvalidConfiguration => "代理设置无效",
            NetworkConnectionTestKinds.Canceled => "已取消",
            _ => "连接测试失败"
        };
    }

    internal static string? DescribeValidation(NetworkAppSettings settings)
    {
        IReadOnlyList<string> errors = settings.Validate();
        if (errors.Count == 0)
        {
            return null;
        }

        return errors[0] switch
        {
            "proxy_address_required" => "请填写代理地址",
            "proxy_scheme_unsupported" => "仅支持 http/https/socks5/socks5h",
            "proxy_credentials_in_url" => "地址中不能包含用户名或密码",
            _ => "代理地址格式无效"
        };
    }

    private async Task TestConnectionAsync(CancellationToken commandToken)
    {
        _testCancellation?.Cancel();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(commandToken);
        CancellationToken cancellationToken = cancellation.Token;
        _testCancellation = cancellation;
        int token = ++_testGeneration;
        IsTesting = true;
        ConnectionResultText = "正在测试...";
        try
        {
            if (IsDirty)
            {
                await SaveAsync();
            }

            if (token != _testGeneration)
            {
                return;
            }

            if (IsDirty || DescribeValidation(_persisted) is not null)
            {
                ConnectionResultText = "代理设置无效或未保存";
                return;
            }

            HostServices services = await _main.ServicesAsync();
            int libraryGeneration = _main.LibraryGeneration;
            NetworkAppSettings applied = _persisted;
            services.UpdateNetworkSettings(applied);
            Func<HostServices, CancellationToken, Task<NetworkConnectionTestResult>> tester = _connectionTester;
            NetworkConnectionTestResult result = await Task.Run(
                () => tester(services, cancellationToken), CancellationToken.None);
            if (token != _testGeneration)
            {
                return;
            }

            ConnectionResultText = _main.IsCurrentLibraryContext(services, libraryGeneration)
                ? DescribeTestResult(result)
                : "";
        }
        catch (OperationCanceledException)
        {
            if (token == _testGeneration)
            {
                ConnectionResultText = cancellationToken.IsCancellationRequested ? "已取消" : "书库正在切换，请稍后重试";
            }
        }
        catch (Exception)
        {
            if (token == _testGeneration)
            {
                ConnectionResultText = "连接测试失败";
            }
        }
        finally
        {
            if (ReferenceEquals(_testCancellation, cancellation))
            {
                _testCancellation = null;
            }

            if (token == _testGeneration)
            {
                IsTesting = false;
            }
        }
    }

    private Task CancelTestAsync()
    {
        _testCancellation?.Cancel();
        return Task.CompletedTask;
    }

    private async Task ClearCredentialAsync()
    {
        try
        {
            HostServices services = await _main.ServicesAsync();
            int libraryGeneration = _main.LibraryGeneration;
            Result removed = await Task.Run(() => services.Credentials.RemoveAsync(ProviderIds.NetworkProxy));
            if (removed.IsFailure)
            {
                Status = "无法清除凭据";
                return;
            }

            NetworkAppSettings next = _main.AppOptions.Network.Normalize() with { HasProxyPassword = false };
            SettingsSaveResult saved = await _main.UpdateAppOptionsAsync(
                _main.AppOptions with { Network = next }, "Network");
            if (_main.IsCurrentLibraryContext(services, libraryGeneration))
            {
                services.HttpTransportPolicy.InvalidateCredentials();
            }

            if (!saved.IsSuccess)
            {
                Status = "凭据已清除，但设置未保存";
                return;
            }

            _persisted = _persisted with { HasProxyPassword = false };
            HasProxyPassword = false;
            SetWithoutDirtyTracking(() => ProxyPasswordInput = "");
            RefreshDirtyState();
            Status = "凭据已清除";
        }
        catch (OperationCanceledException)
        {
            Status = "书库正在切换，请稍后重试";
        }
        catch (Exception)
        {
            Status = "无法清除凭据";
        }
    }

    private void OnDraftEdited()
    {
        if (_isSyncing)
        {
            return;
        }

        RefreshDirtyState();
        string? problem = DescribeValidation(BuildDraft());
        ValidationText = problem ?? "";
        ValidationState = problem is null ? SettingsValidationState.Valid : SettingsValidationState.Invalid;
        LastError = problem;
        SaveState = SettingsSaveState.Dirty;
        Status = "有未保存的更改";
    }

    private void FailSave(string message, SettingsValidationState validation)
    {
        LastError = message;
        ValidationState = validation;
        SaveState = SettingsSaveState.Failed;
        Status = validation == SettingsValidationState.Invalid ? $"{message}，未保存" : "保存失败";
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
    }

    private NetworkAppSettings BuildDraft()
    {
        string[] bypass =
            BypassText.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return new NetworkAppSettings(SelectedMode.Value, ProxyAddress, ProxyUsername, _persisted.HasProxyPassword,
            bypass).Normalize();
    }

    private void SyncFromPersisted()
    {
        _persisted = _main.AppOptions.Network.Normalize();
        SetWithoutDirtyTracking(() =>
        {
            SelectedMode = NetworkProxyModeOption.FromValue(_persisted.ProxyMode);
            ProxyAddress = _persisted.ProxyAddress;
            BypassText = string.Join(Environment.NewLine, _persisted.BypassList);
            ProxyUsername = _persisted.ProxyUsername;
            ProxyPasswordInput = "";
        });
        HasProxyPassword = _persisted.HasProxyPassword;
        ValidationText = DescribeValidation(_persisted) ?? "";
        _isDirty = false;
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
    }

    private void SetWithoutDirtyTracking(Action update)
    {
        bool previous = _isSyncing;
        _isSyncing = true;
        try
        {
            update();
        }
        finally
        {
            _isSyncing = previous;
        }
    }

    private void RefreshDirtyState()
    {
        _isDirty = !SettingsEqual(BuildDraft(), _persisted) || !string.IsNullOrWhiteSpace(ProxyPasswordInput);
        Raise(nameof(IsDirty));
        Raise(nameof(CanSave));
    }

    private static bool SettingsEqual(NetworkAppSettings left, NetworkAppSettings right)
    {
        return left.ProxyMode == right.ProxyMode &&
               left.ProxyAddress == right.ProxyAddress &&
               left.ProxyUsername == right.ProxyUsername &&
               left.HasProxyPassword == right.HasProxyPassword &&
               left.BypassList.SequenceEqual(right.BypassList, StringComparer.Ordinal);
    }
}
