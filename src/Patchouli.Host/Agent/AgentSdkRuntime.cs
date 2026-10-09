using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Patchouli.Agent.Sdk;

namespace Patchouli.Host.Agent;

/// <summary>One independently observed operation, including failures caught by user F# code.</summary>
public sealed record AgentSdkReceipt(
    string OperationId,
    string ParentId,
    string Tool,
    string Arguments,
    string Contract,
    string Status,
    string Payload,
    string? ErrorCode,
    double ElapsedMs)
{
    public bool IsPrimitive { get; init; } = true;
}

/// <summary>Immutable activation; a stage only narrows the ordinary host capabilities.</summary>
public sealed record AgentSdkPolicy(
    IReadOnlySet<string>? AllowedTools = null,
    int? PrimitiveLimit = null,
    IReadOnlyList<ExportedTool>? Exports = null,
    string Activation = "chat");

/// <summary>The shared SDK invoker. Provider JSON never becomes executable F# source.</summary>
public sealed class AgentSdkRuntime(IAgentMcpGateway gateway)
{
    public Scope Activate(AgentEffectContext context, string parentId, CancellationToken cancellationToken)
    {
        return new Scope(gateway, context, parentId, cancellationToken);
    }

    public sealed class Scope : ISdkInvoker, IDisposable
    {
        private readonly IAgentMcpGateway _gateway;
        private readonly AgentEffectContext _context;
        private readonly CancellationToken _token;
        private readonly string _parent;
        private readonly SemaphoreSlim _primitiveGate = new(1, 1);
        private readonly List<AgentSdkReceipt> _receipts = [];
        private readonly object _receiptGate = new();
        private readonly object _pendingGate = new();
        private readonly List<Task<SdkResult>> _pending = [];
        private bool _accepting = true;
        private int _ordinal;
        private int _primitives;

        internal Scope(IAgentMcpGateway gateway, AgentEffectContext context, string parent,
            CancellationToken cancellationToken)
        {
            _gateway = gateway;
            _context = context;
            _parent = parent;
            _token = cancellationToken;
        }

        public void Dispose()
        {
            lock (_pendingGate)
            {
                _accepting = false;
            }
            // Cancellation can outlive an atomic commit. This semaphore never allocates a WaitHandle
            // and is collected with the last operation rather than disposed under its continuation.
        }

        public IReadOnlyList<AgentSdkReceipt> Receipts
        {
            get
            {
                lock (_receiptGate)
                {
                    return _receipts.ToArray();
                }
            }
        }

        internal void Restore(IReadOnlyList<AgentSdkReceipt> receipts)
        {
            lock (_receiptGate)
            {
                _receipts.AddRange(receipts);
            }
        }

        private void Observe(AgentSdkReceipt receipt)
        {
            lock (_receiptGate)
            {
                _receipts.Add(receipt);
            }
        }

        public Task<SdkResult> InvokeAsync(string name, string arguments)
        {
            return Submit(name, arguments, _context.SdkPolicy?.AllowedTools, _parent, 0);
        }

