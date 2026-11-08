using System.Text.Json;
using System.Text.RegularExpressions;
using Patchouli.Core.Results;
using Patchouli.Mcp;

namespace Patchouli.Host.Plugins;

/// <summary>
/// Loads and strictly validates patchouli-plugin.toml: identity/SemVer/API pins, library
/// grants (must be empty in this MVP), package-relative non-traversing paths, reserved node
/// names, node verb support, duplicate/conflicting paths, endpoint action uniqueness, and
/// every referenced README/schema document (which must exist and compile under the supported
/// schema subset). Nothing undeclared is mountable.
/// </summary>
public static class PluginManifestLoader
{
    public const string ManifestFileName = "patchouli-plugin.toml";
    public const string SupportedSchema = "patchouli.plugin/1";
    public const string SupportedHostApi = "patchouli.plugins/1";
    private const int MaxManifestBytes = 256 * 1024;
    private const int MaxAssetBytes = 1024 * 1024;

    private static readonly Regex SemVer = new(
        @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\+([0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$",
        RegexOptions.Compiled);

    private static readonly Regex ActionName = new(@"^[a-zA-Z][a-zA-Z0-9_]{0,63}$", RegexOptions.Compiled);

    private static readonly Regex ViewId = new(@"^[a-zA-Z][a-zA-Z0-9_-]{0,63}$", RegexOptions.Compiled);

    /// <summary>Ids that would collide with built-in VFS scopes or reserved namespaces.</summary>
    private static readonly HashSet<string> ReservedIds = new(StringComparer.Ordinal)
    {
        "plugin", "items", "texts", "translations", "csl-styles", "runs", "library",
        "library.yml", "library.toon", "actions", "documents", "styles", "evidence"
    };

    /// <summary>VFS paths the host synthesizes itself; plugins may never declare them.</summary>
    private static readonly HashSet<string> ReservedNodePaths = new(StringComparer.Ordinal)
    {
        "plugin.yml", "README.md", "files", "views"
    };

    /// <summary>Host-synthesized path prefixes; plugins may never declare paths under them.</summary>
    private static readonly string[] ReservedNodePrefixes = ["files/", "views/"];

    private static readonly HashSet<string> ViewPlacements = new(StringComparer.Ordinal)
    {
        "tab", "window", "pdf-sidebar"
    };

    private const int MaxViewTitleLength = 200;

    private static readonly HashSet<string> NodeVerbs = new(StringComparer.Ordinal) { "find", "fetch" };

    private static readonly HashSet<string> ExecutionModes = new(StringComparer.Ordinal) { "sync" };

    public static Result<PluginDefinition> Load(string manifestPath)
    {
        try
        {
            return LoadCore(manifestPath);
        }
        catch (PluginManifestException exception)
        {
            return Result<PluginDefinition>.Failure(AppErrorCodes.InvalidArgument, exception.Message);
        }
        catch (PluginTomlException exception)
        {
            return Result<PluginDefinition>.Failure(AppErrorCodes.InvalidArgument,
                $"Invalid plugin manifest TOML: {exception.Message}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return Result<PluginDefinition>.Failure(AppErrorCodes.InvalidArgument,
                $"Cannot load plugin manifest '{manifestPath}': {exception.Message}");
        }
    }

    private static Result<PluginDefinition> LoadCore(string manifestPath)
    {
        if (!Path.IsPathFullyQualified(manifestPath))
        {
            throw Fail($"Manifest path '{manifestPath}' must be absolute.");
        }

        FileInfo file = new(manifestPath);
        if (!file.Exists)
        {
            throw Fail($"Manifest '{manifestPath}' does not exist.");
        }

        if (file.Length > MaxManifestBytes)
        {
            throw Fail($"Manifest '{manifestPath}' exceeds {MaxManifestBytes} bytes.");
        }

        string packageDirectory = Path.GetFullPath(file.DirectoryName!);
        IReadOnlyDictionary<string, object?> document = PluginToml.Parse(File.ReadAllText(file.FullName));

        string id = RequiredString(document, "id");
        if (!McpResourceUris.IsValidPluginId(id))
        {
            throw Fail("Manifest 'id' must be a dotted path token of letters, digits, '-' and '_'.");
        }

        if (ReservedIds.Contains(id))
        {
            throw Fail($"Plugin id '{id}' collides with a reserved built-in scope.");
        }

        string version = RequiredString(document, "version");
        if (!SemVer.IsMatch(version))
        {
            throw Fail($"Manifest 'version' '{version}' is not a valid SemVer.");
        }

        string schema = RequiredString(document, "schema");
        if (!string.Equals(schema, SupportedSchema, StringComparison.Ordinal))
        {
            throw Fail($"Manifest schema '{schema}' is not supported; this host requires '{SupportedSchema}'.");
        }

        // The contract spells this key 'host-api' (canonical); 'host_api' is accepted
        // as a compatibility alias only.
        string hostApi = document.ContainsKey("host-api")
            ? RequiredString(document, "host-api")
            : RequiredString(document, "host_api");
        if (!string.Equals(hostApi, SupportedHostApi, StringComparison.Ordinal))
        {
            throw Fail($"Manifest host-api '{hostApi}' is not supported; this host requires '{SupportedHostApi}'.");
        }

        string name = RequiredString(document, "name");
        string description = RequiredString(document, "description");
        string readmePath = SafePackagePath(RequiredString(document, "readme"), "readme");
        RequirePackageFile(packageDirectory, readmePath, "readme");

        RejectUnsupportedCapabilities(document);

        PluginRuntimeSpec runtime = ParseRuntime(document);
        IReadOnlyList<string> grants = ParseLibraryGrants(document);
        if (grants.Count > 0)
        {
            throw Fail(
                "Manifest declares library grants; plugin library grants are not supported in this host version.");
        }

        VfsDeclaration vfs = ParseVfs(document, packageDirectory);
        IReadOnlyList<PluginViewDeclaration> views = ParseUiViews(document, vfs.Nodes);
        return Result<PluginDefinition>.Success(new PluginDefinition(id, version, schema, hostApi,
            name, description, readmePath, packageDirectory, runtime, vfs.Nodes, vfs.Endpoints, views));
    }

    /// <summary>
    /// Parses the optional [ui] section's [[ui.views]] array. Every declared field is strictly
    /// validated: the view id is a unique token, placement is a closed enum, and source must
    /// name a literal (non-template) declared file node that carries the fetch verb. Unknown
    /// keys inside a view entry are ignored, matching node parsing.
    /// </summary>
    private static IReadOnlyList<PluginViewDeclaration> ParseUiViews(
        IReadOnlyDictionary<string, object?> document, IReadOnlyList<PluginVfsNode> nodes)
    {
        if (!document.TryGetValue("ui", out object? uiValue))
        {
            return [];
        }

        IReadOnlyDictionary<string, object?> ui = RequireTable(uiValue, "ui");
        if (!ui.TryGetValue("views", out object? viewsValue))
        {
            return [];
        }

        List<PluginViewDeclaration> views = [];
        HashSet<string> seenIds = new(StringComparer.Ordinal);
        foreach (object? viewValue in RequireArray(viewsValue, "ui.views"))
        {
            IReadOnlyDictionary<string, object?> view = RequireTable(viewValue, "ui.views[]");
            string id = RequiredString(view, "id");
            if (!ViewId.IsMatch(id))
            {
                throw Fail($"ui.views id '{id}' must match {ViewId}.");
            }

            if (!seenIds.Add(id))
            {
                throw Fail($"Duplicate ui.views id '{id}'.");
            }

            string title = RequiredString(view, "title");
            if (title.Length > MaxViewTitleLength)
            {
                throw Fail($"ui.views '{id}' title exceeds {MaxViewTitleLength} characters.");
            }

            string placement = RequiredString(view, "placement");
            if (!ViewPlacements.Contains(placement))
            {
                throw Fail($"ui.views '{id}' placement '{placement}' is not supported; " +
                           "use tab, window or pdf-sidebar.");
            }

            string source = RequiredString(view, "source");
            PluginVfsNode? sourceNode = nodes.FirstOrDefault(node =>
                !node.IsDirectory && !node.IsTemplate &&
                string.Equals(node.Path, source, StringComparison.Ordinal));
            if (sourceNode is null)
            {
                throw Fail($"ui.views '{id}' source '{source}' is not a declared file node.");
            }

            if (!sourceNode.Verbs.Contains("fetch", StringComparer.Ordinal))
            {
                throw Fail($"ui.views '{id}' source '{source}' must be a file node with the fetch verb.");
            }

            views.Add(new PluginViewDeclaration(id, title, placement, source));
        }

        return views;
    }

    private static void RejectUnsupportedCapabilities(IReadOnlyDictionary<string, object?> document)
    {
        foreach (string section in new[] { "capabilities", "subjects", "client" })
        {
            if (document.ContainsKey(section))
            {
                throw Fail($"Manifest section '[{section}]' declares capabilities this host does not issue.");
            }
        }
    }

    private static PluginRuntimeSpec ParseRuntime(IReadOnlyDictionary<string, object?> document)
    {
        IReadOnlyDictionary<string, object?> runtime = RequiredTable(document, "runtime");
        string transport = RequiredString(runtime, "transport");
        if (!string.Equals(transport, "stdio", StringComparison.Ordinal))
        {
            throw Fail($"Runtime transport '{transport}' is not supported; only 'stdio' is mountable.");
        }

        string command = RequiredString(runtime, "command");
        if (command.Any(char.IsWhiteSpace) || command.Contains('\0'))
        {
            throw Fail("Runtime command must be a single executable token (no whitespace or shell syntax).");
        }

        IReadOnlyList<string> args = OptionalStringArray(runtime, "args") ?? [];
        if (args.Count > 64 || args.Any(value => value.Contains('\0')))
        {
            throw Fail("Runtime args must be at most 64 plain strings.");
        }

        return new PluginRuntimeSpec(command, args);
    }

    private static IReadOnlyList<string> ParseLibraryGrants(IReadOnlyDictionary<string, object?> document)
    {
        if (!document.TryGetValue("library", out object? library) || library is null)
        {
            return [];
        }

        if (library is not IReadOnlyDictionary<string, object?> table &&
            library is not Dictionary<string, object?>)
        {
            throw Fail("Manifest section '[library]' must be a table.");
        }

        IReadOnlyDictionary<string, object?> libraryTable = (IReadOnlyDictionary<string, object?>)library;
        return OptionalStringArray(libraryTable, "grants") ?? [];
    }

    private sealed record VfsDeclaration(
        IReadOnlyList<PluginVfsNode> Nodes,
        IReadOnlyList<PluginVfsEndpoint> Endpoints);

    private static VfsDeclaration ParseVfs(IReadOnlyDictionary<string, object?> document, string packageDirectory)
    {
        List<PluginVfsNode> nodes = [];
        List<PluginVfsEndpoint> endpoints = [];
        if (!document.TryGetValue("vfs", out object? vfsValue))
        {
            throw Fail("Manifest must declare a [vfs] section.");
        }

        IReadOnlyDictionary<string, object?> vfs = RequireTable(vfsValue, "vfs");
        HashSet<string> seenPaths = new(StringComparer.Ordinal);
        bool sawRoot = false;
        if (vfs.TryGetValue("nodes", out object? nodesValue))
        {
            foreach (object? nodeValue in RequireArray(nodesValue, "vfs.nodes"))
            {
                IReadOnlyDictionary<string, object?> node = RequireTable(nodeValue, "vfs.nodes[]");
                PluginVfsNode parsed = ParseNode(node, packageDirectory);
                if (parsed.Path.Length == 0)
                {
                    if (sawRoot || !parsed.IsDirectory)
                    {
                        throw Fail("Exactly one root directory node with path = \"\" is required.");
                    }

                    sawRoot = true;
                }

                if (!seenPaths.Add(parsed.Path))
                {
                    throw Fail($"Duplicate vfs node path '{parsed.Path}'.");
                }

                nodes.Add(parsed);
            }
        }

        if (!sawRoot)
        {
            throw Fail("Manifest must declare the root directory node (path = \"\").");
        }

        CheckNodeConflicts(nodes);
        if (vfs.TryGetValue("endpoints", out object? endpointsValue))
        {
            foreach (object? endpointValue in RequireArray(endpointsValue, "vfs.endpoints"))
            {
                IReadOnlyDictionary<string, object?> endpoint = RequireTable(endpointValue, "vfs.endpoints[]");
                endpoints.Add(ParseEndpoint(endpoint, packageDirectory, seenPaths));
            }
        }

        return new VfsDeclaration(nodes, endpoints);
    }

    private static PluginVfsNode ParseNode(IReadOnlyDictionary<string, object?> node, string packageDirectory)
    {
        // path = "" is the declared mount-root directory; RequiredString would reject it.
        string path = node.TryGetValue("path", out object? pathValue) && pathValue is string text
            ? text
            : throw Fail("vfs node requires a string 'path'.");
        string kind = RequiredString(node, "kind");
        if (kind is not ("file" or "directory"))
        {
            throw Fail($"vfs node '{path}' kind must be 'file' or 'directory'.");
        }

        if (path.Length == 0 && kind != "directory")
        {
            throw Fail("The root vfs node must be a directory.");
        }

        if (path.Length > 0)
        {
            ValidateNodePath(path, kind);
        }

        IReadOnlyList<string> verbs = OptionalStringArray(node, "verbs")
                                      ?? throw Fail($"vfs node '{path}' requires a non-empty verbs array.");
        if (verbs.Count == 0 || verbs.Any(verb => !NodeVerbs.Contains(verb)))
        {
            throw Fail($"vfs node '{path}' verbs must be a non-empty subset of find/fetch.");
        }

        if (kind == "file" && verbs.Any(verb => verb != "fetch"))
        {
            throw Fail($"vfs node '{path}' is a file; only the fetch verb is supported.");
        }

        if (IsReservedPath(path))
        {
            throw Fail($"vfs node '{path}' collides with a host-reserved path.");
        }

        string? schemaPath = OptionalString(node, "schema");
        if (schemaPath is not null)
        {
            RequireSchemaFile(packageDirectory, schemaPath, $"vfs node '{path}'");
        }

        return new PluginVfsNode(path, kind, verbs, OptionalString(node, "summary"),
            OptionalString(node, "media"), schemaPath);
    }

    private static void ValidateNodePath(string path, string kind)
    {
        if (kind == "directory")
        {
            if (!path.EndsWith("/", StringComparison.Ordinal) ||
                !McpResourceUris.IsValidPluginRelativePath(path) || path.Contains('{'))
            {
                throw Fail($"Directory node path '{path}' must be a valid relative path ending in '/'.");
            }
        }
        else if (!McpResourceUris.IsValidPluginRelativePath(path) &&
                 !McpResourceUris.IsValidPluginTemplatePath(path))
        {
            throw Fail($"File node path '{path}' is not a valid non-traversing relative path.");
        }
    }

    private static void CheckNodeConflicts(IReadOnlyList<PluginVfsNode> nodes)
    {
        foreach (PluginVfsNode node in nodes)
        {
            foreach (PluginVfsNode other in nodes)
            {
                if (ReferenceEquals(node, other) || !other.IsDirectory || other.Path.Length == 0)
                {
                    continue;
                }

                // A file path that equals a directory's address (without the slash) conflicts.
                if (!node.IsDirectory && string.Equals(node.Path, other.Path.TrimEnd('/'), StringComparison.Ordinal))
                {
                    throw Fail($"vfs node '{node.Path}' conflicts with directory '{other.Path}'.");
                }
            }
        }
    }

    private static PluginVfsEndpoint ParseEndpoint(IReadOnlyDictionary<string, object?> endpoint,
        string packageDirectory, HashSet<string> seenPaths)
    {
        string path = RequiredString(endpoint, "path");
        if (!McpResourceUris.IsValidPluginRelativePath(path) || path.Length == 0 ||
            path.EndsWith("/", StringComparison.Ordinal) || path.Contains('{'))
        {
            throw Fail($"Endpoint path '{path}' must be a valid relative file path.");
        }

        if (IsReservedPath(path))
        {
            throw Fail($"Endpoint path '{path}' collides with a host-reserved path.");
        }

        if (!seenPaths.Add(path))
        {
            throw Fail($"Endpoint path '{path}' conflicts with a declared vfs node path.");
        }

        List<PluginEndpointMessage> messages = [];
        HashSet<string> actions = new(StringComparer.Ordinal);
        foreach (object? messageValue in RequireArray(
                     endpoint.TryGetValue("messages", out object? m) ? m : null, $"vfs.endpoints '{path}'.messages"))
        {
            IReadOnlyDictionary<string, object?> message = RequireTable(messageValue,
                $"vfs.endpoints '{path}'.messages[]");
            string action = RequiredString(message, "action");
            if (!ActionName.IsMatch(action) || !actions.Add(action))
            {
                throw Fail($"Endpoint '{path}' has a duplicate or invalid action '{action}'.");
            }

            IReadOnlyList<string> targetTypes = OptionalStringArray(message, "target_types") ?? [];
            if (targetTypes.Count == 0)
            {
                throw Fail($"Endpoint '{path}' action '{action}' requires non-empty target_types.");
            }

            string inputSchemaPath = SafePackagePath(RequiredString(message, "input_schema"),
                $"endpoint '{path}' action '{action}' input_schema");
            string resultSchemaPath = SafePackagePath(RequiredString(message, "result_schema"),
                $"endpoint '{path}' action '{action}' result_schema");
            JsonElement inputSchema = RequireSchemaFile(packageDirectory, inputSchemaPath,
                $"endpoint '{path}' action '{action}' input_schema");
            JsonElement resultSchema = RequireSchemaFile(packageDirectory, resultSchemaPath,
                $"endpoint '{path}' action '{action}' result_schema");
            string mode = RequiredString(message, "execution_mode");
            if (!ExecutionModes.Contains(mode))
            {
                throw Fail($"Endpoint '{path}' action '{action}' execution_mode '{mode}' is not supported.");
            }

            string sideEffects = RequiredString(message, "side_effects");
            if (sideEffects.Length > 4096)
            {
                throw Fail($"Endpoint '{path}' action '{action}' side_effects is too long.");
            }

            messages.Add(new PluginEndpointMessage(action, targetTypes, inputSchemaPath, resultSchemaPath,
                mode, sideEffects, inputSchema, resultSchema));
        }

        if (messages.Count == 0)
        {
            throw Fail($"Endpoint '{path}' must declare at least one message action.");
        }

        return new PluginVfsEndpoint(path, messages);
    }

    /// <summary>
    /// Whether a node or endpoint path collides with a host-synthesized namespace
    /// (plugin.yml, README.md, files/, the views/ view namespace).
    /// </summary>
    private static bool IsReservedPath(string path)
    {
        return ReservedNodePaths.Contains(path) ||
               ReservedNodePrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>Resolves a package-relative reference; traversal or absolute paths are rejected.</summary>
    private static string SafePackagePath(string reference, string field)
    {
        if (string.IsNullOrWhiteSpace(reference) || Path.IsPathFullyQualified(reference) ||
            reference.Contains('\\') || reference.Contains('%') || reference.Contains('?') ||
            reference.Contains('#') || reference.StartsWith('/') ||
            reference.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw Fail($"Manifest field '{field}' references an unsafe package path '{reference}'.");
        }

        return reference;
    }

    private static void RequirePackageFile(string packageDirectory, string reference, string field)
    {
        string full = Path.GetFullPath(Path.Combine(packageDirectory, reference));
        if (!full.StartsWith(packageDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            !File.Exists(full))
        {
            throw Fail($"Manifest field '{field}' references '{reference}' which does not exist in the package.");
        }

        if (new FileInfo(full).Length > MaxAssetBytes)
        {
            throw Fail($"Manifest field '{field}' references '{reference}' exceeding {MaxAssetBytes} bytes.");
        }
    }

    private static JsonElement RequireSchemaFile(string packageDirectory, string reference, string field)
    {
        SafePackagePath(reference, field);
        RequirePackageFile(packageDirectory, reference, field);
        string full = Path.GetFullPath(Path.Combine(packageDirectory, reference));
        JsonElement schema;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(full));
            schema = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw Fail($"Schema '{reference}' for {field} is not valid JSON: {exception.Message}");
        }

        if (PluginJsonSchema.CheckSupported(schema) is { } unsupported)
        {
            throw Fail($"Schema '{reference}' for {field} is unsupported: {unsupported}");
        }

        return schema;
    }

    private static string RequiredString(IReadOnlyDictionary<string, object?> table, string key)
    {
        return table.TryGetValue(key, out object? value) && value is string text && text.Length > 0
            ? text
            : throw Fail($"Manifest requires non-empty string '{key}'.");
    }

    private static string? OptionalString(IReadOnlyDictionary<string, object?> table, string key)
    {
        return table.TryGetValue(key, out object? value) && value is string text && text.Length > 0
            ? text
            : null;
    }

    private static IReadOnlyList<string>? OptionalStringArray(IReadOnlyDictionary<string, object?> table, string key)
    {
        if (!table.TryGetValue(key, out object? value))
        {
            return null;
        }

        if (value is not List<object?> list)
        {
            throw Fail($"Manifest field '{key}' must be an array of strings.");
        }

        if (list.Any(entry => entry is not string))
        {
            throw Fail($"Manifest field '{key}' must be an array of strings.");
        }

        return list.Cast<string>().ToArray();
    }

    private static IReadOnlyDictionary<string, object?> RequiredTable(
        IReadOnlyDictionary<string, object?> table, string key)
    {
        return table.TryGetValue(key, out object? value)
            ? RequireTable(value, key)
            : throw Fail($"Manifest requires table '[{key}]'.");
    }

    private static IReadOnlyDictionary<string, object?> RequireTable(object? value, string field)
    {
        return value is IReadOnlyDictionary<string, object?> table
            ? table
            : throw Fail($"Manifest field '{field}' must be a table.");
    }

    private static IReadOnlyList<object?> RequireArray(object? value, string field)
    {
        return value is List<object?> list
            ? list
            : throw Fail($"Manifest field '{field}' must be an array.");
    }

    private static PluginManifestException Fail(string message)
    {
        return new PluginManifestException(message);
    }

    private sealed class PluginManifestException(string message) : Exception(message);
}
