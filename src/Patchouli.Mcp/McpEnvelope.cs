using System.Text.Json.Serialization;

namespace Patchouli.Mcp;

/// <summary>
/// Shared JSON response envelope used by both the MCP tools and the patchouli-cli
/// executable: { data, revision, warnings, continuation, error }.
/// </summary>
public sealed record McpEnvelope<TData>(
    [property: JsonPropertyName("data")] TData Data,
    [property: JsonPropertyName("revision")]
    string? Revision = null,
    [property: JsonPropertyName("warnings")]
    IReadOnlyList<string> Warnings = null!,
    [property: JsonPropertyName("continuation")]
    string? Continuation = null,
    [property: JsonPropertyName("error")]
    McpToolError? Error = null)
{
    public static McpEnvelope<TData> Create(
        TData data,
        string? revision = null,
        IReadOnlyList<string>? warnings = null,
        string? continuation = null,
        McpToolError? error = null)
    {
        return new McpEnvelope<TData>(data, revision, warnings ?? Array.Empty<string>(), continuation, error);
    }
}
