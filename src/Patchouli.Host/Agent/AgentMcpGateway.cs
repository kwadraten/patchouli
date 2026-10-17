using System.Text.Json;
using System.Text.Json.Serialization;
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

    /// <summary>
    /// Issue #11: images returned by the tool (the <c>view</c> tool). The model adapter
    /// layer must pass these to the provider as vision input — never degrade to a path
    /// or base64 text. Empty for text-only tools.
    /// </summary>
    public IReadOnlyList<AgentImageResult> Images { get; init; } = [];
}

/// <summary>Issue #11: one image returned by a tool call, with its metadata.</summary>
/// <param name="MetadataJson">The JSON text metadata envelope (canonical URI pinned to the
/// actually-used revision, view type, MIME, actual width/height, crop range, image_index).</param>
/// <param name="Base64Data">Raw base64 PNG bytes (no data-URL prefix).</param>
/// <param name="MimeType">Always image/png in v1.</param>
public sealed record AgentImageResult(string MetadataJson, string Base64Data, string MimeType);

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
                "add" => await AddToolAsync(Deserialize<McpAddToolArguments>(payload), cancellationToken)
                    .ConfigureAwait(false),
                "view" => await ViewToolAsync(Deserialize<McpViewToolArguments>(payload), cancellationToken)
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
            .PutAsync(new McpPutRequest(request.Uri, request.Content, request.PreserveTags,
                    request.SourceRevision, request.TranslationVersion,
                    request.Boxes?.Select(box => new McpBoxReplacement(box.BoxId, box.Content)).ToArray()),
                cancellationToken)
            .ConfigureAwait(false);
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

    private static AgentToolOutcome FromAdd(McpCommandResult<McpAddMeta, McpAddResult> result)
    {
        return Render(result);
    }

    private async Task<AgentToolOutcome> AddToolAsync(McpAddToolArguments request,
        CancellationToken cancellationToken)
    {
        return FromAdd(await _commands.AddAsync(new McpAddRequest(request.Path, request.Kind), cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Issue #11: routes the agent-facing <c>view</c> tool through the shared view command.
    /// The text payload carries the metadata envelope; the PNG bytes travel in
    /// <see cref="AgentToolOutcome.Images"/> so the model adapter can pass them as real
    /// vision input instead of a path or base64 text.
    /// </summary>
    private async Task<AgentToolOutcome> ViewToolAsync(McpViewToolArguments request,
        CancellationToken cancellationToken)
    {
        McpViewCommandResult result = await _commands
            .ViewAsync(new McpViewRequest(request.Uri, request.Context, request.MaxEdge), cancellationToken)
            .ConfigureAwait(false);
        McpCommandResult<McpViewMeta, McpViewImageMetadata> command = result.Result;
        if (command.Envelope is null)
        {
            string detail = command.Error?.ToTerminalLine() ?? "The view request failed.";
            return new AgentToolOutcome(false, detail, command.Error?.Name);
        }

        string text = McpCommandService.RenderText(command.Envelope, "json");
        if (!command.IsSuccess || result.PngBytes is null)
        {
            return new AgentToolOutcome(false, text, command.Error?.Name);
        }

        return new AgentToolOutcome(true, text)
        {
            Images =
            [
                new AgentImageResult(text, Convert.ToBase64String(result.PngBytes), "image/png")
            ]
        };
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
    private sealed record McpPutToolArguments(
        [property: JsonPropertyName("uri")] string Uri,
        [property: JsonPropertyName("content")]
        string? Content = null,
        [property: JsonPropertyName("preserve_tags")]
        bool PreserveTags = false,
        [property: JsonPropertyName("source_revision")]
        string? SourceRevision = null,
        [property: JsonPropertyName("translation_version")]
        int? TranslationVersion = null,
        [property: JsonPropertyName("boxes")] IReadOnlyList<McpPutBoxToolArguments>? Boxes = null);

    private sealed record McpPutBoxToolArguments(
        [property: JsonPropertyName("box_id")] string BoxId,
        [property: JsonPropertyName("content")]
        string Content);

    /// <summary>Arguments of the agent-facing MCP <c>add</c> tool.</summary>
    private sealed record McpAddToolArguments(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("kind")] string? Kind = null);

    /// <summary>Arguments of the agent-facing MCP <c>view</c> tool.</summary>
    private sealed record McpViewToolArguments(
        [property: JsonPropertyName("uri")] string Uri,
        [property: JsonPropertyName("context")] string? Context = null,
        [property: JsonPropertyName("max_edge")] int? MaxEdge = null);
}
