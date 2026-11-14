using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.UI.Diagnostics;
using Patchouli.UI.Services;

namespace Patchouli.UI.ViewModels.Settings;

public sealed partial class VirtualLinkSettingsViewModel : SettingsSectionViewModelBase
{
    private readonly MainWindowViewModel _main;
    private readonly IProtocolRegistrationService _registration;
    private bool _isSyncing;

    public VirtualLinkSettingsViewModel(MainWindowViewModel main)
        : this(main, new ProtocolRegistrationService())
    {
    }

    internal VirtualLinkSettingsViewModel(MainWindowViewModel main, IProtocolRegistrationService registration)
    {
        _main = main;
        _registration = registration;
        Refresh();
    }

    [ObservableProperty] public partial bool RegisterProtocol { get; set; }
    [ObservableProperty] public partial bool ParseLinksInSearchBar { get; set; }
    [ObservableProperty] public partial bool ActivateWindowOnProtocolOpen { get; set; } = true;
    [ObservableProperty] public partial bool RegistrationSupported { get; private set; }
    [ObservableProperty] public partial bool IsRegistering { get; private set; }
    [ObservableProperty] public partial string RegistrationStatus { get; private set; } = "";

    [ExcludeFromDerivedGeneration] public bool CanChangeRegistration => RegistrationSupported && !IsRegistering;
    internal Task RegistrationTask { get; private set; } = Task.CompletedTask;

    public override bool SupportsEditing => true;
    public override bool IsDirty => false;
    public override bool CanSave => false;

    partial void OnIsRegisteringChanged(bool value)
    {
        Raise(nameof(CanChangeRegistration));
    }

    partial void OnRegistrationSupportedChanged(bool value)
    {
        Raise(nameof(CanChangeRegistration));
    }

    partial void OnRegisterProtocolChanged(bool value)
    {
        if (_isSyncing)
        {
            return;
        }

        RegistrationTask = ChangeRegistrationAsync(value);
        RegistrationTask.Observe("settings", "register-virtual-link-protocol");
    }

    partial void OnParseLinksInSearchBarChanged(bool value)
    {
        if (!_isSyncing)
        {
            _main.UpdateAppOptionsDeferred(current => current with
            {
                Ui = current.Ui with { ParseVirtualLinksInSearchBar = value }
            }, "Ui");
        }
    }

    partial void OnActivateWindowOnProtocolOpenChanged(bool value)
    {
        if (!_isSyncing)
        {
            _main.UpdateAppOptionsDeferred(current => current with
            {
                Ui = current.Ui with { ActivateWindowOnProtocolOpen = value }
            }, "Ui");
        }
    }

    public override Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRegistering)
        {
            Refresh();
        }

        return Task.CompletedTask;
    }

    public override Task SaveAsync()
    {
        return Task.CompletedTask;
    }

    public override Task DiscardAsync()
    {
        return LoadAsync();
    }

    private void Refresh()
    {
        _isSyncing = true;
        try
        {
            ParseLinksInSearchBar = _main.AppOptions.Ui.ParseVirtualLinksInSearchBar;
            ActivateWindowOnProtocolOpen = _main.AppOptions.Ui.ActivateWindowOnProtocolOpen;
            ProtocolRegistrationState state = _registration.GetState();
            RegistrationSupported = state.IsSupported;
            RegisterProtocol = state.IsRegistered;
            RegistrationStatus = state.Detail;
        }
        catch (Exception exception)
        {
            RegistrationSupported = false;
            RegistrationStatus = $"无法读取协议注册状态：{exception.Message}";
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private async Task ChangeRegistrationAsync(bool registered)
    {
        if (IsRegistering)
        {
            return;
        }

        IsRegistering = true;
        RegistrationStatus = registered ? "正在注册…" : "正在取消注册…";
        string? error = null;
        try
        {
            await Task.Run(() => _registration.SetRegistered(registered));
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
        finally
        {
            Refresh();
            if (error is not null)
            {
                RegistrationStatus = $"操作失败：{error}";
            }

            IsRegistering = false;
        }
    }
}
