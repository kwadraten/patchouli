using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Patchouli.Workflows;
using Patchouli.Agent.Sdk;

namespace Patchouli.Host.Agent;

/// <summary>Private desktop entry point for a persistent FSI process with its own current directory.</summary>
public static class AgentFsiReplWorker
{
    public const string Switch = "--agent-fsi-worker";

    private sealed class RpcInvoker(StreamReader reader, StreamWriter writer) : ISdkInvoker, IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly object _pendingGate = new();
        private readonly List<Task<SdkResult>> _pending = [];
        private bool _accepting = true;

        public Task<SdkResult> InvokeAsync(string name, string arguments)
        {
            lock (_pendingGate)
            {
                if (!_accepting)
                {
                    return Task.FromResult(new SdkResult(false, "SDK_SCOPE_EXPIRED: the evaluation has ended.",
                        "SDK_SCOPE_EXPIRED", ""));
                }

                Task<SdkResult> call = InvokeRpcAsync(name, arguments);
                _pending.Add(call);
                return call;
            }
        }

        public async Task DrainAsync()
        {
            Task<SdkResult>[] pending;
            lock (_pendingGate)
            {
                _accepting = false;
                pending = _pending.ToArray();
            }

            await Task.WhenAll(pending).ConfigureAwait(false);
        }

        private async Task<SdkResult> InvokeRpcAsync(string name, string arguments)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { kind = "sdk-call", name, arguments }))
                    .ConfigureAwait(false);
                string response = await reader.ReadLineAsync().ConfigureAwait(false)
                                  ?? throw new IOException("SDK connection closed.");
                return JsonSerializer.Deserialize<SdkResult>(response)
                       ?? throw new IOException("SDK returned no result.");
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose()
        {
            _gate.Dispose();
        }
    }

    public static async Task<int> RunAsync(string pipeName)
    {
        using NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(30_000).ConfigureAwait(false);
        using StreamReader reader = new(pipe, Encoding.UTF8, false, leaveOpen: true);
        await using StreamWriter writer = new(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using ScriptHostSession host = ScriptHostSession.Create();
        string workingDirectory = Directory.GetCurrentDirectory();
        // Compile the string in FSI so its type uses the interpreter's framework references.
        // A reflected System.String binding can differ from FSI's string type on this runtime.
        ScriptEvaluationResult initialized = host.EvaluateScript(
            "open Patchouli.Agent.Sdk\nlet workingDirectory = " + JsonSerializer.Serialize(workingDirectory),
            Path.Combine(workingDirectory, "agent-repl.fsx"));
        if (!initialized.Succeeded)
        {
            throw new InvalidOperationException("FSI workspace initialization failed: " + initialized.Failure);
        }

        string installedBindings = "";
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            using JsonDocument request = JsonDocument.Parse(line);
            string code = request.RootElement.GetProperty("code").GetString() ?? string.Empty;
            string sdkBindings = request.RootElement.GetProperty("sdkBindings").GetString() ?? string.Empty;
            if (sdkBindings != installedBindings && sdkBindings.Length > 0)
            {
                ScriptEvaluationResult installed = host.EvaluateScript(sdkBindings, "agent-sdk.fsx");
                if (!installed.Succeeded)
                {
                    throw new InvalidOperationException("SDK_BINDING_FAILED: " + installed.Failure);
                }

                installedBindings = sdkBindings;
            }

            // Relative IO and spawned processes start here again even if the preceding interaction changed cwd.
            Directory.CreateDirectory(workingDirectory);
            Directory.SetCurrentDirectory(workingDirectory);
            host.ClearOutput();
            ScriptEvaluationResult result;
            using RpcInvoker sdk = new(reader, writer);
            using IDisposable binding = SdkModule.useInvoker(sdk);
            using (AgentFsiConsoleCapture.Begin(host.OutputWriter, host.FailureWriter))
            {
                string file = Path.Combine(workingDirectory, "agent-repl.fsx");
                ScriptCheckResult checkedCode = host.CheckScript(code, file);
                result = checkedCode.Succeeded
                    ? host.EvaluateScript(code, file)
                    : new ScriptEvaluationResult(false, null, checkedCode.Diagnostics,
                        ScriptDiagnostics.describeAll(checkedCode.Diagnostics));
            }

            // Every admitted operation settles before the terminal frame; no competing pipe reader
            // survives this evaluation, even when model code forgets to await a returned Task.
            await sdk.DrainAsync().ConfigureAwait(false);

            string payload = JsonSerializer.Serialize(new
            {
                succeeded = result.Succeeded,
                output = host.Output,
                error = host.Failure,
                value = result.Value?.ToString(),
                diagnostics = result.Diagnostics.Select(diagnostic => diagnostic.Message).ToArray(),
                failure = result.Failure
            });
            await writer.WriteLineAsync(JsonSerializer.Serialize(new AgentToolOutcome(
                result.Succeeded, payload, result.Succeeded ? null : "FSI_EVALUATION_FAILED"))).ConfigureAwait(false);
        }

        return 0;
    }
}
