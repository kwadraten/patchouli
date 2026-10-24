using System.Text.Json.Serialization;
using Patchouli.Core.Results;

namespace Patchouli.Mcp;

/// <summary>
/// Issue #28: one internal implementation a runs domain endpoint can be driven with —
/// an OCR engine id for <c>runs/ocr/control</c>, a translation extension id for
/// <c>runs/translation/control</c>, or a metadata source id for
/// <c>runs/metadata/control</c>. Entries are enumerated live from the host's real
/// registries (OCR adapter registry, extension catalog, metadata source registry) at
/// fetch time; nothing here is a static table that could drift from what the host
/// actually accepts.
/// </summary>
public sealed record McpImplementationEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Description = null,
    [property: JsonPropertyName("identifier_schemes")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? IdentifierSchemes = null,
    [property: JsonPropertyName("is_default")]
    bool IsDefault = false,
    [property: JsonPropertyName("active")] bool Active = true);

/// <summary>
/// Issue #28: live enumeration of the implementations behind one runs domain, used to
/// enrich the control endpoint description so an agent learns which ids it may pass
/// to <c>send</c> (the OCR <c>engine</c> argument, the configured translation engine,
/// metadata source ids and their identifier schemes) without guessing.
/// </summary>
public interface IMcpImplementationCatalogApi
{
    /// <summary>
    /// Lists the implementations of one runs domain (<c>ocr</c>, <c>translation</c> or
    /// <c>metadata</c>, as in <c>RunEndpointCatalog.Domains</c>).
    /// </summary>
    Task<Result<IReadOnlyList<McpImplementationEntry>>> ListAsync(string domain,
        CancellationToken cancellationToken = default);
}
