using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Patchouli.Core.Cli;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Cli;

public sealed class CliPathService : ICliPathService
{
    private static readonly string CliFileName = OperatingSystem.IsWindows() ? "patchouli-cli.exe" : "patchouli-cli";
    private const string MacOsBinLink = "/usr/local/bin/patchouli-cli";
    private const string UserPathRegistryKey = "Environment";
    private const string UserPathRegistryValue = "Path";

    public CliPathService(string? baseDirectory = null)
    {
        _baseDirectory = baseDirectory ?? AppContext.BaseDirectory;
    }

    private readonly string _baseDirectory;

    public CliInstallation GetInstallation()
    {
        string? path = LocateCli();
        if (path is null)
        {
            return new CliInstallation(null, null, false);
        }

        string? version = null;
        try
        {
            version = FileVersionInfo.GetVersionInfo(path).FileVersion;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return new CliInstallation(path, version, IsCliInPath());
    }

    public Result AddToPath()
    {
        if (OperatingSystem.IsMacOS())
        {
            return AddMacOsSymlink();
        }

        if (!OperatingSystem.IsWindows())
        {
            return Result.Failure(AppErrorCodes.UnsupportedOperation,
                "PATH registration is only supported on Windows and macOS.");
        }

        string? path = LocateCli();
        if (path is null)
        {
            return Result.Failure(AppErrorCodes.NotFound,
                "patchouli-cli was not found next to the application; PATH registration is unavailable.");
        }

        string directory = Path.GetDirectoryName(path)!;
        string userPath = ReadUserPath();
        string[] segments = userPath.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => string.Equals(segment.TrimEnd('\\'), directory.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase)))
        {
            return Result.Success();
        }

        try
        {
            string next = string.IsNullOrWhiteSpace(userPath) ? directory : $"{userPath};{directory}";
            WriteUserPath(next);
            return Result.Success();
        }
        catch (IOException exception)
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"无法更新用户 PATH：{exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"无法更新用户 PATH：{exception.Message}");
        }
    }

    public Result RemoveFromPath()
    {
        if (OperatingSystem.IsMacOS())
        {
            return RemoveMacOsSymlink();
        }

        if (!OperatingSystem.IsWindows())
        {
            return Result.Failure(AppErrorCodes.UnsupportedOperation,
                "PATH registration is only supported on Windows and macOS.");
        }

        string? path = LocateCli();
        if (path is null)
        {
            return Result.Success();
        }

        string directory = Path.GetDirectoryName(path)!.TrimEnd('\\');
        string userPath = ReadUserPath();
        string[] segments = userPath.Split(';', StringSplitOptions.RemoveEmptyEntries);
        string[] remaining = segments
            .Where(segment => !string.Equals(segment.TrimEnd('\\'), directory, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (remaining.Length == segments.Length)
        {
            return Result.Success();
        }

        try
        {
            WriteUserPath(string.Join(';', remaining));
            return Result.Success();
        }
        catch (IOException exception)
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"无法更新用户 PATH：{exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"无法更新用户 PATH：{exception.Message}");
        }
    }

    public string? LocateCli()
    {
        string installed = Path.Combine(_baseDirectory, "cli", CliFileName);
        if (File.Exists(installed))
        {
            return installed;
        }

        string bundled = Path.Combine(_baseDirectory, CliFileName);
        return File.Exists(bundled) ? bundled : null;
    }

    private bool IsCliInPath()
    {
        string? path = LocateCli();
        if (path is null)
        {
            return false;
        }

        if (OperatingSystem.IsMacOS())
        {
            return File.Exists(MacOsBinLink);
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        string directory = Path.GetDirectoryName(path)!.TrimEnd('\\');
        return ReadUserPath().Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment.TrimEnd('\\'), directory, StringComparison.OrdinalIgnoreCase));
    }

    [SupportedOSPlatform("windows")]
    private static string ReadUserPath()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(UserPathRegistryKey);
        return key?.GetValue(UserPathRegistryValue) as string ?? string.Empty;
    }

    [SupportedOSPlatform("windows")]
    private static void WriteUserPath(string next)
    {
        using RegistryKey? key = Registry.CurrentUser.CreateSubKey(UserPathRegistryKey);
        key?.SetValue(UserPathRegistryValue, next, RegistryValueKind.ExpandString);
        NotifyEnvironmentChanged();
    }

    [SupportedOSPlatform("windows")]
    private static void NotifyEnvironmentChanged()
    {
        _ = SendMessageTimeout(new IntPtr(0xFFFF), WmSettingChange, IntPtr.Zero, "Environment", SmtoAbortIfHung, 1000,
            out _);
    }

    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint fuFlags,
        uint uTimeout, out IntPtr result);

    private Result AddMacOsSymlink()
    {
        string? path = LocateCli();
        if (path is null)
        {
            return Result.Failure(AppErrorCodes.NotFound,
                "patchouli-cli was not found next to the application; PATH registration is unavailable.");
        }

        try
        {
            if (File.Exists(MacOsBinLink))
            {
                FileInfo link = new(MacOsBinLink);
                if (link.LinkTarget is null)
                {
                    return Result.Failure(AppErrorCodes.InvalidState,
                        $"{MacOsBinLink} exists but is not a symlink; it was not modified.");
                }
            }

            File.CreateSymbolicLink(MacOsBinLink, path);
            return Result.Success();
        }
        catch (IOException exception)
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"无法创建 /usr/local/bin 符号链接：{exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"无法创建 /usr/local/bin 符号链接：{exception.Message}");
        }
    }

    private Result RemoveMacOsSymlink()
    {
        try
        {
            if (!File.Exists(MacOsBinLink))
            {
                return Result.Success();
            }

            FileInfo link = new(MacOsBinLink);
            if (link.LinkTarget is null)
            {
                return Result.Success();
            }

            string? target = Path.GetFullPath(link.LinkTarget);
            string? installed = LocateCli();
            if (installed is not null &&
                !string.Equals(target, Path.GetFullPath(installed), StringComparison.Ordinal))
            {
                return Result.Success();
            }

            File.Delete(MacOsBinLink);
            return Result.Success();
        }
        catch (IOException exception)
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"无法移除 /usr/local/bin 符号链接：{exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Result.Failure(AppErrorCodes.DatabaseError, $"无法移除 /usr/local/bin 符号链接：{exception.Message}");
        }
    }
}
