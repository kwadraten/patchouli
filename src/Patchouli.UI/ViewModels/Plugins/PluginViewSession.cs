using System.Buffers;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.Core.Results;
using Patchouli.Mcp;

namespace Patchouli.UI.ViewModels;

/// <summary>
/// One live plugin web-view session: the declared view, the permission-gated HTML payload
/// fetched through <see cref="IMcpPluginApi.FetchAsync"/>, the load/error surface for the
/// hosting container, and the host-provided page context (<see cref="PageUri"/> — non-null
/// only for pdf-sidebar placements). The session is pure view-model: it never touches
/// <c>NativeWebView</c>, so headless tests exercise it fully.
/// </summary>
public sealed partial class PluginViewSession : ViewModelBase
{
    /// <summary>The registry grant verb required before any view content is fetched.</summary>
    public const string RequiredVerb = "view";

    /// <summary>Hard cap on a single plugin-request message body, in UTF-16 chars.</summary>
    internal const int MaxRequestBodyChars = 12 * 1024 * 1024;

    /// <summary>Cap on concurrently in-flight plugin fetch/send bridge calls.</summary>
    internal const int MaxPendingRequests = 16;

    private int _loadGeneration;
    private CancellationTokenSource? _loadCancellation;
    private IMcpPluginApi? _plugins;
    private CancellationTokenSource? _requestCancellation;
    private readonly SemaphoreSlim _requestSlots = new(MaxPendingRequests, MaxPendingRequests);
    private bool _live;

