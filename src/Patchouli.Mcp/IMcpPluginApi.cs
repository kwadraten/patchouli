using System.Text.Json;
using Patchouli.Core.Results;

namespace Patchouli.Mcp;

/// <summary>
/// One enabled local development-plugin mount projected for the shared MCP command surface.
/// <see cref="Verbs"/> is the effective grant (manifest verbs intersected with the device's
/// registry <c>allowed_verbs</c>); <see cref="State"/> distinguishes a not-yet-started mount
/// from a verified one and from a failed start.
/// </summary>
public sealed record McpPluginMountInfo(
    string PluginId,
    string Name,
    string Version,
    string RootUri,
    IReadOnlyList<string> Verbs,
    string State);

public sealed record McpPluginFindEntry(
    string Uri,
    string Title,
    string Type);

public sealed record McpPluginFindPage(
    IReadOnlyList<McpPluginFindEntry> Entries,
    string? Continuation);

public sealed record McpPluginFetchContent(
    string ContentType,
    string Content);

public sealed record McpPluginSendOutcome(
    string State,
    object? Result);

/// <summary>
/// The concrete result of a plugin <c>put</c>: the plugin-defined state plus the
/// operation's concrete result (declared by the plugin in its readme.md).
/// </summary>
public sealed record McpPluginPutOutcome(
    string State,
    object? Result);

/// <summary>
/// One declared plugin web view ([[ui.views]]) projected for the command surface. The view
/// itself is a host-synthesized resource (<see cref="Uri"/>); <see cref="SourceUri"/>/
/// <see cref="SourcePath"/> address the declared file node that serves its HTML payload.
/// </summary>
public sealed record McpPluginViewInfo(
    string ViewId,
    string Title,
    string Placement,
    string Uri,
    string SourceUri,
    string SourcePath);

/// <summary>
/// The local development-plugin mount surface consumed by <see cref="McpCommandService"/>.
/// Implementations own the device registry (explicit install + approval records), manifest
/// validation and lazy plugin processes. The command service stays transport-free: it only
/// sees mounts, verb gates, declared views, static descriptors and typed find/fetch/send/put
/// operations.
/// </summary>
public interface IMcpPluginApi
{
    /// <summary>Every enabled mount, including failed ones (state "failed").</summary>
    IReadOnlyList<McpPluginMountInfo> Mounts { get; }

    /// <summary>The enabled mount for one plugin id, or null when unregistered/disabled.</summary>
    McpPluginMountInfo? FindMount(string pluginId);

    /// <summary>The registry grant gate: manifest-supported verb explicitly allowed by the device.</summary>
    bool AllowsVerb(string pluginId, string verb);

    /// <summary>Whether the relative path is a declared control endpoint (send target).</summary>
    bool IsEndpoint(string pluginId, string relativePath);

    /// <summary>Declared views of an enabled mount (static manifest data; empty when disabled or none).</summary>
    IReadOnlyList<McpPluginViewInfo> Views(string pluginId);

    /// <summary>One declared view of an enabled mount, or null.</summary>
    McpPluginViewInfo? FindView(string pluginId, string viewId);

    /// <summary>
    /// Process-free fetch for reserved and statically known resources (the plugin descriptor,
    /// README, the mount-root listing and endpoint descriptors). A null value means the path
    /// addresses plugin-owned content and must be answered by the running plugin process; a
    /// failure means the path is not fetchable at all.
    /// </summary>
    Result<McpPluginFetchContent?> TryFetchStatic(string pluginId, string relativePath);

    /// <summary>
    /// Live browse/search inside one plugin mount. <paramref name="relativeScope"/> is "" for the
    /// mount root or a declared directory path ("results/"); the implementation maps plugin-relative
    /// entry URIs back onto canonical mount URIs and enforces that none escape the mount.
    /// </summary>
    Task<Result<McpPluginFindPage>> FindAsync(string pluginId, string relativeScope, string? query,
        int limit, string? cursor, CancellationToken cancellationToken = default);

    /// <summary>Live fetch of plugin-owned file content by relative path.</summary>
    Task<Result<McpPluginFetchContent>> FetchAsync(string pluginId, string relativePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Typed message to a declared control endpoint. The implementation validates args against the
    /// manifest input schema and the returned result against the result schema before reporting
    /// success.
    /// </summary>
    Task<Result<McpPluginSendOutcome>> SendAsync(string pluginId, string endpointPath, string action,
        JsonElement args, string? requestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Plugin-defined write to a declared node. The implementation validates the input content
    /// against the node schema where declared, then delegates the write to the plugin process
    /// (<c>patchouli.plugin.put</c>) and returns the plugin's concrete result. The host
    /// imposes no create-vs-overwrite convention; semantics belong to the plugin.
    /// </summary>
    Task<Result<McpPluginPutOutcome>> PutAsync(string pluginId, string relativePath, string content,
        CancellationToken cancellationToken = default);
}
