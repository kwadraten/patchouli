using System.Text.Json;
using Patchouli.Core.Results;
using Patchouli.Mcp;

namespace Patchouli.Host.Plugins;

/// <summary>
/// The development-plugin mount surface: reads the device registry, validates manifests,
/// answers static descriptor/directory/readme/view requests without a process, and lazily
/// supervises one stdio plugin process per mount for real find/fetch/send. Verbs require the
/// manifest declaration AND the registry allowed_verbs grant; nothing else is reachable.
/// </summary>
public sealed class HostPluginApi : IMcpPluginApi, IAsyncDisposable
{
    private readonly string _registryPath;
    private readonly IPluginProcessLauncher _launcher;
    private readonly PluginClientOptions _options;
    private readonly Action<Exception, string>? _reportException;
    private readonly object _gate = new();
    private readonly Dictionary<string, PluginMount> _mounts = new(StringComparer.Ordinal);
    private PluginRegistrySnapshot _registry = new([], []);
    private DateTime _registryStamp = DateTime.MinValue;
    private IReadOnlyList<McpPluginMountInfo>? _mountsCache;
    private bool _disposed;

    /// <summary>Registry location override; production passes the device data root path.</summary>
    public HostPluginApi(string registryPath, IPluginProcessLauncher? launcher = null,
        PluginClientOptions? options = null, Action<Exception, string>? reportException = null)
    {
        _registryPath = registryPath;
        _launcher = launcher ?? new PluginProcessLauncher();
        _options = options ?? new PluginClientOptions();
        _reportException = reportException;
    }

    public IReadOnlyList<string> Diagnostics => Snapshot().Registry.Diagnostics;

    public IReadOnlyList<McpPluginMountInfo> Mounts
    {
        get
        {
            Snapshot();
            lock (_gate)
            {
                return _mountsCache!;
            }
        }
    }

    public McpPluginMountInfo? FindMount(string pluginId)
    {
        return Mounts.FirstOrDefault(mount => string.Equals(mount.PluginId, pluginId, StringComparison.Ordinal));
    }

    public bool AllowsVerb(string pluginId, string verb)
    {
        PluginMount? mount = GetMount(pluginId);
        return mount is not null && mount.EffectiveVerbs.Contains(verb, StringComparer.Ordinal);
    }

    public bool IsEndpoint(string pluginId, string relativePath)
    {
        return GetMount(pluginId)?.Definition.FindEndpoint(relativePath) is not null;
    }

    public IReadOnlyList<McpPluginViewInfo> Views(string pluginId)
    {
        PluginDefinition? definition = GetMount(pluginId)?.Definition;
        return definition?.Views.Select(view => ProjectView(definition, view)).ToArray() ?? [];
    }

    public McpPluginViewInfo? FindView(string pluginId, string viewId)
    {
        PluginDefinition? definition = GetMount(pluginId)?.Definition;
        PluginViewDeclaration? view = definition?.FindView(viewId);
        return view is null ? null : ProjectView(definition!, view);
    }

    /// <summary>Projects one declared view onto canonical mount URIs.</summary>
    private static McpPluginViewInfo ProjectView(PluginDefinition definition, PluginViewDeclaration view)
    {
        return new McpPluginViewInfo(view.Id, view.Title, view.Placement,
            McpResourceUris.PluginUri(definition.Id, "views/" + view.Id),
            McpResourceUris.PluginUri(definition.Id, view.Source), view.Source);
    }