    public PluginViewSession(string pluginId, McpPluginViewInfo view)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(view);
        PluginId = pluginId;
        View = view;
    }

    /// <summary>
    /// Host-injected managed-files surface for the <c>files</c> bridge operation: resolves the
    /// session plugin's declared managed files and their local presence without any download.
    /// Null means the host offers no managed-file capability; the operation then replies
    /// "unsupported". Set once by the owning <see cref="MainWindowViewModel"/> before load.
    /// </summary>
    internal Func<string, CancellationToken, Task<Result<PluginManagedFilesView>>>? ManagedFileStatusAsync { get; set; }

    /// <summary>
    /// Host-injected <c>files/ensure</c> authority: the callback itself shows the host
    /// confirmation and runs the managed download under modal progress/cancellation, so a
    /// document request can never trigger an unconfirmed download. Null means no ensure
    /// capability; the operation then replies "unsupported".
    /// </summary>
    internal Func<string, CancellationToken, Task<Result<PluginManagedFilesView>>>? ConfirmManagedFilesAsync
    {
        get;
        set;
    }

    public string PluginId { get; }
    public McpPluginViewInfo View { get; }
    [ExcludeFromDerivedGeneration] public string Title => View.Title;

    /// <summary>True while the view HTML fetch is in flight.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>The fetched view HTML; null until the first successful load.</summary>
    [ObservableProperty]
    public partial string? Html { get; private set; }

    /// <summary>User-facing load failure text (permission denied, fetch error); null on success.</summary>
    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    /// <summary>
    /// Canonical URI of the PDF workspace page this view's context should track, or null for
    /// tab/window placements. Written by the owning workspace on page/revision changes.
    /// </summary>
    [ObservableProperty]
    public partial string? PageUri { get; internal set; }

    /// <summary>
    /// Permission gate + content fetch. Rejects when the device registry does not grant the
    /// "view" verb, then fetches <see cref="McpPluginViewInfo.SourcePath"/> from the plugin.
    /// A superseding call or <see cref="Release"/> discards the in-flight result so a stale
    /// load can never repopulate a closed/reused session.
    /// </summary>
    public async Task<bool> LoadAsync(IMcpPluginApi plugins, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        int generation = ++_loadGeneration;
        CancellationTokenSource load = ReplaceLoadCancellation(cancellationToken);
        IsLoading = true;
        ErrorText = null;
        try
        {
            if (!plugins.AllowsVerb(PluginId, RequiredVerb))
            {
                ErrorText = "插件视图权限未授予（view）。";
                return false;
            }

            Result<McpPluginFetchContent> result =
                await plugins.FetchAsync(PluginId, View.SourcePath, load.Token);
            if (generation != _loadGeneration)
            {
                return false;
            }

            if (result.IsFailure)
            {
                ErrorText = $"插件视图加载失败：{result.ErrorCode} {result.ErrorMessage}";
                return false;
            }

            Html = result.Value.Content;
            _plugins = plugins;
            _requestCancellation = new CancellationTokenSource();
            _live = true;
            return true;
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>
    /// Host-side failure surface for errors that occur outside <see cref="LoadAsync"/> —
    /// e.g. the native web view could not be materialized (missing runtime, denied data
    /// folder). Reporting here keeps the failure visible in the hosting container instead
    /// of crashing the process.
    /// </summary>
    internal void ReportError(string message)
    {
        IsLoading = false;
        ErrorText = message;
    }

    /// <summary>
    /// Handles one <c>patchouli:plugin-request</c> message from the view document: a
    /// permission-gated fetch/send call scoped strictly to this session's own plugin.
    /// Returns the serialized reply JSON to dispatch back into the document, or null when
    /// the message is not a plugin request and belongs to another channel.
    /// </summary>
    public async Task<string?> HandleRequestAsync(string body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out JsonElement type) ||
                type.ValueKind != JsonValueKind.String ||
                !string.Equals(type.GetString(), PluginViewContextScript.PluginRequestMessageType,
                    StringComparison.Ordinal))
            {
                return null;
            }

            string? id = ReadRequestId(root);
            if (body.Length > MaxRequestBodyChars)
            {
                return WriteErrorReply(id, "request_too_large",
                    $"Request exceeds the {MaxRequestBodyChars}-character limit.");
            }

            if (id is null)
            {
                return WriteErrorReply(null, "invalid_request",
                    "Request 'id' must be a string of at most 128 characters.");
            }

            if (!TryReadString(root, "operation", out string? operation) ||
                operation is not ("fetch" or "send" or "files"))
            {
                return WriteErrorReply(id, "invalid_request",
                    "Request 'operation' must be \"fetch\", \"send\" or \"files\".");
            }

            // 'path' belongs to the fetch/send contract only; the files operation addresses
            // the plugin's whole managed-file set, so its requests carry no path.
            string? path = null;
            if (operation is "fetch" or "send" &&
                (!TryReadString(root, "path", out path) || !IsValidPluginPath(path)))
            {
                return WriteErrorReply(id, "invalid_path",
                    "Request 'path' must be a declared plugin-relative path.");
            }

            string? action = null;
            if (root.TryGetProperty("action", out JsonElement actionElement))
            {
                if (actionElement.ValueKind != JsonValueKind.String)
                {
                    return WriteErrorReply(id, "invalid_request",
                        "Request 'action' must be a string when present.");
                }

                action = actionElement.GetString();
            }

            JsonElement args = default;
            if (root.TryGetProperty("args", out JsonElement argsElement))
            {
                if (argsElement.ValueKind != JsonValueKind.Object)
                {
                    return WriteErrorReply(id, "invalid_request",
                        "Request 'args' must be an object when present.");
                }

                args = argsElement.Clone();
            }

            string? requestId = null;
            if (root.TryGetProperty("request_id", out JsonElement requestIdElement))
            {
                if (requestIdElement.ValueKind != JsonValueKind.String)
                {
                    return WriteErrorReply(id, "invalid_request",
                        "Request 'request_id' must be a string when present.");
                }

                requestId = requestIdElement.GetString();
            }

            if (operation == "send" && string.IsNullOrEmpty(action))
            {
                return WriteErrorReply(id, "invalid_request",
                    "Request 'action' is required for the send operation.");
            }

            if (operation == "files" && action is not ("status" or "ensure"))
            {
                return WriteErrorReply(id, "invalid_request",
                    "Request 'action' for the files operation must be \"status\" or \"ensure\".");
            }

            // The scoped plugin id is always this session's own; a request can never name
            // another plugin, and every call re-checks the registry grants.
            IMcpPluginApi? plugins = Volatile.Read(ref _plugins);
            if (!_live || plugins is null)
            {
                return WriteErrorReply(id, "session_unavailable",
                    "The plugin view session is not loaded or was released.");
            }

            if (!plugins.AllowsVerb(PluginId, RequiredVerb))
            {
                return WriteErrorReply(id, "permission_denied",
                    "The 'view' grant for this plugin is not allowed.");
            }

            // Fetch/send require the matching registry verb. The files operation is a host
            // capability rather than a declared verb: "view" plus an injected managed-files
            // callback is the gate, and the callback itself rejects plugins that declare no
            // managed files. A dedicated registry verb can be added later without loosening
            // this floor.
            if (operation != "files" && !plugins.AllowsVerb(PluginId, operation!))
            {
                return WriteErrorReply(id, "permission_denied",
                    $"The '{operation}' grant is not allowed for this plugin.");
            }

            if (!_requestSlots.Wait(0))
            {
                return WriteErrorReply(id, "busy",
                    $"More than {MaxPendingRequests} plugin requests are already in flight.");
            }

            try
            {
                CancellationTokenSource? sessionCancellation = Volatile.Read(ref _requestCancellation);
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                    sessionCancellation?.Token ?? CancellationToken.None, cancellationToken);

                if (operation == "files")
                {
                    return await HandleFilesRequestAsync(id, action!, linked.Token)
                        .ConfigureAwait(false);
                }

                if (operation == "fetch")
                {
                    Result<McpPluginFetchContent> fetched = await plugins
                        .FetchAsync(PluginId, path!, linked.Token)
                        .ConfigureAwait(false);
                    if (fetched.IsFailure)
                    {
                        return WriteErrorReply(id, fetched.ErrorCode ?? "error",
                            fetched.ErrorMessage ?? "The fetch failed.");
                    }

                    return WriteOkReply(id, writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteString("content_type", fetched.Value.ContentType);
                        writer.WriteString("content", fetched.Value.Content);
                        writer.WriteEndObject();
                    });
                }

                Result<McpPluginSendOutcome> sent = await plugins
                    .SendAsync(PluginId, path!, action!, args, requestId, linked.Token)
                    .ConfigureAwait(false);
                if (sent.IsFailure)
                {
                    return WriteErrorReply(id, sent.ErrorCode ?? "error",
                        sent.ErrorMessage ?? "The send failed.");
                }

                return WriteOkReply(id, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("state", sent.Value.State);
                    writer.WritePropertyName("result");
                    JsonSerializer.Serialize(writer, sent.Value.Result);
                    writer.WriteEndObject();
                });
            }
            catch (OperationCanceledException)
            {
                return WriteErrorReply(id, "cancelled", "The plugin request was cancelled.");
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return WriteErrorReply(id, "internal_error", exception.Message);
            }
            finally
            {
                _requestSlots.Release();
            }
        }
    }

    /// <summary>Plugin-relative paths only: no schemes, OS paths, backslashes, NUL or
    /// dot/dotdot components; the root "" stays valid.</summary>
    internal static bool IsValidPluginPath(string path)
    {
        if (path.Length == 0)
        {
            return true;
        }

        if (path[0] == '/' || path.IndexOfAny('\\', ':', '\0') >= 0)
        {
            return false;
        }

        string trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return false;
        }

        foreach (string segment in trimmed.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The <c>files</c> bridge operation: <c>status</c> resolves the session plugin's declared
    /// managed files (no download); <c>ensure</c> hands the request to the host-confirming
    /// <see cref="ConfirmManagedFilesAsync"/> callback — the document's request is the user
    /// intent, the callback's own dialog is the host authority, and no code path reaches a
    /// download without it. Replies carry only sanitized file metadata (declared relative
    /// path, state, size, digest, version) — never a local path and never the download URL.
    /// </summary>
    private async Task<string> HandleFilesRequestAsync(string id, string action,
        CancellationToken cancellationToken)
    {
        Func<string, CancellationToken, Task<Result<PluginManagedFilesView>>>? handler =
            action == "status" ? ManagedFileStatusAsync : ConfirmManagedFilesAsync;
        if (handler is null)
        {
            return WriteErrorReply(id, "unsupported",
                "This host does not offer a managed-files capability to plugin views.");
        }

        Result<PluginManagedFilesView> managed = await handler(PluginId, cancellationToken)
            .ConfigureAwait(false);
        if (managed.IsFailure)
        {
            return WriteErrorReply(id, managed.ErrorCode ?? "error",
                managed.ErrorMessage ?? "The managed-files request failed.");
        }

        return WriteOkReply(id, writer => WriteManagedFilesReply(writer, managed.Value));
    }

    private static void WriteManagedFilesReply(Utf8JsonWriter writer, PluginManagedFilesView view)
    {
        writer.WriteStartObject();
        writer.WriteString("state", view.State);
        writer.WritePropertyName("files");
        writer.WriteStartArray();
        foreach (PluginManagedFileView file in view.Files)
        {
            writer.WriteStartObject();
            writer.WriteString("path", file.Path);
            writer.WriteString("state", file.State);
            if (file.ExpectedBytes is { } bytes)
            {
                writer.WriteNumber("expected_bytes", bytes);
            }

            if (file.Sha256 is { } sha256)
            {
                writer.WriteString("sha256", sha256);
            }

            if (file.Version is { } version)
            {
                writer.WriteString("version", version);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static string? ReadRequestId(JsonElement root)
    {
        if (!root.TryGetProperty("id", out JsonElement element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? id = element.GetString();
        return id is { Length: <= 128 } ? id : null;
    }

    private static bool TryReadString(JsonElement root, string name,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
        out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out JsonElement element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return value is not null;
    }

    private static string WriteOkReply(string id, Action<Utf8JsonWriter> writeValue)
    {
        return WriteReply(id, true, writer =>
        {
            writer.WritePropertyName("value");
            writeValue(writer);
        });
    }

    private static string WriteErrorReply(string? id, string code, string message)
    {
        return WriteReply(id, false, writer =>
        {
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteString("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
        });
    }

    private static string WriteReply(string? id, bool ok, Action<Utf8JsonWriter> writeBody)
    {
        ArrayBufferWriter<byte> buffer = new(256);
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            if (id is null)
            {
                writer.WriteNull("id");
            }
            else
            {
                writer.WriteString("id", id);
            }

            writer.WriteBoolean("ok", ok);
            writeBody(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Detaches the session from its container: cancels the in-flight fetch and every
    /// in-flight plugin request, and drops the loaded HTML and page context so a released
    /// view can never be repopulated by a late response.
    /// </summary>
    public void Release()
    {
        _loadGeneration++;
        _live = false;
        Volatile.Write(ref _plugins, null);
        if (_loadCancellation is not null)
        {
            _loadCancellation.Cancel();
            _loadCancellation.Dispose();
            _loadCancellation = null;
        }

        CancellationTokenSource? requestCancellation = Volatile.Read(ref _requestCancellation);
        if (requestCancellation is not null)
        {
            Volatile.Write(ref _requestCancellation, null);
            requestCancellation.Cancel();
            requestCancellation.Dispose();
        }

        IsLoading = false;
        Html = null;
        PageUri = null;
    }

    private CancellationTokenSource ReplaceLoadCancellation(CancellationToken linked)
    {
        if (_loadCancellation is not null)
        {
            _loadCancellation.Cancel();
            _loadCancellation.Dispose();
        }

        _loadCancellation = linked.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(linked)
            : new CancellationTokenSource();
        return _loadCancellation;
    }
}
