using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Patchouli.UI.Services;

public sealed record ProtocolRegistrationState(bool IsSupported, bool IsRegistered, string Detail);

public interface IProtocolRegistrationService
{
    ProtocolRegistrationState GetState();
    void SetRegistered(bool registered);
}

/// <summary>Device-local, current-user Windows URL registration, shared with the installer.</summary>
internal sealed partial class ProtocolRegistrationService : IProtocolRegistrationService
{
    internal const string RegistryPath = @"Software\Classes\patchouli";
    internal const string OwnerValue = "PatchouliProtocolOwner";
    internal const string Owner = "net.patchouli.app";
    private const string PreferencesPath = @"Software\Patchouli.NET";
    private readonly string _registryPath;
    private readonly string _executablePath;

    public ProtocolRegistrationService() : this(RegistryPath,
        Path.Combine(AppContext.BaseDirectory, "Patchouli.UI.exe"))
    {
    }

    internal ProtocolRegistrationService(string registryPath, string executablePath)
    {
        _registryPath = registryPath;
        _executablePath = Path.GetFullPath(executablePath);
    }

    public ProtocolRegistrationState GetState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ProtocolRegistrationState(false, false, "当前平台暂不支持在应用内注册协议。");
        }

        return ReadWindowsState();
    }

    public void SetRegistered(bool registered)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("当前平台暂不支持在应用内注册协议。");
        }

        SetWindowsRegistration(registered);
    }

    [SupportedOSPlatform("windows")]
    private ProtocolRegistrationState ReadWindowsState()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_registryPath);
        using RegistryKey? command = key?.OpenSubKey(@"shell\open\command");
        bool registered = key is not null && key.GetValue(OwnerValue) as string == Owner &&
                          key.GetValue("URL Protocol") is string &&
                          command?.GetValue(null) as string == BuildCommand(_executablePath);
        return new ProtocolRegistrationState(true, registered,
            registered ? "已为当前用户注册 patchouli:// 链接。" : "尚未为当前应用注册 patchouli:// 链接。");
    }

    internal static string BuildCommand(string executablePath)
    {
        if (executablePath.Contains('"'))
        {
            throw new ArgumentException("可执行文件路径不能含引号。", nameof(executablePath));
        }

        return $"\"{executablePath}\" --open-uri \"%1\"";
    }

    [SupportedOSPlatform("windows")]
    private void SetWindowsRegistration(bool registered)
    {
        using (RegistryKey? existing = Registry.CurrentUser.OpenSubKey(_registryPath))
        {
            if (existing is not null && existing.GetValue(OwnerValue) as string != Owner)
            {
                throw new InvalidOperationException("patchouli 协议已由其他程序注册，请先在系统设置中更改关联。");
            }
        }

        if (registered)
        {
            if (!File.Exists(_executablePath))
            {
                throw new FileNotFoundException("找不到 Patchouli 桌面程序。", _executablePath);
            }

            using RegistryKey key = Registry.CurrentUser.CreateSubKey(_registryPath);
            key.SetValue(null, "URL:Patchouli Literature Link");
            key.SetValue("URL Protocol", "");
            key.SetValue(OwnerValue, Owner);
            using RegistryKey icon = key.CreateSubKey("DefaultIcon");
            icon.SetValue(null, $"\"{_executablePath}\",0");
            using RegistryKey command = key.CreateSubKey(@"shell\open\command");
            command.SetValue(null, BuildCommand(_executablePath));
        }
        else
        {
            Registry.CurrentUser.DeleteSubKeyTree(_registryPath, false);
        }

        if (_registryPath == RegistryPath)
        {
            using RegistryKey preferences = Registry.CurrentUser.CreateSubKey(PreferencesPath);
            preferences.SetValue("ProtocolRegistrationDisabled", registered ? 0 : 1);
        }

        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
