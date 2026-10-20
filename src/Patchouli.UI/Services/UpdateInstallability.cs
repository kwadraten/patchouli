using Microsoft.Win32;
using System.Runtime.Versioning;

namespace Patchouli.UI.Services;

internal static class UpdateInstallability
{
    private const string InnoSetupUninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{DCBB7F21-2751-4C90-A9B4-9459523CFF70}_is1";

    public static bool CanInstallInPlace()
    {
        if (!OperatingSystem.IsWindows())
        {
            // AppImage, deb/rpm and Flatpak are owned by the container or system
            // package manager. The DMG is a manual drag-to-Applications workflow.
            return false;
        }

        string installDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        if (IsKnownExternalManagerPath(installDirectory))
        {
            return false;
        }

        try
        {
            return IsSupportedInstall(true, IsKnownExternalManagerPath(installDirectory),
                HasMatchingInnoInstallRecord(installDirectory));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.Security.SecurityException or ArgumentException)
        {
            // Any uncertainty falls back to the GitHub Release manual path.
            return false;
        }
    }

    internal static bool IsSupportedInstall(bool isWindows, bool isExternalManagerPath,
        bool hasMatchingInnoRecord)
    {
        return isWindows && !isExternalManagerPath && hasMatchingInnoRecord;
    }

    internal static bool IsKnownExternalManagerPath(string installDirectory)
    {
        string normalized = installDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string[] managedRoots =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "apps"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "lib"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps")
        ];

        return managedRoots.Where(static path => !string.IsNullOrWhiteSpace(path))
            .Any(root => normalized.StartsWith(
                Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    [SupportedOSPlatform("windows")]
    private static bool HasMatchingInnoInstallRecord(string installDirectory)
    {
        foreach (RegistryKey hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using RegistryKey? key = hive.OpenSubKey(InnoSetupUninstallKey);
            string? registeredDirectory = key?.GetValue("InstallLocation") as string;
            if (!string.IsNullOrWhiteSpace(registeredDirectory) &&
                InstallPathsMatch(registeredDirectory, installDirectory))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeDirectory(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    internal static bool InstallPathsMatch(string registeredDirectory, string installDirectory)
    {
        return string.Equals(NormalizeDirectory(registeredDirectory), NormalizeDirectory(installDirectory),
            StringComparison.OrdinalIgnoreCase);
    }
}
