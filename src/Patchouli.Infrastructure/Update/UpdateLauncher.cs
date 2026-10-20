using System.Diagnostics;
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
/// <item>macOS and Linux: manual GitHub Release installation only.</item>
/// </list>
/// </summary>
public sealed class UpdateLauncher
{
    /// <summary>
    /// Installer extensions the launcher knows how to start, matched
    /// case-insensitively against the file extension. Anything else is
    /// rejected before any launch is attempted (see <see cref="LaunchAsync"/>).
    /// </summary>
    internal static readonly string[] KnownInstallerExtensions = [".exe"];

    internal static bool CanLaunchInstaller(bool isWindows, string? path)
    {
        return isWindows && !string.IsNullOrWhiteSpace(path) &&
               KnownInstallerExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

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
            if (!CanLaunchInstaller(OperatingSystem.IsWindows(), installerPath))
            {
                return Task.FromResult(Result.Failure(AppErrorCodes.ValidationFailed,
                    "此安装包必须从 GitHub Release 页面手动安装。"));
            }

            ProcessStartInfo startInfo = new()
            {
                FileName = installerPath,
                UseShellExecute = true,
            };

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

}
