using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Patchouli.Core.Agents;

namespace Patchouli.Infrastructure.Agents;

/// <summary>
/// Shared child-process runner used by Agent detection and launch validation. It never invokes a shell
/// for ordinary executables; batch files are wrapped in <c>cmd.exe /d /s /c call</c> by the detector.
/// </summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public async Task<AgentProcessResult> RunAsync(
        AgentProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            return AgentProcessResult.NotStarted("可执行文件名为空。");
        }

        ProcessStartInfo startInfo = new(request.FileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (string argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        if (request.Environment is not null)
        {
            foreach ((string name, string? value) in request.Environment)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (value is null)
                {
                    startInfo.Environment.Remove(name);
                }
                else
                {
                    startInfo.Environment[name] = value;
                }
            }
        }

        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return AgentProcessResult.NotStarted("进程未能启动。");
            }
        }
        catch (Win32Exception exception)
        {
            return AgentProcessResult.NotStarted(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return AgentProcessResult.NotStarted(exception.Message);
        }
        catch (IOException exception)
        {
            return AgentProcessResult.NotStarted(exception.Message);
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // A process that does not read stdin is still a valid version-probe target.
        }

        TimeSpan timeout = request.Timeout ?? DefaultTimeout;
        using CancellationTokenSource
            timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            string output = await standardOutput.ConfigureAwait(false);
            string error = await standardError.ConfigureAwait(false);
            return new AgentProcessResult(true, false, process.ExitCode, output, error, null);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            string output = await SafeReadAsync(standardOutput).ConfigureAwait(false);
            string error = await SafeReadAsync(standardError).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new AgentProcessResult(true, true, -1, output, error,
                $"进程在 {timeout.TotalSeconds:0.###} 秒后超时。");
        }
    }

    private static async Task<string> SafeReadAsync(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
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
        catch (InvalidOperationException exception)
        {
            Debug.WriteLine($"Agent process termination failed: {exception}");
        }
        catch (Win32Exception exception)
        {
            Debug.WriteLine($"Agent process termination failed: {exception}");
        }
        catch (NotSupportedException exception)
        {
            Debug.WriteLine($"Agent process termination failed: {exception}");
        }
    }
}
