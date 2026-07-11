using System.Diagnostics;

namespace Patchouli.Ocr;

public sealed record ProcessRunRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    TimeSpan? Timeout = null,
    IReadOnlyDictionary<string, string>? Environment = null);

public sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default);
}

public sealed class SystemProcessRunner : IProcessRunner
{
    public bool UsesShellExecute => false;

    public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ProcessStartInfo info = new(request.FileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            info.WorkingDirectory = request.WorkingDirectory;
        }

        foreach (string argument in request.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (request.Environment is not null)
        {
            foreach (KeyValuePair<string, string> pair in request.Environment)
            {
                info.Environment[pair.Key] = pair.Value;
            }
        }

        using Process process = new() { StartInfo = info };
        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        TimeSpan timeout = request.Timeout ?? TimeSpan.FromSeconds(60);
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
            return new ProcessRunResult(process.ExitCode, await output, await error, false);
        }
        catch (TimeoutException)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }

            await process.WaitForExitAsync(CancellationToken.None);
            return new ProcessRunResult(-1, await output, await error, true);
        }
    }
}
