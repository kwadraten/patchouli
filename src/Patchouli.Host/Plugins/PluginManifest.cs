using System.Text.Json;

namespace Patchouli.Host.Plugins;

/// <summary>One declared VFS node inside a plugin mount (patchouli-plugin.toml [[vfs.nodes]]).</summary>
public sealed record PluginVfsNode(
    string Path,
    string Kind,
    IReadOnlyList<string> Verbs,
    string? Summary,
    string? Media,
    string? Schema)
{
    public bool IsDirectory => Kind == "directory";
    public bool IsTemplate => Path.Contains('{', StringComparison.Ordinal);
}

/// <summary>One declared endpoint message ([[vfs.endpoints.messages]]).</summary>
public sealed record PluginEndpointMessage(
    string Action,
    IReadOnlyList<string> TargetTypes,
    string InputSchemaPath,
    string ResultSchemaPath,
    string ExecutionMode,
    string SideEffects,
    JsonElement InputSchema,
    JsonElement ResultSchema);

/// <summary>One declared control endpoint ([[vfs.endpoints]]).</summary>
public sealed record PluginVfsEndpoint(
    string Path,
    IReadOnlyList<PluginEndpointMessage> Messages);

/// <summary>Process launch parameters declared by [runtime].</summary>
public sealed record PluginRuntimeSpec(
    string Command,
    IReadOnlyList<string> Arguments);

/// <summary>
/// One declared web view inside a plugin mount ([[ui.views]]). The view itself is a
/// host-synthesized resource at patchouli://plugin/{id}/views/{view-id}; <see cref="Source"/>
/// is the plugin-relative file node that serves its HTML payload.
/// </summary>
public sealed record PluginViewDeclaration(
    string Id,
    string Title,
    string Placement,
    string Source);

/// <summary>
/// A validated patchouli-plugin.toml plus the package-relative assets it references.
/// The manifest is the single source of truth for the mount surface; nothing undeclared is
/// ever exposed by the host.
/// </summary>
public sealed record PluginDefinition(
    string Id,
    string Version,
    string Schema,
    string HostApi,
    string Name,
    string Description,
    string ReadmePath,
    string PackageDirectory,
    PluginRuntimeSpec Runtime,
    IReadOnlyList<PluginVfsNode> Nodes,
    IReadOnlyList<PluginVfsEndpoint> Endpoints,
    IReadOnlyList<PluginViewDeclaration> Views)
{
    public string RootUri => Patchouli.Mcp.McpResourceUris.PluginRootUri(Id);

    /// <summary>All verbs this manifest declares support for: node verbs plus send when endpoints exist and view when views exist.</summary>
    public IReadOnlyList<string> DeclaredVerbs { get; } =
        ComputeDeclaredVerbs(Nodes, Endpoints, Views);

    private static string[] ComputeDeclaredVerbs(IReadOnlyList<PluginVfsNode> nodes,
        IReadOnlyList<PluginVfsEndpoint> endpoints, IReadOnlyList<PluginViewDeclaration> views)
    {
        HashSet<string> verbs = new(StringComparer.Ordinal);
        foreach (PluginVfsNode node in nodes)
        {
            foreach (string verb in node.Verbs)
            {
                verbs.Add(verb);
            }
        }

        if (endpoints.Count > 0)
        {
            verbs.Add("send");
        }

        if (views.Count > 0)
        {
            verbs.Add("view");
        }

        return verbs.ToArray();
    }

    public PluginVfsEndpoint? FindEndpoint(string relativePath)
    {
        return Endpoints.FirstOrDefault(endpoint =>
            string.Equals(endpoint.Path, relativePath, StringComparison.Ordinal));
    }

    /// <summary>The declared view with one view id, if any.</summary>
    public PluginViewDeclaration? FindView(string viewId)
    {
        return Views.FirstOrDefault(view =>
            string.Equals(view.Id, viewId, StringComparison.Ordinal));
    }

    /// <summary>The declared node matching a concrete relative path (literal or template), if any.</summary>
    public PluginVfsNode? FindNode(string relativePath)
    {
        foreach (PluginVfsNode node in Nodes)
        {
            if (node.IsTemplate
                    ? Patchouli.Mcp.McpResourceUris.MatchesPluginTemplatePath(node.Path, relativePath)
                    : string.Equals(node.Path, relativePath, StringComparison.Ordinal))
            {
                return node;
            }
        }

        return null;
    }
}