    public Result<McpPluginFetchContent?> TryFetchStatic(string pluginId, string relativePath)
    {
        PluginMount? mount = GetMount(pluginId);
        if (mount is null)
        {
            return Result<McpPluginFetchContent?>.Failure(AppErrorCodes.NotFound,
                $"Plugin '{pluginId}' is not registered or is disabled.");
        }

        PluginDefinition definition = mount.Definition;
        switch (relativePath)
        {
            case "plugin.yml":
                return Result<McpPluginFetchContent?>.Success(
                    new McpPluginFetchContent("application/json", SerializeDescriptor(definition)));
            case "README.md":
                return ReadPackageAsset(definition, definition.ReadmePath, "text/markdown");
            case "":
                return Result<McpPluginFetchContent?>.Success(new McpPluginFetchContent("application/json",
                    SerializeDirectoryListing(definition)));
        }

        if (relativePath == "files" || relativePath.StartsWith("files/", StringComparison.Ordinal))
        {
            return Result<McpPluginFetchContent?>.Failure(AppErrorCodes.UnsupportedOperation,
                "The managed-files capability is not supported by this host.");
        }

        if (relativePath is "views" or "views/")
        {
            // The host-synthesized views namespace is directory-shaped: browse it with find.
            return definition.Views.Count == 0
                ? Result<McpPluginFetchContent?>.Failure(AppErrorCodes.NotFound,
                    $"'{relativePath}' is not a declared resource of plugin '{pluginId}'.")
                : Result<McpPluginFetchContent?>.Failure(AppErrorCodes.InvalidArgument,
                    "Directory listings are browsed with find, not fetched.");
        }

        if (relativePath.StartsWith("views/", StringComparison.Ordinal))
        {
            string viewId = relativePath["views/".Length..];
            PluginViewDeclaration? view = viewId.Contains('/', StringComparison.Ordinal)
                ? null
                : definition.FindView(viewId);
            return view is null
                ? Result<McpPluginFetchContent?>.Failure(AppErrorCodes.NotFound,
                    $"'{relativePath}' is not a declared view of plugin '{pluginId}'.")
                : Result<McpPluginFetchContent?>.Success(new McpPluginFetchContent("application/json",
                    SerializeViewDescriptor(definition, view)));
        }

        PluginVfsEndpoint? endpoint = definition.FindEndpoint(relativePath);
        if (endpoint is not null)
        {
            return Result<McpPluginFetchContent?>.Success(new McpPluginFetchContent("application/json",
                SerializeEndpointDescriptor(definition, endpoint)));
        }

        PluginVfsNode? node = definition.FindNode(relativePath);
        if (node is null)
        {
            return Result<McpPluginFetchContent?>.Failure(AppErrorCodes.NotFound,
                $"'{relativePath}' is not a declared resource of plugin '{pluginId}'.");
        }

        if (node.IsDirectory)
        {
            return Result<McpPluginFetchContent?>.Failure(AppErrorCodes.InvalidArgument,
                "Directory listings are browsed with find, not fetched.");
        }

        if (!node.Verbs.Contains("fetch", StringComparer.Ordinal))
        {
            return Result<McpPluginFetchContent?>.Failure("permission_denied",
                $"fetch is not declared on plugin node '{relativePath}'.");
        }

        return Result<McpPluginFetchContent?>.Success(null); // Plugin-owned content: answer live.
    }

