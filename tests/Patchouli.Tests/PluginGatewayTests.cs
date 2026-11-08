using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Core.View;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Mcp;

namespace Patchouli.Tests;

/// <summary>
/// Command-surface tests for the plugin mount seam: URI parsing, root discovery, static vs
/// live fetch routing, registry allowed_verbs + existing switch/domain gates, send target
/// rules and put/cite denial on the patchouli://plugin/ namespace.
/// </summary>
public sealed class PluginGatewayTests
{
    private const string PluginId = "kwadraten.kuzushi-classifier";
    private const string Root = "patchouli://plugin/kwadraten.kuzushi-classifier/";

    private static McpCommandService CreateCommands(FakePluginApi? plugins = null,
        Func<CancellationToken, Task<McpServerSettings>>? permissions = null, bool sendEnabled = true)
    {
        return new McpCommandService(Stub<IMcpReadApi>.Create(), Stub<IMcpWriteApi>.Create(),
            Stub<IBiblatexImportService>.Create(), Stub<IItemService>.Create(),
            Stub<IVersionedEvidenceReader>.Create(), sendEnabled: sendEnabled,
            permissionSettings: permissions, pluginApi: plugins);
    }

    [Fact]
    public void Plugin_uris_parse_to_dedicated_kinds()
    {
        McpResourceUris.Parse("patchouli://plugin/").Value.Kind.Should().Be(McpUriKind.PluginsScope);
        McpResourceUris.Parse("patchouli://plugin").Value.Kind.Should().Be(McpUriKind.PluginsScope);
        McpUriParseResult root = McpResourceUris.Parse(Root).Value;
        root.Kind.Should().Be(McpUriKind.Plugin);
        root.PluginId.Should().Be(PluginId);
        McpUriParseResult file = McpResourceUris.Parse(Root + "status.json").Value;
        file.Kind.Should().Be(McpUriKind.PluginResource);
        file.PluginPath.Should().Be("status.json");
        McpResourceUris.Parse(Root + "results/").Value.PluginPath.Should().Be("results/");

        foreach (string bad in new[]
                 {
                     "patchouli://plugin/bad%20id/x", "patchouli://plugin/a/../x",
                     "patchouli://plugin/a/x?y=1", "patchouli://plugin/a//x", "patchouli://plugin//x"
                 })
        {
            McpResourceUris.Parse(bad).IsFailure.Should().BeTrue(bad);
        }
    }

    [Fact]
    public async Task Root_discovery_is_unchanged_without_plugins_and_lists_mounted_namespace()
    {
        McpCommandResult<McpFindMeta, object> bare =
            await CreateCommands().FindAsync(new McpFindRequest(null, null, null));
        bare.IsSuccess.Should().BeTrue();
        bare.Envelope!.Entries.Should().HaveCount(6);

        FakePluginApi plugins = new(PluginId, ["find", "fetch", "send"]);
        McpCommandResult<McpFindMeta, object> mounted =
            await CreateCommands(plugins).FindAsync(new McpFindRequest(null, null, null));
        mounted.IsSuccess.Should().BeTrue();
        mounted.Envelope!.Entries.Should().HaveCount(7);
        mounted.Envelope.Entries.Cast<McpFindEntry>().Should()
            .Contain(entry => entry.Uri == "patchouli://plugin/" && entry.Type == "directory");

        McpCommandResult<McpFindMeta, object> scope = await CreateCommands(plugins)
            .FindAsync(new McpFindRequest(null, "patchouli://plugin/", null));
        scope.IsSuccess.Should().BeTrue();
        scope.Envelope!.Entries.Cast<McpFindEntry>().Should()
            .Contain(entry => entry.Uri == Root);
    }

    [Fact]
    public async Task Static_descriptors_do_not_launch_and_files_go_live()
    {
        FakePluginApi plugins = new(PluginId, ["find", "fetch", "send"]);
        McpCommandService commands = CreateCommands(plugins);

        McpCommandResult<McpFetchMeta, McpFetchResult> descriptor = await commands.FetchAsync(
            new McpFetchRequest([Root + "plugin.yml"], null, null));
        descriptor.IsSuccess.Should().BeTrue();
        plugins.LiveFetches.Should().Be(0); // static fetch never reaches the process

        McpCommandResult<McpFetchMeta, McpFetchResult> endpoint = await commands.FetchAsync(
            new McpFetchRequest([Root + "control"], null, null));
        endpoint.IsSuccess.Should().BeTrue();
        plugins.LiveFetches.Should().Be(0);

        McpCommandResult<McpFetchMeta, McpFetchResult> status = await commands.FetchAsync(
            new McpFetchRequest([Root + "status.json"], null, null));
        status.IsSuccess.Should().BeTrue();
        plugins.LiveFetches.Should().Be(1);
        status.Envelope!.Entries[0].ContentType.Should().Be("application/json");
    }

