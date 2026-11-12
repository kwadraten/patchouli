using System.Text.Json;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Mcp;

namespace Patchouli.Host.Plugins;

/// <summary>
/// The reverse plugin-interop contract: a plugin calls host verbs over the same stdio
/// JSON-RPC channel the host uses to call the plugin. One method per host verb,
/// <c>patchouli.host.{find,fetch,put,cite,send,add}</c>, whose params are exactly the
/// arguments of the matching <c>patchouli.{verb}</c> MCP tool, so a plugin call gets
/// exactly the host behavior an agent would get. Every reply — success or refusal — is
/// a JSON-RPC <c>result</c> carrying the MCP tool-response shape
/// <c>{content:[{type:"text",text}],isError}</c>; only an unknown method (not a
/// <c>patchouli.host.*</c> name) is a JSON-RPC error. A plugin that needs to know
/// whether the host answers reverse calls sends one with a timeout: hosts without this
/// feature ignore it, hosts with it always answer.
/// </summary>
public static class PluginReverseHostVerbs
{
    /// <summary>Maximum nesting of plugin → host → plugin → host chains (ADR 0058).</summary>
    public const int MaxDepth = 8;

    public static readonly IReadOnlyList<string> All =
        ["find", "fetch", "put", "cite", "send", "add"];

    public const string MethodPrefix = "patchouli.host.";

    public static string MethodName(string verb)
    {
        return MethodPrefix + verb;
    }

    public static string ToolName(string verb)
    {
        return "patchouli." + verb;
    }

    public static bool IsReverseMethod(string method)
    {
        return method.StartsWith(MethodPrefix, StringComparison.Ordinal);
    }

    public static bool TryParseVerb(string method, out string verb)
    {
        verb = string.Empty;
        if (!IsReverseMethod(method))
        {
            return false;
        }

        verb = method[MethodPrefix.Length..];
        return All.Contains(verb, StringComparer.Ordinal);
    }
}

/// <summary>
/// The reverse-call depth of the async flow currently dispatching a plugin-initiated
/// host-verb call. Set by the reverse dispatcher around the verb invocation; read by
/// <see cref="PluginStdioClient.CallToolAsync"/> so an outbound host → plugin call
/// made while answering a reverse call is tagged with that call's depth. The tag does
/// not cross the process boundary — the receiving client's outstanding-call record
/// carries the depth back into the next reverse dispatch (ADR 0058).
/// </summary>
internal static class PluginReverseCallDepth
{
    internal static readonly AsyncLocal<int?> Current = new();
}

/// <summary>Outcome of one reverse dispatch: handled with a tool-response result, or not a reverse method.</summary>
internal sealed record ReverseHostReply(bool Handled, JsonElement? Result);

