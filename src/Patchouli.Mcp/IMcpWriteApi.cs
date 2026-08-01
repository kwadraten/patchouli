using Patchouli.Core.Results;
using Patchouli.Core.Ids;

namespace Patchouli.Mcp;

public sealed record McpPutRequest(string Uri, string Content, string BaseRevision);

public sealed record McpPutResponse(string Uri, string Kind, string Revision, bool Writable);

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

public interface IMcpWriteApi
{
    event EventHandler<McpResourceChangedEventArgs>? ResourceChanged;

    Task<Result<McpPutResponse>> PutAsync(
        McpPutRequest request,
        CancellationToken cancellationToken = default);
}