    [Fact]
    public async Task Registry_and_switch_gates_deny_plugin_operations()
    {
        FakePluginApi noVerbs = new(PluginId, []);
        McpCommandService commands = CreateCommands(noVerbs);

        McpCommandResult<McpFetchMeta, McpFetchResult> fetch = await commands.FetchAsync(
            new McpFetchRequest([Root + "status.json"], null, null));
        fetch.Envelope!.Entries[0].Error.Should().Contain("PERMISSION_DENIED");

        McpCommandResult<McpFindMeta, object> find = await commands.FindAsync(
            new McpFindRequest(null, Root, null));
        find.Error!.Code.Should().Be((int)McpErrorCode.PermissionDenied);

        FakePluginApi sendOnly = new(PluginId, ["fetch"]);
        McpCommandResult<McpActionSendMeta, McpActionSendResult> send = await CreateCommands(sendOnly)
            .SendAsync(new McpActionSendRequest(Root, "classify",
                JsonSerializer.SerializeToElement(new { }), Root + "control"));
        send.Error!.Code.Should().Be((int)McpErrorCode.PermissionDenied);

        McpCommandResult<McpActionSendMeta, McpActionSendResult> switched = await CreateCommands(
                new FakePluginApi(PluginId, ["send"]), sendEnabled: false)
            .SendAsync(new McpActionSendRequest(Root, "classify",
                JsonSerializer.SerializeToElement(new { }), Root + "control"));
        switched.Error!.Code.Should().Be((int)McpErrorCode.PermissionDenied);
    }

