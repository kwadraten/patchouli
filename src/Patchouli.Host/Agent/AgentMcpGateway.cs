using System.Text.Json;
using Patchouli.Mcp;
using Patchouli.McpServer;

namespace Patchouli.Host.Agent;

/// <summary>The outcome of one MCP tool call executed for an agent session.</summary>
/// <param name = "Succeeded">True when the tool produced a usable payload.</param>
/// <param name = "Payload">The text payload fed back to the core as a <c>ToolResult</c>.</param>
/// <param name = "ErrorCode">Stable failure code when <paramref name = "Succeeded"/> is false.</param>
public sealed record AgentToolOutcome(bool Succeeded, string Payload, string? ErrorCode = null)
{
    public IReadOnlyList<AgentSdkReceipt> Receipts { get; init; } = [];
}

/// <summary>The outcome of one atomic whole-resource <c>put</c>.</summary>
/// <param name = "Committed">True when the commit succeeded; false means it rolled back.</param>
/// <param name = "Detail">Terminal-style detail for the run log (never carries secrets or paths).</param>
public sealed record AgentPutOutcome(bool Committed, string Detail);

/// <summary>
///     The MCP surface an agent session may use. It exists so the interpreter's effect mapping is
///     testable with a stub and so the session service never depends on the concrete command
///     service; the production implementation is <see cref = "McpCommandServiceAgentGateway"/>, which
///     routes every call through <see cref = "McpCommandService"/> so permissions, validation,
///     error codes and the v3 envelope stay isomorphic with the external MCP surface.
/// </summary>
public interface IAgentMcpGateway
{
    /// <summary>Invokes one MCP tool by name with its JSON arguments.</summary>
    Task<AgentToolOutcome> CallToolAsync(string name, string arguments, CancellationToken cancellationToken);

    /// <summary>
    ///     Performs one atomic whole-resource replace. Once this call has started it is the commit
    ///     point (ADR 0024/0036): cancellation never rewrites its outcome.
    /// </summary>
    Task<AgentPutOutcome> PutAsync(string uri, string content, CancellationToken cancellationToken);
}

/// <summary>
///     Routes agent tool calls and <c>put</c>s through the shared <see cref = "McpCommandService"/>,
///     so the built-in agent is exactly as capable — and as restricted — as any other MCP client:
///     the same user tool switches, the same atomic write set and the same error vocabulary.
/// </summary>
public sealed class McpCommandServiceAgentGateway : IAgentMcpGateway
{
    private static readonly JsonSerializerOptions Arguments = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    ///     The library revision of the closed v3 error envelope built when a request fails before any
    ///     command envelope exists. It is the same fallback revision the command service itself uses
    ///     when the library state cannot be read.
    /// </summary>
    private const string UnknownLibraryRevision = "lib:0";

    private readonly McpCommandService _commands;

    /// <summary>Creates the gateway over an already composed command service.</summary>
    public McpCommandServiceAgentGateway(McpCommandService commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _commands = commands;
    }

