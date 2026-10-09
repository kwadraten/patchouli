using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Patchouli.Agent.Sdk;

namespace Patchouli.Host.Agent;

internal sealed class AgentFsiProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly NamedPipeServerStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly Task _output;
    private readonly Task _error;
    private readonly CancellationTokenSource _drainLifetime = new();

    private AgentFsiProcess(Process process, NamedPipeServerStream pipe)
    {
        _process = process;
        _pipe = pipe;
        _reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        _output = DrainAsync(process.StandardOutput, _drainLifetime.Token);
        _error = DrainAsync(process.StandardError, _drainLifetime.Token);
        process.StandardInput.Close();
    }

    public static async Task<AgentFsiProcess> StartAsync(string directory, CancellationToken cancellationToken)
    {
        // Unix adds the OS temp path to pipe names; keep the endpoint short for macOS.
        string pipeName = "pf" + Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        NamedPipeServerStream pipe = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Process? process = null;
        AgentFsiProcess? worker = null;
        try
        {
            process = Process.Start(StartInfo(directory, pipeName))
                      ?? throw new IOException("The FSI worker could not start.");
            using CancellationTokenSource startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(TimeSpan.FromSeconds(30));
            Task connected = pipe.WaitForConnectionAsync(startup.Token);
            Task exited = process.WaitForExitAsync(startup.Token);
            if (await Task.WhenAny(connected, exited).ConfigureAwait(false) == exited)
            {
                await startup.CancelAsync().ConfigureAwait(false);
                try
                {
                    await connected.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                throw new IOException("The FSI worker exited before connecting.");
            }

            await connected.ConfigureAwait(false);
            await startup.CancelAsync().ConfigureAwait(false);
            try
            {
                await exited.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            worker = new AgentFsiProcess(process, pipe);
            return worker;
        }
        catch
        {
            if (worker is not null)
            {
                await worker.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                if (process is not null)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                    }

                    await process.WaitForExitAsync().ConfigureAwait(false);
                    process.Dispose();
                }

                await pipe.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private static ProcessStartInfo StartInfo(string directory, string pipeName)
    {
        string? current = Environment.ProcessPath;
        bool desktop = Assembly.GetEntryAssembly()?.GetName().Name == "Patchouli.UI";
        string appHost = Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "Patchouli.UI.exe" : "Patchouli.UI");
        string? executable = desktop && current is not null && !Path.GetFileNameWithoutExtension(current)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? current
            : File.Exists(appHost)
                ? appHost
                : null;
        string dotnetHost = current is not null && Path.GetFileNameWithoutExtension(current)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? current
            : "dotnet";
        ProcessStartInfo info = new(executable ?? dotnetHost)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (executable is null)
        {
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Patchouli.UI.dll"));
        }

        info.ArgumentList.Add(AgentFsiReplWorker.Switch);
        info.ArgumentList.Add(pipeName);
        return info;
    }

    public async Task<AgentToolOutcome> EvaluateAsync(string code,
        Func<string, string, Task<SdkResult>>? invokeSdk, CancellationToken cancellationToken,
        string sdkBindings = "")
    {
        using CancellationTokenRegistration interrupt = cancellationToken.Register(Interrupt);
        await _writer.WriteLineAsync(JsonSerializer.Serialize(new { code, sdkBindings }).AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        string response;
        while (true)
        {
            response = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                       ?? throw new IOException("The FSI worker closed its connection.");
            using JsonDocument message = JsonDocument.Parse(response);
            if (!message.RootElement.TryGetProperty("kind", out JsonElement kind) || kind.GetString() != "sdk-call")
            {
                break;
            }

            string name = message.RootElement.GetProperty("name").GetString()!;
            string arguments = message.RootElement.GetProperty("arguments").GetString()!;
            SdkResult result;
            try
            {
                result = invokeSdk is null
                    ? new SdkResult(false, "SDK_NOT_INSTALLED", "SDK_NOT_INSTALLED", "")
                    : await invokeSdk(name, arguments).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                result = new SdkResult(false, error.Message, "SDK_CALL_FAILED", "");
            }

            await _writer.WriteLineAsync(JsonSerializer.Serialize(result).AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return JsonSerializer.Deserialize<AgentToolOutcome>(response)
               ?? throw new IOException("The FSI worker returned no result.");
    }

    public void Interrupt()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(true);
            }
        }
        catch (InvalidOperationException)
        {
            // The owned worker already exited.
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interrupt();
        await _process.WaitForExitAsync().ConfigureAwait(false);
        await _drainLifetime.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_output, _error).ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);
        _reader.Dispose();
        await _pipe.DisposeAsync().ConfigureAwait(false);
        _process.Dispose();
        _drainLifetime.Dispose();
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        char[] buffer = new char[4096];
        try
        {
            while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) != 0)
            {
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A script-created process can retain stdout after the REPL worker exits.
            return;
        }
    }
}