    [Fact]
    public async Task Domain_denials_still_apply_to_plugin_uris()
    {
        McpServerSettings settings = McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow) with
        {
            DomainPermissions = [new McpDomainPermission("plugin", "fetch", false)]
        };
        McpCommandResult<McpFetchMeta, McpFetchResult> denied = await CreateCommands(
                new FakePluginApi(PluginId, ["fetch"]),
                _ => Task.FromResult(settings))
            .FetchAsync(new McpFetchRequest([Root + "status.json", "patchouli://library.yml"], null, null));
        denied.Error!.Code.Should().Be((int)McpErrorCode.PermissionDenied); // mixed batch denied whole
    }

    [Fact]
    public async Task Plugin_send_validates_endpoint_and_target()
    {
        FakePluginApi plugins = new(PluginId, ["send"]);
        McpCommandService commands = CreateCommands(plugins);

        McpCommandResult<McpActionSendMeta, McpActionSendResult> wrongTarget = await commands.SendAsync(
            new McpActionSendRequest("patchouli://plugin/other-plugin/", "classify",
                JsonSerializer.SerializeToElement(new { }), Root + "control"));
        wrongTarget.Error!.Code.Should().Be((int)McpErrorCode.InvalidArgument);

        McpCommandResult<McpActionSendMeta, McpActionSendResult> notEndpoint = await commands.SendAsync(
            new McpActionSendRequest(Root, "classify",
                JsonSerializer.SerializeToElement(new { }), Root + "status.json"));
        notEndpoint.Error!.Code.Should().Be((int)McpErrorCode.InvalidArgument);
        plugins.Sends.Should().Be(0);

        McpCommandResult<McpActionSendMeta, McpActionSendResult> ok = await commands.SendAsync(
            new McpActionSendRequest(Root, "classify",
                JsonSerializer.SerializeToElement(new { image_base64 = "AA==" }), Root + "control"));
        ok.IsSuccess.Should().BeTrue();
        plugins.Sends.Should().Be(1);
        ok.Envelope!.Entries[0].State.Should().Be("completed");
    }

    [Fact]
    public async Task Plugin_uris_are_read_only_for_put_and_cite()
    {
        McpCommandService commands = CreateCommands(new FakePluginApi(PluginId, ["find", "fetch"]));
        McpCommandResult<McpPutMeta, McpPutResult> put =
            await commands.PutAsync(new McpPutRequest(Root + "status.json", "{}"));
        put.Error!.Code.Should().Be((int)McpErrorCode.PermissionDenied);

        McpCommandResult<McpCiteMeta, McpCitationResult> cite =
            await commands.CiteAsync(new McpCiteRequest([Root + "status.json"], null, null, false, false));
        cite.Envelope!.Entries[0].Error.Should().Contain("NOT_CITABLE");
    }

    /// <summary>Registry-faithful fake: tracks live calls so tests prove static paths stay process-free.</summary>
    private sealed class FakePluginApi(
        string pluginId,
        IReadOnlyList<string> verbs,
        IReadOnlyList<McpPluginViewInfo>? views = null)
        : IMcpPluginApi
    {
        private readonly IReadOnlyList<McpPluginViewInfo> _views = views ?? [];

        public int LiveFinds { get; private set; }
        public int LiveFetches { get; private set; }
        public int Sends { get; private set; }

        public IReadOnlyList<McpPluginMountInfo> Mounts { get; } =
        [
            new(pluginId, "Fake", "0.1.0",
                McpResourceUris.PluginRootUri(pluginId), verbs, "registered")
        ];

        public McpPluginMountInfo? FindMount(string id)
        {
            return Mounts.FirstOrDefault(mount => mount.PluginId == id);
        }

        public bool AllowsVerb(string id, string verb)
        {
            return FindMount(id)?.Verbs.Contains(verb, StringComparer.Ordinal) == true;
        }

        public bool IsEndpoint(string id, string relativePath)
        {
            return relativePath == "control";
        }

        public IReadOnlyList<McpPluginViewInfo> Views(string id)
        {
            return FindMount(id) is null ? [] : _views;
        }

        public McpPluginViewInfo? FindView(string id, string viewId)
        {
            return FindMount(id) is null
                ? null
                : _views.FirstOrDefault(view => view.ViewId == viewId);
        }

        public Result<McpPluginFetchContent?> TryFetchStatic(string id, string relativePath)
        {
            if (relativePath is "views" or "views/")
            {
                return Result<McpPluginFetchContent?>.Failure("invalid_argument",
                    "Directory listings are browsed with find, not fetched.");
            }

            if (relativePath.StartsWith("views/", StringComparison.Ordinal))
            {
                return FindView(id, relativePath["views/".Length..]) is { } view
                    ? Result<McpPluginFetchContent?>.Success(new McpPluginFetchContent("application/json",
                        $$"""{"uri":"{{view.Uri}}","plugin_id":"{{id}}","view_id":"{{view.ViewId}}","title":"{{view.Title}}","placement":"{{view.Placement}}","source_uri":"{{view.SourceUri}}"}"""))
                    : Result<McpPluginFetchContent?>.Failure("not_found", "not a declared view");
            }

            return relativePath switch
            {
                "plugin.yml" or "README.md" or "control" => Result<McpPluginFetchContent?>.Success(
                    new McpPluginFetchContent("application/json", "{}")),
                "" => Result<McpPluginFetchContent?>.Success(
                    new McpPluginFetchContent("application/json", """{"uri":"","entries":[]}""")),
                "status.json" or "results/" => Result<McpPluginFetchContent?>.Success(null),
                _ => Result<McpPluginFetchContent?>.Failure("not_found", "not declared")
            };
        }

        public Task<Result<McpPluginFindPage>> FindAsync(string id, string relativeScope, string? query,
            int limit, string? cursor, CancellationToken cancellationToken = default)
        {
            LiveFinds++;
            return Task.FromResult(Result<McpPluginFindPage>.Success(new McpPluginFindPage(
                [new McpPluginFindEntry(McpResourceUris.PluginRootUri(id) + "status.json", "status", "file")],
                null)));
        }

        public Task<Result<McpPluginFetchContent>> FetchAsync(string id, string relativePath,
            CancellationToken cancellationToken = default)
        {
            LiveFetches++;
            return Task.FromResult(Result<McpPluginFetchContent>.Success(
                new McpPluginFetchContent("application/json", """{"ok":true}""")));
        }

        public Task<Result<McpPluginSendOutcome>> SendAsync(string id, string endpointPath, string action,
            JsonElement args, string? requestId, CancellationToken cancellationToken = default)
        {
            Sends++;
            return Task.FromResult(Result<McpPluginSendOutcome>.Success(
                new McpPluginSendOutcome("completed", new { result_id = "r1", action })));
        }
    }

    /// <summary>Defaulting proxy: returns failed Results for any API call not overridden.</summary>
    private class Stub<T> : DispatchProxy where T : class
    {
        public static T Create()
        {
            return Create<T, Stub<T>>()!;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Type resultType = method!.ReturnType.GenericTypeArguments[0]; // Result<X>
            if (resultType == typeof(Result<McpLibraryStateResponse>))
            {
                return Task.FromResult(Result<McpLibraryStateResponse>.Success(
                    new McpLibraryStateResponse("lib", "lib:1")));
            }

            object failure = resultType
                .GetMethod("Failure", [typeof(string), typeof(string)])!
                .Invoke(null, ["fake", "x"])!;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(resultType).Invoke(null, [failure]);
        }
    }
}