    /// <inheritdoc/>
    public async Task<AgentToolOutcome> CallToolAsync(string name, string arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string payload = string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments;
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            if (McpProtocolHandler.ValidateToolArguments("patchouli." + name, document.RootElement) is { } invalid)
            {
                return new AgentToolOutcome(false, "INVALID_ARGUMENT: " + invalid, "INVALID_ARGUMENT");
            }

            return name.ToLowerInvariant() switch
            {
                "find" => FromFind(await _commands.FindAsync(Deserialize<McpFindRequest>(payload), cancellationToken)
                    .ConfigureAwait(false)),
                "fetch" => FromFetch(await _commands
                    .FetchAsync(Deserialize<McpFetchRequest>(payload), cancellationToken).ConfigureAwait(false)),
                "cite" => FromCite(await _commands.CiteAsync(Deserialize<McpCiteRequest>(payload), cancellationToken)
                    .ConfigureAwait(false)),
                "put" => await PutToolAsync(Deserialize<McpPutToolArguments>(payload), cancellationToken)
                    .ConfigureAwait(false),
                "send" => await SendToolAsync(document.RootElement, cancellationToken).ConfigureAwait(false),
                _ => new AgentToolOutcome(false, $"Unsupported MCP tool '{name}'.", "TOOL_NOT_SUPPORTED")
            };
        }
        catch (JsonException exception)
        {
            return new AgentToolOutcome(false, $"Invalid arguments for '{name}': {exception.Message}",
                "INVALID_ARGUMENT");
        }
    }

    /// <inheritdoc/>
    public async Task<AgentPutOutcome> PutAsync(string uri, string content, CancellationToken cancellationToken)
    {
        McpCommandResult<McpPutMeta, McpPutResult> result =
            await _commands.PutAsync(new McpPutRequest(uri, content), cancellationToken).ConfigureAwait(false);
        if (result.Envelope is { Entries.Count: > 0 } envelope)
        {
            McpPutResult put = envelope.Entries[0];
            return new AgentPutOutcome(put.Committed,
                put.Committed ? $"committed {put.Uri} ({put.ContentBytes} bytes)" : $"rolled back {put.Uri}");
        }

        return new AgentPutOutcome(false,
            result.Error?.ToTerminalLine() ?? $"The put to '{uri}' did not reach a commit outcome.");
    }

    private static T Deserialize<T>(string payload)
    {
        return JsonSerializer.Deserialize<T>(payload, Arguments) ?? throw new JsonException("Expected a JSON object.");
    }

    private async Task<AgentToolOutcome> SendToolAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string? Text(string name)
        {
            return arguments.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;
        }

        IReadOnlyList<McpSendParameter>? parameters = null;
        if (arguments.TryGetProperty("parameters", out JsonElement values))
        {
            parameters = values.EnumerateArray().Select(value =>
            {
                string text = value.GetString()!;
                int separator = text.IndexOf('=');
                if (separator <= 0)
                {
                    throw new JsonException("send parameters must use NAME=VALUE.");
                }

                return new McpSendParameter(text[..separator], text[(separator + 1)..]);
            }).ToArray();
        }

        return Render(await _commands
            .SendAsync(
                new McpSendRequest(Text("instruction")!, Text("workflow"), parameters, Text("session"),
                    Text("message_id"), Text("text")), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    ///     Routes the agent-facing <c>put</c> tool through the same atomic whole-resource write channel
    ///     as <see cref = "PutAsync"/>, so a script that writes through the tool and a caller that uses
    ///     the dedicated put effect reach exactly one write path, one permission check and one
    ///     validation.
    /// </summary>
    /// <remarks>
    ///     The payload is the JSON projection of the command envelope and never a prose line appended
    ///     to it: a committed put carries its entry, a structurally rejected one carries the same entry
    ///     with <c>committed=false</c> and the <c>translation_errors</c> list, and a refused one carries
    ///     the closed v3 error envelope. The payload therefore stays one parseable JSON document, which
    ///     is what lets the caller tell "the host rejected this write" from "the host does not route
    ///     this tool" and never fall back to a second write path around an explicit refusal.
    /// </remarks>
    private async Task<AgentToolOutcome> PutToolAsync(McpPutToolArguments request, CancellationToken cancellationToken)
    {
        McpCommandResult<McpPutMeta, McpPutResult> result = await _commands
            .PutAsync(new McpPutRequest(request.Uri, request.Content), cancellationToken).ConfigureAwait(false);
        if (result.Envelope is { } envelope)
        {
            bool committed = result.IsSuccess && envelope.Entries.Count > 0 &&
                             envelope.Entries.All(entry => entry.Committed);
            return new AgentToolOutcome(committed, McpCommandService.RenderText(envelope, "json"),
                committed ? null : result.Error?.Name ?? "INVALID_CONTENT");
        }

        McpToolError error = result.Error ?? McpToolError.From(McpErrorCode.Internal,
            "The put produced neither a response envelope nor an error.");
        return new AgentToolOutcome(false,
            McpCommandService.RenderText(McpErrorEnvelope.Build("put", error, UnknownLibraryRevision), "json"),
            error.Name);
    }

    private static AgentToolOutcome FromFind(McpCommandResult<McpFindMeta, object> result)
    {
        return Render(result);
    }

    private static AgentToolOutcome FromFetch(McpCommandResult<McpFetchMeta, McpFetchResult> result)
    {
        return Render(result);
    }

    private static AgentToolOutcome FromCite(McpCommandResult<McpCiteMeta, McpCitationResult> result)
    {
        return Render(result);
    }

    private static AgentToolOutcome Render<TMeta, TEntry>(McpCommandResult<TMeta, TEntry> result)
    {
        if (result.Envelope is null)
        {
            string detail = result.Error?.ToTerminalLine() ?? "The MCP request failed.";
            return new AgentToolOutcome(false, detail, result.Error?.Name);
        }

        // TOON is the tool surface's default text projection; JSON keeps the agent payload lossless.
        string text = McpCommandService.RenderText(result.Envelope, "json");
        return result.IsSuccess
            ? new AgentToolOutcome(true, text)
            : new AgentToolOutcome(false, text, result.Error?.Name);
    }

    /// <summary>
    ///     Arguments of the agent-facing MCP <c>put</c> tool. The declared nullability mirrors the wire
    ///     contract: a missing property deserializes to null and the command service answers with its
    ///     own <c>INVALID_ARGUMENT</c> envelope, exactly as it does for the external MCP surface.
    /// </summary>
    /// <param name = "Uri">Resource URI to replace as a whole.</param>
    /// <param name = "Content">Whole new content of that resource.</param>
    private sealed record McpPutToolArguments(string Uri, string Content);
}