    public async Task<Result<McpPluginFindPage>> FindAsync(string pluginId, string relativeScope,
        string? query, int limit, string? cursor, CancellationToken cancellationToken = default)
    {
        PluginMount? mount = GetMount(pluginId);
        if (mount is null)
        {
            return Result<McpPluginFindPage>.Failure(AppErrorCodes.NotFound,
                $"Plugin '{pluginId}' is not registered or is disabled.");
        }

        // Browsing the mount root is satisfied from the validated manifest; only real
        // plugin-owned lookups start the process.
        if (relativeScope.Length == 0 && string.IsNullOrEmpty(query))
        {
            return Result<McpPluginFindPage>.Success(new McpPluginFindPage(
                StaticRootEntries(mount), null));
        }

        // The host-synthesized views namespace lists statically; there is no plugin-side
        // directory or query to forward to.
        if (relativeScope is "views" or "views/")
        {
            if (mount.Definition.Views.Count == 0)
            {
                return Result<McpPluginFindPage>.Failure(AppErrorCodes.NotFound,
                    $"'{relativeScope}' is not a declared directory of plugin '{pluginId}'.");
            }

            if (!string.IsNullOrEmpty(query))
            {
                return Result<McpPluginFindPage>.Failure(AppErrorCodes.InvalidArgument,
                    "The views namespace is discovery-only and accepts no query.");
            }

            return Result<McpPluginFindPage>.Success(new McpPluginFindPage(
                mount.Definition.Views.Select(view => new McpPluginFindEntry(
                    mount.RootUri + "views/" + view.Id, view.Title, "view")).ToArray(), null));
        }

        if (!mount.Definition.Nodes.Any(node => node.IsDirectory &&
                                                string.Equals(node.Path, relativeScope, StringComparison.Ordinal)))
        {
            PluginVfsNode? fileNode = mount.Definition.FindNode(relativeScope);
            return fileNode is { IsDirectory: false }
                ? Result<McpPluginFindPage>.Failure(AppErrorCodes.InvalidArgument,
                    "find scopes must be the plugin root or a declared directory.")
                : Result<McpPluginFindPage>.Failure(AppErrorCodes.NotFound,
                    $"'{relativeScope}' is not a declared directory of plugin '{pluginId}'.");
        }

        Result<PluginStdioClient> client = await mount.EnsureClientAsync(_launcher, _options,
            _reportException, cancellationToken);
        if (client.IsFailure)
        {
            return Result<McpPluginFindPage>.Failure(client.ErrorCode!, client.ErrorMessage!);
        }

        Result<JsonElement> structured = await client.Value.CallToolAsync("patchouli.plugin.find",
            BuildArgs(("in", relativeScope), ("query", query), ("limit", (object)limit),
                ("cursor", (object?)cursor)), null, cancellationToken);
        if (structured.IsFailure)
        {
            return Result<McpPluginFindPage>.Failure(structured.ErrorCode!, structured.ErrorMessage!);
        }

        return ProjectFindPage(mount, structured.Value);
    }

    public async Task<Result<McpPluginFetchContent>> FetchAsync(string pluginId, string relativePath,
        CancellationToken cancellationToken = default)
    {
        PluginMount? mount = GetMount(pluginId);
        if (mount is null)
        {
            return Result<McpPluginFetchContent>.Failure(AppErrorCodes.NotFound,
                $"Plugin '{pluginId}' is not registered or is disabled.");
        }

        Result<PluginStdioClient> client = await mount.EnsureClientAsync(_launcher, _options,
            _reportException, cancellationToken);
        if (client.IsFailure)
        {
            return Result<McpPluginFetchContent>.Failure(client.ErrorCode!, client.ErrorMessage!);
        }

        Result<JsonElement> structured = await client.Value.CallToolAsync("patchouli.plugin.fetch",
            BuildArgs(("uri", relativePath)), null, cancellationToken);
        if (structured.IsFailure)
        {
            return Result<McpPluginFetchContent>.Failure(structured.ErrorCode!, structured.ErrorMessage!);
        }

        JsonElement body = structured.Value;
        if (!body.TryGetProperty("content", out JsonElement content) ||
            content.ValueKind != JsonValueKind.String ||
            !body.TryGetProperty("content_type", out JsonElement contentType) ||
            contentType.ValueKind != JsonValueKind.String)
        {
            return Result<McpPluginFetchContent>.Failure("unavailable",
                "Plugin returned a malformed fetch result (expected uri/content_type/content).");
        }

        PluginVfsNode? node = mount.Definition.FindNode(relativePath);
        if (node?.Schema is not null)
        {
            string? schemaError = ValidateSchemaAsset(mount.Definition, node.Schema, content.GetString()!);
            if (schemaError is not null)
            {
                return Result<McpPluginFetchContent>.Failure("unavailable", schemaError);
            }
        }

        return Result<McpPluginFetchContent>.Success(
            new McpPluginFetchContent(contentType.GetString()!, content.GetString()!));
    }

