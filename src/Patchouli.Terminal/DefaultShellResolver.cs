using System.ComponentModel;
using System.Diagnostics;

namespace Patchouli.Terminal;

/// <summary>
/// An executable and argument list suitable for <see cref="TerminalSessionOptions"/>.
/// </summary>
public sealed record ShellCommand(string Executable, IReadOnlyList<string> Arguments);

/// <summary>
/// Resolves the preferred interactive shell: PowerShell 7, then Windows PowerShell 5.1, then bash.
/// </summary>
public static class DefaultShellResolver
{
    private static readonly string[] PowerShellArguments = ["-NoLogo", "-NoProfile"];
    private static readonly TimeSpan CommandSearchTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Resolves the preferred shell on the current operating system.
    /// </summary>
    public static ShellCommand Resolve()
    {
        return OperatingSystem.IsWindows() ? ResolveWindows() : ResolvePosix();
    }

    /// <summary>
    /// Resolves the preferred shell and wraps it in session options.
    /// </summary>
    public static TerminalSessionOptions CreateOptions(string? workingDirectory = null)
    {
        ShellCommand shell = Resolve();
        return new TerminalSessionOptions
        {
            Executable = shell.Executable,
            Arguments = shell.Arguments,
            WorkingDirectory = workingDirectory
        };
    }

    /// <summary>
    /// Finds a file through the operating system command search (<c>where.exe</c> on Windows,
    /// <c>which -a</c> elsewhere), or returns an existing rooted path unchanged. The child process
    /// inherits <c>PATH</c>, so no directory list is read here.
    /// </summary>
    public static string? FindOnPath(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (Path.IsPathRooted(fileName))
        {
            return File.Exists(fileName) ? Path.GetFullPath(fileName) : null;
        }

        string executable = OperatingSystem.IsWindows() ? "where.exe" : "which";
        List<string> arguments = OperatingSystem.IsWindows() ? [fileName] : ["-a", fileName];
        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return null;
            }
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The command search does not read stdin; a closed pipe is harmless.
        }

        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)CommandSearchTimeout.TotalMilliseconds))
        {
            Kill(process);
            return null;
        }

        string standardOutput = output.GetAwaiter().GetResult();
        _ = error.GetAwaiter().GetResult();
        foreach (string line in standardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = line.Trim().Trim('"');
            if (Path.IsPathRooted(candidate) && File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static ShellCommand ResolveWindows()
    {
        string? pwsh = FindOnPath("pwsh.exe") ?? FindExisting(
            Path.Combine(GetProgramFiles(), "PowerShell", "7", "pwsh.exe"),
            Path.Combine(GetProgramFiles(), "PowerShell", "7-preview", "pwsh.exe"),
            Path.Combine(GetLocalApplicationData(), "Microsoft", "WindowsApps", "pwsh.exe"));
        if (pwsh is not null)
        {
            return new ShellCommand(pwsh, PowerShellArguments);
        }

        string windowsPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(windowsPowerShell))
        {
            return new ShellCommand(windowsPowerShell, PowerShellArguments);
        }

        string? bash = FindOnPath("bash.exe") ?? FindExisting(
            Path.Combine(GetProgramFiles(), "Git", "bin", "bash.exe"),
            Path.Combine(GetProgramFiles(), "Git", "usr", "bin", "bash.exe"),
            Path.Combine(GetLocalApplicationData(), "Programs", "Git", "bin", "bash.exe"));
        if (bash is not null)
        {
            return new ShellCommand(bash, []);
        }

        return new ShellCommand("cmd.exe", []);
    }

    private static ShellCommand ResolvePosix()
    {
        string? pwsh = FindOnPath("pwsh") ?? FindExisting(
            "/usr/local/bin/pwsh",
            "/opt/homebrew/bin/pwsh",
            "/usr/bin/pwsh");
        if (pwsh is not null)
        {
            return new ShellCommand(pwsh, PowerShellArguments);
        }

        return new ShellCommand(FindExisting("/bin/bash", "/usr/bin/bash") ?? "bash", []);
    }

    private static string GetProgramFiles()
    {
        return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    }

    private static string GetLocalApplicationData()
    {
        return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    private static string? FindExisting(params string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (InvalidOperationException)
        {
            // The command search already exited.
        }
        catch (Win32Exception)
        {
            // The command search already exited.
        }
        catch (NotSupportedException)
        {
            // The command search already exited.
        }
    }
}