/// <summary>
/// Dispatches plugin-initiated host-verb calls. The gates run in this order and every
/// refusal is rendered as a tool-response result with <c>isError</c>, never as a
/// JSON-RPC error, so the plugin handles refusals exactly like failed tool calls:
/// (1) the manifest ∩ registry host-verb grant (default deny); (2) the call must not
/// target the calling plugin's own mount — a plugin reads its own data directly and
/// the host cannot answer a call into a process that is waiting for the answer;
/// (3) the host's live MCP tool switches for put/send/add; (4) the recursion depth
/// budget. Only a call that passes all four reaches the host verb implementation,
/// invoked through the same command surface agents use, so domain permissions and
/// validation apply unchanged.
/// </summary>
internal sealed class PluginReverseHostDispatcher(
    string pluginId,
    Func<string, string, string?> checkGrant,
    Func<string, JsonElement, CancellationToken, Task<object>> invokeTool,
    Func<CancellationToken, Task<McpServerSettings>>? liveSettings,
    Func<int> currentReverseDepth)
{
    private readonly string _pluginId = pluginId;
    private readonly Func<string, string, string?> _checkGrant = checkGrant;
    private readonly Func<string, JsonElement, CancellationToken, Task<object>> _invokeTool = invokeTool;
    private readonly Func<CancellationToken, Task<McpServerSettings>>? _liveSettings = liveSettings;
    private readonly Func<int> _currentReverseDepth = currentReverseDepth;

    public async Task<ReverseHostReply> DispatchAsync(string method, JsonElement parameters,
        CancellationToken cancellationToken)
    {
        if (!PluginReverseHostVerbs.IsReverseMethod(method))
        {
            return new ReverseHostReply(false, null);
        }

        try
        {
            if (!PluginReverseHostVerbs.TryParseVerb(method, out string verb))
            {
                return new ReverseHostReply(true, ErrorResult(method, AppErrorCodes.InvalidArgument,
                    $"Unknown reverse host verb '{method}'; use patchouli.host.find/fetch/put/cite/send/add."));
            }

            string tool = PluginReverseHostVerbs.ToolName(verb);
            if (_checkGrant(_pluginId, verb) is { } grantRefusal)
            {
                return new ReverseHostReply(true,
                    ErrorResult(tool, AppErrorCodes.PermissionDenied, grantRefusal));
            }

            if (TargetsOwnMount(verb, parameters))
            {
                return new ReverseHostReply(true, ErrorResult(tool, AppErrorCodes.ReentrancyRefused,
                    $"A plugin must not call host verb '{verb}' on its own mount " +
                    $"'{McpResourceUris.PluginRootUri(_pluginId)}'; read the plugin's own data directly."));
            }

            if (verb is "put" or "send" or "add" && await IsToolSwitchedOffAsync(tool, cancellationToken)
                    .ConfigureAwait(false))
            {
                return new ReverseHostReply(true, ErrorResult(tool, AppErrorCodes.PermissionDenied,
                    $"'{tool}' is disabled by the host's MCP tool switches."));
            }

            int depth = _currentReverseDepth() is int current and >= 0 ? current + 1 : 0;
            if (depth > PluginReverseHostVerbs.MaxDepth)
            {
                return new ReverseHostReply(true, ErrorResult(tool, AppErrorCodes.DepthExceeded,
                    $"Reverse host-verb calls nested deeper than {PluginReverseHostVerbs.MaxDepth} are refused; " +
                    "break the plugin → host → plugin cycle."));
            }

            int? previous = PluginReverseCallDepth.Current.Value;
            PluginReverseCallDepth.Current.Value = depth;
            try
            {
                object response = await _invokeTool(tool, parameters, cancellationToken)
                    .ConfigureAwait(false);
                return new ReverseHostReply(true, JsonSerializer.SerializeToElement(response));
            }
            finally
            {
                PluginReverseCallDepth.Current.Value = previous;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ReverseHostReply(true, ErrorResult(method, "internal",
                $"The host could not answer reverse call '{method}': {exception.Message}"));
        }
    }

    private async Task<bool> IsToolSwitchedOffAsync(string tool, CancellationToken cancellationToken)
    {
        if (_liveSettings is null)
        {
            // Without live settings the switch state cannot be verified; fail closed for
            // the mutating verbs rather than letting a disabled tool through.
            return true;
        }

        try
        {
            McpServerSettings settings = await _liveSettings(cancellationToken).ConfigureAwait(false);
            McpToolOverride? match = settings.ToolOverrides.FirstOrDefault(value =>
                string.Equals(value.ToolName, tool, StringComparison.Ordinal));
            return match is { Enabled: false };
        }
        catch (Exception)
        {
            // An unreadable settings store must never turn a gated verb into an open one.
            return true;
        }
    }

    private bool TargetsOwnMount(string verb, JsonElement arguments)
    {
        string prefix = McpResourceUris.PluginRootUri(_pluginId);
        string bare = prefix.TrimEnd('/');
        bool IsOwn(string? uri) => uri is not null &&
            (string.Equals(uri, bare, StringComparison.Ordinal) ||
             uri.StartsWith(prefix, StringComparison.Ordinal));
        return verb switch
        {
            "find" => IsOwn(OptionalString(arguments, "in")),
            "fetch" => arguments.TryGetProperty("uris", out JsonElement uris) &&
                       uris.ValueKind == JsonValueKind.Array &&
                       uris.EnumerateArray().Any(uri =>
                           uri.ValueKind == JsonValueKind.String && IsOwn(uri.GetString())),
            "put" => IsOwn(OptionalString(arguments, "uri")),
            "cite" => arguments.TryGetProperty("refs", out JsonElement refs) &&
                      refs.ValueKind == JsonValueKind.Array &&
                      refs.EnumerateArray().Any(uri =>
                          uri.ValueKind == JsonValueKind.String && IsOwn(uri.GetString())),
            "send" => (arguments.TryGetProperty("endpoint", out JsonElement endpoint) &&
                       endpoint.ValueKind == JsonValueKind.String &&
                       endpoint.GetString()!.StartsWith("plugin/" + _pluginId + "/",
                           StringComparison.Ordinal)) ||
                      IsOwn(OptionalString(arguments, "target")),
            _ => false
        };
    }

    private static string? OptionalString(JsonElement arguments, string name)
    {
        return arguments.ValueKind == JsonValueKind.Object &&
               arguments.TryGetProperty(name, out JsonElement value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    internal static JsonElement ErrorResult(string tool, string code, string message)
    {
        string text = JsonSerializer.Serialize(new
        {
            tool,
            error = new { code, message }
        });
        return JsonSerializer.SerializeToElement(new
        {
            content = new[] { new { type = "text", text } },
            isError = true
        });
    }
}