    public async Task<Result<McpPluginSendOutcome>> SendAsync(string pluginId, string endpointPath,
        string action, JsonElement args, string? requestId, CancellationToken cancellationToken = default)
    {
        PluginMount? mount = GetMount(pluginId);
        if (mount is null)
        {
            return Result<McpPluginSendOutcome>.Failure(AppErrorCodes.NotFound,
                $"Plugin '{pluginId}' is not registered or is disabled.");
        }

        PluginVfsEndpoint? endpoint = mount.Definition.FindEndpoint(endpointPath);
        PluginEndpointMessage? message = endpoint?.Messages.FirstOrDefault(value =>
            string.Equals(value.Action, action, StringComparison.Ordinal));
        if (endpoint is null || message is null)
        {
            return Result<McpPluginSendOutcome>.Failure(AppErrorCodes.InvalidArgument,
                $"'{action}' is not a declared action of endpoint '{endpointPath}'.");
        }

        if (!message.TargetTypes.Contains("plugin-root", StringComparer.Ordinal))
        {
            return Result<McpPluginSendOutcome>.Failure(AppErrorCodes.UnsupportedOperation,
                $"Action '{action}' targets '{string.Join(",", message.TargetTypes)}', which this host does not issue.");
        }

        if (PluginJsonSchema.Validate(message.InputSchema, args) is { } inputError)
        {
            return Result<McpPluginSendOutcome>.Failure(AppErrorCodes.InvalidArgument,
                $"args failed the declared input schema: {inputError}");
        }

        Result<PluginStdioClient> client = await mount.EnsureClientAsync(_launcher, _options,
            _reportException, cancellationToken);
        if (client.IsFailure)
        {
            return Result<McpPluginSendOutcome>.Failure(client.ErrorCode!, client.ErrorMessage!);
        }

        Result<JsonElement> structured = await client.Value.CallToolAsync("patchouli.plugin.send",
            BuildArgs(("endpoint", endpointPath), ("action", action), ("target", ""),
                ("args", args), ("request_id", requestId)), null, cancellationToken);
        if (structured.IsFailure)
        {
            return Result<McpPluginSendOutcome>.Failure(structured.ErrorCode!, structured.ErrorMessage!);
        }

        JsonElement body = structured.Value;
        if (!body.TryGetProperty("state", out JsonElement state) || state.ValueKind != JsonValueKind.String)
        {
            return Result<McpPluginSendOutcome>.Failure("unavailable",
                "Plugin returned a malformed send result (expected state + result).");
        }

        object? result = null;
        if (body.TryGetProperty("result", out JsonElement resultValue) &&
            resultValue.ValueKind != JsonValueKind.Null)
        {
            if (PluginJsonSchema.Validate(message.ResultSchema, resultValue) is { } resultError)
            {
                return Result<McpPluginSendOutcome>.Failure("unavailable",
                    $"Plugin result failed the declared result schema: {resultError}");
            }

            result = RewriteResultUris(mount, resultValue);
        }

        return Result<McpPluginSendOutcome>.Success(
            new McpPluginSendOutcome(state.GetString()!, result));
    }

    private static object BuildArgs(params (string Name, object? Value)[] pairs)
    {
        Dictionary<string, object?> arguments = new(StringComparer.Ordinal);
        foreach ((string name, object? value) in pairs)
        {
            if (value is not null)
            {
                arguments[name] = value;
            }
        }

        return arguments;
    }

