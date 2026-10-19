using System.Diagnostics;
using System.Runtime.InteropServices;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Update;

/// <summary>
/// Issue #15: launches a verified installer for the current platform.
/// The caller must only pass a path produced by <see cref="UpdateDownloader"/>
/// (a fully downloaded and verified file); this launcher never downloads or
/// verifies anything itself.
/// <list type="bullet">
/// <item>Windows: starts the Inno Setup <c>.exe</c> (it elevates itself when needed).</item>
/// <item>macOS: <c>open</c>s the <c>.dmg</c> so the user can drag the app into
/// <c>/Applications</c> — mounting is the standard DMG update step.</item>
/// <item>Linux: marks the AppImage executable and starts it directly.</item>
/// </list>
/// </summary>
public sealed class UpdateLauncher
{
    /// <summary>
    /// Installer extensions the launcher knows how to start, matched
    /// case-insensitively against the file extension. Anything else is
    /// rejected before any launch is attempted (see <see cref="LaunchAsync"/>).
    /// </summary>
    private static readonly string[] KnownInstallerExtensions = [".exe", ".dmg", ".AppImage"];

    public Task<Result> LaunchAsync(string installerPath, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
            {
                return Task.FromResult(Result.Failure(AppErrorCodes.ValidationFailed,
                    "安装包文件不存在，无法启动安装。"));
            }

            // Reject unknown installer kinds explicitly: without this guard a
            // ".tmp" fallback file would reach the UseShellExecute fallback
            // below, which on Linux reports success via xdg-open while
            // installing nothing.
            if (!KnownInstallerExtensions.Contains(Path.GetExtension(installerPath),
                    StringComparer.OrdinalIgnoreCase))
            {
                return Task.FromResult(Result.Failure(AppErrorCodes.ValidationFailed,
                    "不支持的安装包类型，仅支持 .exe、.dmg、.AppImage。"));
            }

            ProcessStartInfo startInfo;
            if (OperatingSystem.IsWindows() &&
                installerPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = installerPath,
                    UseShellExecute = true,
                };
            }
            else if (OperatingSystem.IsMacOS() &&
                     installerPath.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase))
            {
                startInfo = new ProcessStartInfo("open")
                {
                    UseShellExecute = false,
                };
                startInfo.ArgumentList.Add(installerPath);
            }
            else if (OperatingSystem.IsLinux() &&
                     installerPath.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase))
            {
                MakeExecutable(installerPath);
                startInfo = new ProcessStartInfo
                {
                    FileName = installerPath,
                    UseShellExecute = false,
                };
            }
            else
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = installerPath,
                    UseShellExecute = true,
                };
            }

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return Task.FromResult(Result.Failure(AppErrorCodes.ValidationFailed,
                    "安装程序未能启动。"));
            }

            return Task.FromResult(Result.Success());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.update-launcher"))
        {
            return Task.FromResult(Result.Failure(AppErrorCodes.ValidationFailed,
                $"安装程序启动失败：{exception.Message}"));
        }
    }

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path,
                mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or PlatformNotSupportedException)
        {
            _ = exception;
        }
    }
}
