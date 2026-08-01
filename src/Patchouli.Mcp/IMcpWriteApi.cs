using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Mcp;

public sealed record McpPutRequest(string Uri, string Content);

public sealed record McpPutResponse(string Uri, string ResourceType, bool Committed, int ContentBytes);

public sealed class McpResourceChangedEventArgs : EventArgs
{
    public McpResourceChangedEventArgs(string uri, string kind, string revision, ItemId? itemId = null)
    {
        Uri = uri;
        Kind = kind;
        Revision = revision;
        ItemId = itemId;
    }

    public string Uri { get; }
    public string Kind { get; }
    public string Revision { get; }
    public ItemId? ItemId { get; }
}

/// <summary>
/// The single host write service. put replaces exactly one whole writable resource after
/// full-content validation and an atomic commit; it has no base-revision precondition.
/// </summary>
public interface IMcpWriteApi
{
    event EventHandler<McpResourceChangedEventArgs>? ResourceChanged;

    Task<Result<McpPutResponse>> PutAsync(
        McpPutRequest request,
        CancellationToken cancellationToken = default);
}