    /// <summary>Rewrites plugin-relative result/resource URIs onto the mount; escaping URIs fail.</summary>
    private object RewriteResultUris(PluginMount mount, JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("resource_uri", out JsonElement resourceUri) &&
            resourceUri.ValueKind == JsonValueKind.String)
        {
            string relative = resourceUri.GetString()!;
            if (!McpResourceUris.IsValidPluginRelativePath(relative))
            {
                throw new InvalidOperationException(
                    $"Plugin result resource_uri '{relative}' escapes the mount.");
            }

            Dictionary<string, JsonElement> rewritten = result.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.Clone(),
                    StringComparer.Ordinal);
            rewritten["resource_uri"] = JsonSerializer.SerializeToElement(
                mount.RootUri + relative);
            return JsonSerializer.SerializeToElement(rewritten);
        }

        return result.Clone();
    }

    private Result<McpPluginFindPage> ProjectFindPage(PluginMount mount, JsonElement body)
    {
        if (!body.TryGetProperty("entries", out JsonElement entries) ||
            entries.ValueKind != JsonValueKind.Array)
        {
            return Result<McpPluginFindPage>.Failure("unavailable",
                "Plugin returned a malformed find result (expected entries).");
        }

        List<McpPluginFindEntry> projected = [];
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("uri", out JsonElement uri) ||
                uri.ValueKind != JsonValueKind.String ||
                !McpResourceUris.IsValidPluginRelativePath(uri.GetString()))
            {
                return Result<McpPluginFindPage>.Failure("unavailable",
                    "Plugin find returned an entry URI escaping its mount.");
            }

            string relative = uri.GetString()!;
            string title = entry.TryGetProperty("title", out JsonElement titleValue) &&
                           titleValue.ValueKind == JsonValueKind.String
                ? titleValue.GetString()!
                : relative;
            string type = entry.TryGetProperty("type", out JsonElement typeValue) &&
                          typeValue.ValueKind == JsonValueKind.String
                ? typeValue.GetString()!
                : "file";
            projected.Add(new McpPluginFindEntry(mount.RootUri + relative, title, type));
        }

        string? continuation = body.TryGetProperty("continuation", out JsonElement token) &&
                               token.ValueKind == JsonValueKind.String
            ? token.GetString()
            : null;
        return Result<McpPluginFindPage>.Success(new McpPluginFindPage(projected, continuation));
    }

    /// <summary>Root browse entries: declared children, endpoints and the reserved descriptor/readme.</summary>
    private static McpPluginFindEntry[] StaticRootEntries(PluginMount mount)
    {
        List<McpPluginFindEntry> entries = [];
        PluginDefinition definition = mount.Definition;
        foreach (PluginVfsNode node in definition.Nodes)
        {
            if (node.Path.Length == 0 || (node.Path.Contains('/', StringComparison.Ordinal) &&
                                          node.Path.TrimEnd('/').Contains('/', StringComparison.Ordinal)))
            {
                continue; // Only immediate root children are listed at the mount root.
            }

            if (node.IsTemplate)
            {
                continue;
            }

            string path = node.IsDirectory ? node.Path.TrimEnd('/') : node.Path;
            if (path.Contains('/', StringComparison.Ordinal))
            {
                continue;
            }

            entries.Add(new McpPluginFindEntry(mount.RootUri + node.Path,
                node.Summary ?? node.Path, node.IsDirectory ? "directory" : "file"));
        }

        foreach (PluginVfsEndpoint endpoint in definition.Endpoints)
        {
            if (!endpoint.Path.Contains('/', StringComparison.Ordinal))
            {
                entries.Add(new McpPluginFindEntry(mount.RootUri + endpoint.Path, endpoint.Path, "endpoint"));
            }
        }

        if (definition.Views.Count > 0)
        {
            entries.Add(new McpPluginFindEntry(mount.RootUri + "views/", "views/", "directory"));
        }

        entries.Add(new McpPluginFindEntry(mount.RootUri + "plugin.yml", "plugin.yml", "file"));
        entries.Add(new McpPluginFindEntry(mount.RootUri + "README.md", "README.md", "file"));
        return entries.ToArray();
    }

    private Result<McpPluginFetchContent?> ReadPackageAsset(PluginDefinition definition, string reference,
        string contentType)
    {
        string full = Path.GetFullPath(Path.Combine(definition.PackageDirectory, reference));
        if (!full.StartsWith(definition.PackageDirectory + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) || !File.Exists(full))
        {
            return Result<McpPluginFetchContent?>.Failure(AppErrorCodes.NotFound,
                $"Package asset '{reference}' is missing.");
        }

        try
        {
            return Result<McpPluginFetchContent?>.Success(
                new McpPluginFetchContent(contentType, File.ReadAllText(full)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<McpPluginFetchContent?>.Failure("unavailable",
                $"Package asset '{reference}' could not be read: {exception.Message}");
        }
    }

    private string? ValidateSchemaAsset(PluginDefinition definition, string schemaReference, string content)
    {
        string schemaPath = Path.GetFullPath(Path.Combine(definition.PackageDirectory, schemaReference));
        try
        {
            using JsonDocument schemaDocument = JsonDocument.Parse(File.ReadAllText(schemaPath));
            using JsonDocument contentDocument = JsonDocument.Parse(content);
            return PluginJsonSchema.Validate(schemaDocument.RootElement, contentDocument.RootElement) is { } error
                ? $"Plugin content for '{schemaReference}' failed its declared schema: {error}"
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return $"Plugin content for '{schemaReference}' is not valid JSON: {exception.Message}";
        }
    }

    private static string SerializeDescriptor(PluginDefinition definition)
    {
        return JsonSerializer.Serialize(new
        {
            id = definition.Id,
            version = definition.Version,
            schema = definition.Schema,
            host_api = definition.HostApi,
            name = definition.Name,
            description = definition.Description,
            readme = definition.ReadmePath,
            nodes = definition.Nodes.Select(node => new
            {
                path = node.Path, kind = node.Kind, verbs = node.Verbs, summary = node.Summary,
                media = node.Media, schema = node.Schema
            }),
            endpoints = definition.Endpoints.Select(endpoint => new
            {
                path = endpoint.Path,
                messages = endpoint.Messages.Select(message => new
                {
                    action = message.Action, target_types = message.TargetTypes,
                    input_schema = message.InputSchemaPath, result_schema = message.ResultSchemaPath,
                    execution_mode = message.ExecutionMode, side_effects = message.SideEffects
                })
            }),
            views = definition.Views.Select(view => new
            {
                view_id = view.Id,
                title = view.Title,
                placement = view.Placement,
                uri = McpResourceUris.PluginUri(definition.Id, "views/" + view.Id),
                source_uri = McpResourceUris.PluginUri(definition.Id, view.Source)
            })
        });
    }

    private static string SerializeDirectoryListing(PluginDefinition definition)
    {
        return JsonSerializer.Serialize(new
        {
            uri = "",
            type = "directory",
            entries = definition.Nodes
                .Where(node => node.Path.Length == 0 ||
                               !node.Path.TrimEnd('/').Contains('/', StringComparison.Ordinal))
                .Where(node => !node.IsTemplate)
                .Select(node => new
                {
                    uri = node.Path,
                    type = node.IsDirectory ? "directory" : "file",
                    summary = node.Summary
                })
        });
    }

    /// <summary>The static views/{view-id} resource: a compact self-description of one declared view.</summary>
    private static string SerializeViewDescriptor(PluginDefinition definition, PluginViewDeclaration view)
    {
        return JsonSerializer.Serialize(new
        {
            uri = McpResourceUris.PluginUri(definition.Id, "views/" + view.Id),
            plugin_id = definition.Id,
            view_id = view.Id,
            title = view.Title,
            placement = view.Placement,
            source_uri = McpResourceUris.PluginUri(definition.Id, view.Source)
        });
    }

    private static string SerializeEndpointDescriptor(PluginDefinition definition, PluginVfsEndpoint endpoint)
    {
        return JsonSerializer.Serialize(new
        {
            endpoint = definition.RootUri + endpoint.Path,
            path = endpoint.Path,
            messages = endpoint.Messages.Select(message => new
            {
                action = message.Action,
                target_types = message.TargetTypes,
                input_schema = message.InputSchemaPath,
                result_schema = message.ResultSchemaPath,
                execution_mode = message.ExecutionMode,
                side_effects = message.SideEffects
            })
        });
    }

    private PluginMount? GetMount(string pluginId)
    {
        Snapshot();
        lock (_gate)
        {
            return _mounts.GetValueOrDefault(pluginId);
        }
    }

    private (PluginRegistrySnapshot Registry, bool Changed) Snapshot()
    {
        DateTime stamp;
        try
        {
            stamp = File.Exists(_registryPath) ? File.GetLastWriteTimeUtc(_registryPath) : DateTime.MinValue;
        }
        catch (Exception)
        {
            stamp = DateTime.MinValue;
        }

        lock (_gate)
        {
            if (_mountsCache is not null && stamp == _registryStamp)
            {
                return (_registry, false);
            }

            _registryStamp = stamp;
            _registry = PluginRegistry.Load(_registryPath);
            RebuildMountsLocked();
            return (_registry, true);
        }
    }

    private void RebuildMountsLocked()
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<McpPluginMountInfo> infos = [];
        foreach (PluginRegistryEntry entry in _registry.Entries)
        {
            Result<PluginDefinition> loaded = PluginManifestLoader.Load(entry.ManifestPath);
            if (loaded.IsFailure)
            {
                continue; // An unloadable manifest registers nothing; diagnostics live in the load failure.
            }

            PluginDefinition definition = loaded.Value;
            if (!seen.Add(definition.Id))
            {
                continue;
            }

            if (!_mounts.TryGetValue(definition.Id, out PluginMount? mount) ||
                mount.Definition != definition)
            {
                mount?.DisposeAsync().AsTask().Wait();
                mount = new PluginMount(definition, entry);
                _mounts[definition.Id] = mount;
            }

            infos.Add(new McpPluginMountInfo(definition.Id, definition.Name, definition.Version,
                definition.RootUri, mount.EffectiveVerbs, mount.StateName));
        }

        foreach (string orphan in _mounts.Keys.Where(id => !seen.Contains(id)).ToArray())
        {
            _mounts[orphan].DisposeAsync().AsTask().Wait();
            _mounts.Remove(orphan);
        }

        _mountsCache = infos;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (PluginMount mount in _mounts.Values)
        {
            await mount.DisposeAsync();
        }
    }

    /// <summary>One mount: static definition + registry grant + the lazily started process.</summary>
    private sealed class PluginMount : IAsyncDisposable
    {
        private readonly SemaphoreSlim _startGate = new(1, 1);
        private Task<Result<PluginStdioClient>>? _startTask;
        private PluginStdioClient? _client;
        private string _stateName = "registered";
        private bool _disposed;

        public PluginMount(PluginDefinition definition, PluginRegistryEntry entry)
        {
            Definition = definition;
            EffectiveVerbs = entry.AllowedVerbs
                .Intersect(definition.DeclaredVerbs, StringComparer.Ordinal)
                .ToArray();
        }

        public PluginDefinition Definition { get; }
        public string RootUri => Definition.RootUri;
        public IReadOnlyList<string> EffectiveVerbs { get; }
        public string StateName => _stateName;

        public async Task<Result<PluginStdioClient>> EnsureClientAsync(IPluginProcessLauncher launcher,
            PluginClientOptions options, Action<Exception, string>? reportException,
            CancellationToken cancellationToken)
        {
            if (_client is { State: PluginClientState.Ready })
            {
                return Result<PluginStdioClient>.Success(_client);
            }

            await _startGate.WaitAsync(cancellationToken);
            try
            {
                if (_client is { State: PluginClientState.Ready })
                {
                    return Result<PluginStdioClient>.Success(_client);
                }

                // A failed start attempt is sticky for this mount: no silent retry storm.
                _startTask ??= StartClientAsync(launcher, options, reportException);
            }
            finally
            {
                _startGate.Release();
            }

            return await _startTask;
        }

        private async Task<Result<PluginStdioClient>> StartClientAsync(IPluginProcessLauncher launcher,
            PluginClientOptions options, Action<Exception, string>? reportException)
        {
            try
            {
                PluginStdioClient client = new(
                    new PluginProcessSpec(Definition.Runtime.Command, Definition.Runtime.Arguments,
                        Definition.PackageDirectory), launcher, options);
                Result started = await client.StartAsync(CancellationToken.None);
                if (started.IsFailure)
                {
                    _stateName = "failed";
                    await client.DisposeAsync();
                    return Result<PluginStdioClient>.Failure("unavailable",
                        started.ErrorMessage ?? "The plugin process failed to start.");
                }

                _client = client;
                _stateName = "ready";
                return Result<PluginStdioClient>.Success(client);
            }
            catch (Exception exception)
            {
                _stateName = "failed";
                reportException?.Invoke(exception, "plugin-start");
                return Result<PluginStdioClient>.Failure("unavailable",
                    $"The plugin process failed to start: {exception.Message}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_client is not null)
            {
                await _client.DisposeAsync();
            }

            _startGate.Dispose();
        }
    }
}
