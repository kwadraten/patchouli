using System.Diagnostics;

namespace Patchouli.Host.Plugins;

/// <summary>
/// One host-managed file mapping handed to the plugin process at launch: the manifest-relative
/// declaration name and its absolute managed location (OS separators). Plain launch data —
/// never a credential.
/// </summary>
public sealed record PluginManagedFileMapping(string RelativePath, string AbsolutePath);

/// <summary>The verified process launch request for one plugin mount.</summary>
public sealed record PluginProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory)
{
    /// <summary>
    /// Host-managed files root for this mount (<c>{filesRoot}/{pluginId}</c>). When set the
    /// launcher prepends <c>--patchouli-managed-files</c> plus one repeatable
    /// <c>--patchouli-managed-file &lt;relative&gt;|&lt;absolute&gt;</c> per mapping before the
    /// plugin's own declared arguments — the host-controlled launch contract of issue #8.
    /// </summary>
    public string? ManagedFilesRoot { get; init; }

    /// <summary>Per-declaration managed-file mappings; used only when <see cref="ManagedFilesRoot"/> is set.</summary>
    public IReadOnlyList<PluginManagedFileMapping> ManagedFiles { get; init; } = [];

    /// <summary>
    /// The final argument list: host-controlled managed-file arguments first (when
    /// <see cref="ManagedFilesRoot"/> is set), then the manifest's declared runtime args.
    /// </summary>
    public IReadOnlyList<string> EffectiveArguments
    {
        get
        {
            if (ManagedFilesRoot is null)
            {
                return Arguments;
            }

            List<string> arguments = new(2 + ManagedFiles.Count * 2 + Arguments.Count)
            {
                "--patchouli-managed-files", ManagedFilesRoot
            };
            foreach (PluginManagedFileMapping file in ManagedFiles)
            {
                arguments.Add("--patchouli-managed-file");
                arguments.Add(file.RelativePath + "|" + file.AbsolutePath);
            }

            arguments.AddRange(Arguments);
            return arguments;
        }
    }
}

/// <summary>
/// The narrow child-process surface <see cref="PluginStdioClient"/> drives. Production uses
/// <see cref="System.Diagnostics.Process"/>; tests substitute in-memory pipes.
/// </summary>
public interface IPluginChildProcess : IDisposable
{
    Stream StandardInput { get; }
    Stream StandardOutput { get; }
    Stream StandardError { get; }
    bool HasExited { get; }
    int? ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Kills the whole process tree; never throws on an already-dead process.</summary>
    void KillTree();
}

public interface IPluginProcessLauncher
{
    IPluginChildProcess Start(PluginProcessSpec spec);
}

/// <summary>Launches the declared command without a shell: no interpolation, package-relative cwd.</summary>
public sealed class PluginProcessLauncher : IPluginProcessLauncher
{
    public IPluginChildProcess Start(PluginProcessSpec spec)
    {
        string packageExecutable = Path.GetFullPath(Path.Combine(spec.WorkingDirectory, spec.FileName));
        string packageRoot = Path.GetFullPath(spec.WorkingDirectory) + Path.DirectorySeparatorChar;
        string executable = !Path.IsPathFullyQualified(spec.FileName) &&
                            packageExecutable.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase) &&
                            File.Exists(packageExecutable)
            ? packageExecutable
            : spec.FileName;
        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in spec.EffectiveArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process? process = Process.Start(startInfo)
                           ?? throw new InvalidOperationException(
                               $"The plugin process '{spec.FileName}' could not be started.");
        return new PluginChildProcess(process);
    }

    private sealed class PluginChildProcess(Process process) : IPluginChildProcess
    {
        public Stream StandardInput => process.StandardInput.BaseStream;
        public Stream StandardOutput => process.StandardOutput.BaseStream;
        public Stream StandardError => process.StandardError.BaseStream;
        public bool HasExited => process.HasExited;
        public int? ExitCode => process.HasExited ? process.ExitCode : null;

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            return process.WaitForExitAsync(cancellationToken);
        }

        public void KillTree()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch (Exception) when (!process.HasExited)
            {
                // The process died between the check and the kill; nothing left to do.
            }
            catch (InvalidOperationException)
            {
            }
        }

        public void Dispose()
        {
            try
            {
                process.Kill(true);
            }
            catch (Exception)
            {
                // Best effort: a dead or never-started process cannot be killed.
            }

            process.Dispose();
        }
    }
}