        private Task<SdkResult> Submit(string name, string arguments, IReadOnlySet<string>? allowed, string parent,
            int depth)
        {
            lock (_pendingGate)
            {
                if (!_accepting)
                {
                    return Task.FromResult(new SdkResult(false, "SDK_SCOPE_EXPIRED: the tool invocation has ended.",
                        "SDK_SCOPE_EXPIRED", ""));
                }

                Task<SdkResult> call = InvokeCoreAsync(name, arguments, allowed, parent, depth);
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

        private sealed class FunctionInvoker(Scope scope, ExportedTool tool, string parent, int depth) : ISdkInvoker
        {
            public Task<SdkResult> InvokeAsync(string name, string arguments)
            {
                return scope.Submit(name, arguments, tool.Capabilities.ToHashSet(StringComparer.Ordinal),
                    parent, depth);
            }
        }

        private async Task<SdkResult> RejectAsync(string name, string arguments, string parent, string code,
            string detail)
        {
            AgentSdkReceipt receipt = new(_parent + "/" + Interlocked.Increment(ref _ordinal), parent, name, arguments,
                _context.SdkPolicy?.Activation ?? "chat", "Failed", detail, code, 0);
            Observe(receipt);
            if (_context.RecordSdk is { } record)
            {
                await record(receipt).ConfigureAwait(false);
            }

            return Result(receipt);
        }

        private async Task<SdkResult> InvokeCoreAsync(string name, string arguments, IReadOnlySet<string>? allowed,
            string parent, int depth)
        {
            if (name == "fsi" || (allowed is not null && !allowed.Contains(name)) || depth > 16)
            {
                return await RejectAsync(name, arguments, parent, "AGENT_TOOL_DENIED", "AGENT_TOOL_DENIED: " + name)
                    .ConfigureAwait(false);
            }

            ExportedTool? exported = _context.SdkPolicy?.Exports?.SingleOrDefault(tool => tool.Name == name);
            if (exported is not null)
            {
                // Captured host services never cross the pipe. Both projections invoke this typed function.
                using IDisposable binding =
                    SdkModule.useInvoker(new FunctionInvoker(this, exported, parent + "/" + name, depth + 1));
                try
                {
                    string output = await exported.InvokeAsync(arguments).ConfigureAwait(false);
                    return new SdkResult(true, output, "", _parent);
                }
                catch (SdkCallException error)
                {
                    return error.Result;
                }
                catch (Exception error) when (error is JsonException or ArgumentException)
                {
                    return await RejectAsync(name, arguments, parent, "INVALID_ARGUMENT", error.Message)
                        .ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    return await RejectAsync(name, arguments, parent, "SDK_FUNCTION_FAILED", error.Message)
                        .ConfigureAwait(false);
                }
            }

            if (name is not ("find" or "fetch" or "put" or "cite" or "send" or "history"))
            {
                return await RejectAsync(name, arguments, parent, "TOOL_NOT_SUPPORTED",
                    "The SDK has no registered tool named '" + name + "'.").ConfigureAwait(false);
            }

            await _primitiveGate.WaitAsync(_token).ConfigureAwait(false);
            try
            {
                _token.ThrowIfCancellationRequested();
                string canonical;
                try
                {
                    canonical = Canonical(arguments);
                }
                catch (JsonException error)
                {
                    return await RejectAsync(name, arguments, parent, "INVALID_ARGUMENT", error.Message)
                        .ConfigureAwait(false);
                }

                string operation = _parent + "/" + Interlocked.Increment(ref _ordinal);
                string contract = _context.SdkPolicy?.Activation ?? "chat";
                string? path = _context.SessionDirectory is { } directory
                    ? Path.Combine(directory, "sdk", Hash(operation) + ".json")
                    : null;
                if (path is not null && File.Exists(path))
                {
                    AgentSdkReceipt previous = JsonSerializer.Deserialize<AgentSdkReceipt>(
                                                   await File.ReadAllTextAsync(path, _token).ConfigureAwait(false))
                                               ?? throw new InvalidDataException("SDK_RECEIPT_INVALID");
                    if (previous.OperationId != operation || previous.Tool != name ||
                        previous.Arguments != canonical || previous.Contract != contract)
                    {
                        return new SdkResult(false, "SDK_REPLAY_DIVERGED: invocation differs from its receipt.",
                            "SDK_REPLAY_DIVERGED", operation);
                    }

                    if (previous.Status == "Started")
                    {
                        if (name is "put" or "send")
                        {
                            Observe(previous);
                            return new SdkResult(false,
                                "SDK_OPERATION_UNKNOWN: verify the actual resource before retrying.",
                                "SDK_OPERATION_UNKNOWN", operation);
                        }
                    }
                    else
                    {
                        Observe(previous);
                        return Result(previous);
                    }
                }

                if (_context.SdkPolicy?.PrimitiveLimit is { } limit && _primitives >= limit)
                {
                    return await RejectAsync(name, canonical, parent, "AGENT_TOOL_BUDGET_EXHAUSTED",
                        "AGENT_TOOL_BUDGET_EXHAUSTED: SDK primitive budget.").ConfigureAwait(false);
                }

                _primitives++;
                if (name is "put" or "send" && path is not null)
                {
                    foreach (string receiptPath in Directory.Exists(Path.GetDirectoryName(path))
                                 ? Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.json")
                                 : [])
                    {
                        AgentSdkReceipt? unresolved =
                            JsonSerializer.Deserialize<AgentSdkReceipt>(await File.ReadAllTextAsync(receiptPath, _token)
                                .ConfigureAwait(false));
                        if (unresolved is { Status: "Started" } && unresolved.Tool == name &&
                            (name == "send" || SameTarget(unresolved.Arguments, canonical)))
                        {
                            return await RejectAsync(name, canonical, parent, "SDK_OPERATION_UNKNOWN",
                                    "SDK_OPERATION_UNKNOWN: an earlier write to this target has no durable outcome; reconcile it before another write.")
                                .ConfigureAwait(false);
                        }
                    }
                }

                AgentSdkReceipt started = new(operation, parent, name, canonical, contract, "Started", "", null, 0);
                if (path is not null)
                {
                    await SaveAsync(path, started).ConfigureAwait(false);
                }

                if (_context.RecordSdk is { } began)
                {
                    await began(started).ConfigureAwait(false);
                }

                long clock = System.Diagnostics.Stopwatch.GetTimestamp();
                AgentToolOutcome outcome;
                try
                {
                    outcome = name == "history"
                        ? AgentHistoryTool.Read(_context, canonical)
                        : await _gateway.CallToolAsync(name, canonical, _token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    outcome = new AgentToolOutcome(false, error.Message, error.GetType().Name);
                }

                AgentSdkReceipt settled = started with
                {
                    Status = outcome.Succeeded ? "Succeeded" : "Failed",
                    Payload = outcome.Payload,
                    ErrorCode = outcome.ErrorCode,
                    ElapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(clock).TotalMilliseconds
                };
                // A completed write remains durable even if the worker was cancelled during commit.
                if (path is not null)
                {
                    await SaveAsync(path, settled).ConfigureAwait(false);
                }

                if (_context.RecordSdk is { } completed)
                {
                    await completed(settled).ConfigureAwait(false);
                }

                Observe(settled);
                return Result(settled);
            }
            finally
            {
                _primitiveGate.Release();
            }
        }

        private static bool SameTarget(string left, string right)
        {
            using JsonDocument a = JsonDocument.Parse(left), b = JsonDocument.Parse(right);
            return a.RootElement.TryGetProperty("uri", out JsonElement x) &&
                   b.RootElement.TryGetProperty("uri", out JsonElement y) && x.GetString() == y.GetString();
        }

        private static SdkResult Result(AgentSdkReceipt receipt)
        {
            return new SdkResult(receipt.Status == "Succeeded", receipt.Payload, receipt.ErrorCode ?? "",
                receipt.OperationId);
        }
    }

    internal static async Task<IReadOnlyList<AgentSdkReceipt>> ReadReceiptsAsync(string directory, string parent,
        CancellationToken cancellationToken)
    {
        string root = Path.Combine(directory, "sdk");
        List<AgentSdkReceipt> receipts = [];
        if (Directory.Exists(root))
        {
            foreach (string path in Directory.EnumerateFiles(root, "*.json"))
            {
                AgentSdkReceipt receipt = JsonSerializer.Deserialize<AgentSdkReceipt>(
                                              await File.ReadAllTextAsync(path, cancellationToken)
                                                  .ConfigureAwait(false))
                                          ?? throw new InvalidDataException("SDK_RECEIPT_INVALID");
                if (receipt.OperationId.StartsWith(parent + "/", StringComparison.Ordinal))
                {
                    receipts.Add(receipt);
                }
            }
        }

        return receipts.OrderBy(r =>
                int.Parse(r.OperationId[(parent.Length + 1)..], System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
    }

    private static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string Canonical(string arguments)
    {
        using JsonDocument doc = JsonDocument.Parse(arguments);
        return Ordered(doc.RootElement)?.ToJsonString() ?? "null";
    }

    private static JsonNode? Ordered(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            JsonObject obj = new();
            foreach (JsonProperty property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (obj.ContainsKey(property.Name))
                {
                    throw new JsonException("Duplicate SDK argument: " + property.Name);
                }

                obj[property.Name] = Ordered(property.Value);
            }

            return obj;
        }

        return element.ValueKind == JsonValueKind.Array
            ? new JsonArray(element.EnumerateArray().Select(Ordered).ToArray())
            : JsonNode.Parse(element.GetRawText());
    }

    private static async Task SaveAsync(string path, AgentSdkReceipt receipt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(receipt), Encoding.UTF8,
                CancellationToken.None).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
