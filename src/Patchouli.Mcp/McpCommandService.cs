using System.Xml.Linq;
using Patchouli.Core.Mcp;
using System.Text;
using System.Text.Json;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Documents;
using Patchouli.Core.View;

namespace Patchouli.Mcp;

/// <summary>
/// Shared implementation of the find/fetch/put/cite command surface used by both the MCP
/// tools and the patchouli-cli executable so that parameter names, defaults, validation,
/// permissions, response shapes and error codes stay isomorphic. All responses use the
/// closed v3 { meta, continuation, message?, entries } envelope.
/// </summary>
public sealed partial class McpCommandService
{
    private readonly IMcpReadApi _read;
    private readonly IMcpWriteApi _write;
    private readonly IBiblatexImportService _biblatex;
    private readonly IItemService _items;
    private readonly IVersionedEvidenceReader _evidenceReader;
    private readonly IMcpOcrRunsApi? _ocrRuns;
    private readonly bool _exposeLibraryTags;
    private readonly bool _exposeLibraryCollections;
    private readonly bool _putEnabled;
    private readonly bool _sendEnabled;
    private readonly bool _addEnabled;
    private readonly bool _viewEnabled;
    private readonly IFileImportService? _fileImport;
    private readonly IPageImageViewService? _pageView;
    private readonly IMcpOcrCommandApi? _ocrCommands;
    private readonly IMcpTranslationRunsApi? _translationRuns;
    private readonly IMcpMetadataRunsApi? _metadataRuns;
    private readonly IMcpImplementationCatalogApi? _implementationCatalog;
    private readonly Func<CancellationToken, Task<McpServerSettings>>? _permissionSettings;

    public McpCommandService(IMcpReadApi read, IMcpWriteApi write, IBiblatexImportService biblatex,
        IItemService items, IVersionedEvidenceReader evidenceReader,
        bool exposeLibraryTags = true, bool exposeLibraryCollections = true,
        IMcpOcrRunsApi? ocrRuns = null, bool putEnabled = true, bool sendEnabled = true,
        Func<CancellationToken, Task<McpServerSettings>>? permissionSettings = null,
        IFileImportService? fileImport = null, IPageImageViewService? pageView = null,
        bool addEnabled = true, bool viewEnabled = true,
        IMcpOcrCommandApi? ocrCommands = null, IMcpTranslationRunsApi? translationRuns = null,
        IMcpMetadataRunsApi? metadataRuns = null,
        IMcpImplementationCatalogApi? implementationCatalog = null)
    {
        _read = read;
        _write = write;
        _biblatex = biblatex;
        _items = items;
        _evidenceReader = evidenceReader;
        _ocrRuns = ocrRuns;
        _exposeLibraryTags = exposeLibraryTags;
        _exposeLibraryCollections = exposeLibraryCollections;
        _putEnabled = putEnabled;
        _sendEnabled = sendEnabled;
        _addEnabled = addEnabled;
        _viewEnabled = viewEnabled;
        _fileImport = fileImport;
        _pageView = pageView;
        _ocrCommands = ocrCommands;
        _translationRuns = translationRuns;
        _metadataRuns = metadataRuns;
        _implementationCatalog = implementationCatalog;
        _permissionSettings = permissionSettings;
        _actions = CreateActionRegistry();
    }

    public const int MaxLimit = 50;
    public const int DefaultLimitBytes = 65536;
    public const int MaxLimitBytes = 1048576;

    /// <summary>
    /// Default TOON text encoder for the v3 text output. Encoding is performed by the
    /// Corvus-based <see cref="McpToonCodec"/> under the fixed UTF-8/LF, literal TAB,
    /// KeyFolding=Off profile; the codec is the single production encoder.
    /// </summary>
    public static Func<object, string> DefaultToonEncoder { get; } = McpToonCodec.Encode;

    /// <summary>
    /// Default JSON text encoder for the equivalent <c>format=json</c> projection. Both
    /// projections share the same strict JSON data model before it is encoded to text.
    /// </summary>
    public static Func<object, string> DefaultJsonEncoder { get; } = static value =>
        JsonSerializer.Serialize(value);

    /// <summary>
    /// Renders an envelope as text. TOON is the default; an explicit <c>"json"</c> selects
    /// the equivalent JSON projection. Any other or null format resolves to TOON.
    /// </summary>
    public static string RenderText(object envelope, string? format)
    {
        return string.Equals(format, "json", StringComparison.Ordinal)
            ? DefaultJsonEncoder(envelope)
            : DefaultToonEncoder(envelope);
    }

    public async Task<McpCommandResult<McpFindMeta, object>> FindAsync(McpFindRequest request,
        CancellationToken cancellationToken = default)
    {
        string? original = request.In;
        McpFindRequest normalized = request with { In = original is null ? null : McpResourceUris.Normalize(original) };
        return WithLegacyWarnings(await FindCoreAsync(normalized, cancellationToken), [original],
            new McpFindMeta("lib:0", 0, 0, 0));
    }

    public async Task<McpCommandResult<McpFetchMeta, McpFetchResult>> FetchAsync(McpFetchRequest request,
        CancellationToken cancellationToken = default)
    {
        return WithLegacyWarnings(await FetchCoreAsync(request, cancellationToken), request.Uris ?? [],
            new McpFetchMeta("lib:0"));
    }

    public async Task<McpCommandResult<McpPutMeta, McpPutResult>> PutAsync(McpPutRequest request,
        CancellationToken cancellationToken = default)
    {
        return WithLegacyWarnings(await PutCoreAsync(request, cancellationToken), [request.Uri],
            new McpPutMeta("lib:0"));
    }

    public async Task<McpCommandResult<McpCiteMeta, McpCitationResult>> CiteAsync(McpCiteRequest request,
        CancellationToken cancellationToken = default)
    {
        return WithLegacyWarnings(await CiteCoreAsync(request, cancellationToken), request.Refs ?? [],
            new McpCiteMeta("lib:0", null, null, "text", null));
    }

    /// <summary>
    /// Issue #11: imports one host-local file into the library. The path is the explicitly
    /// allowed local-path input exception; the response never exposes host paths.
    /// </summary>
    public Task<McpCommandResult<McpAddMeta, McpAddResult>> AddAsync(McpAddRequest request,
        CancellationToken cancellationToken = default)
    {
        return AddCoreAsync(request, cancellationToken);
    }

    /// <summary>
    /// Issue #11: reads one source image for model text-image collation. The returned
    /// <see cref="McpViewCommandResult"/> carries the PNG bytes alongside the text
    /// metadata envelope so the transport can emit the native MCP image content block.
    /// </summary>
    public Task<McpViewCommandResult> ViewAsync(McpViewRequest request,
        CancellationToken cancellationToken = default)
    {
        return ViewCoreAsync(request, cancellationToken);
    }

    private async Task<McpCommandResult<McpAddMeta, McpAddResult>> AddCoreAsync(McpAddRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return McpCommandResult<McpAddMeta, McpAddResult>.Fail(McpErrorCode.InvalidArgument,
                "path is required.");
        }

        if (!_addEnabled)
        {
            return McpCommandResult<McpAddMeta, McpAddResult>.Fail(McpErrorCode.PermissionDenied,
                "add is disabled by the host's MCP tool switches.");
        }

        if (_fileImport is null)
        {
            return McpCommandResult<McpAddMeta, McpAddResult>.Fail(McpErrorCode.Unavailable,
                "The host does not expose local file import.");
        }

        McpServerSettings? permissions = await PermissionSettingsAsync(cancellationToken);
        string revision = await CurrentLibraryRevisionAsync(cancellationToken);

        Result<FileImportResult> imported = await _fileImport.ImportAsync(
            new FileImportRequest(request.Path, request.Kind, FileImportTrigger.McpAdd, kind =>
                Task.FromResult(AuthorizeAddKind(permissions, kind))),
            cancellationToken);
        if (imported.IsFailure)
        {
            McpErrorCode code = MapImportError(imported.ErrorCode);
            McpEnvelope<McpAddMeta, McpAddResult> failed =
                McpEnvelope<McpAddMeta, McpAddResult>.Create(new McpAddMeta(revision), []);
            McpMessage message = new(McpToolError.From(code, imported.ErrorMessage ?? "Import failed.")
                .ToTerminalLine(), []);
            failed = failed with { Message = message };
            return McpCommandResult<McpAddMeta, McpAddResult>.Partial(failed, code,
                imported.ErrorMessage ?? "Import failed.");
        }

        FileImportResult result = imported.Value;
        McpAddResult entry = new(
            result.Kind.ToString().ToLowerInvariant(),
            result.Reused,
            result.Moved,
            result.ItemUri,
            result.TextUri,
            result.StyleUri,
            result.PageCount,
            result.ImportStatus,
            result.BodyStatus,
            result.Issues.Select(issue => new McpAddIssue(issue.Code, issue.Message)).ToArray());
        McpEnvelope<McpAddMeta, McpAddResult> envelope =
            McpEnvelope<McpAddMeta, McpAddResult>.Create(new McpAddMeta(revision), [entry]);
        return McpCommandResult<McpAddMeta, McpAddResult>.Ok(envelope);
    }

    private static Result AuthorizeAddKind(McpServerSettings? permissions, LocalImportKind kind)
    {
        // PDF imports need items.add + texts.add; CSL installs need csl-styles.add.
        // Existing put grants are never auto-expanded to add.
        bool allowed = kind switch
        {
            LocalImportKind.Pdf => Allowed(permissions, "add", "patchouli://items/") &&
                                   Allowed(permissions, "add", "patchouli://texts/"),
            LocalImportKind.Csl => Allowed(permissions, "add", "patchouli://csl-styles/"),
            _ => false
        };
        return allowed
            ? Result.Success()
            : Result.Failure(AppErrorCodes.UnsupportedOperation,
                "add is disabled for this domain by the host's MCP permissions.");
    }

    private static McpErrorCode MapImportError(string? errorCode)
    {
        return errorCode switch
        {
            AppErrorCodes.InvalidArgument => McpErrorCode.InvalidArgument,
            AppErrorCodes.NotFound => McpErrorCode.NotFound,
            AppErrorCodes.UnsupportedOperation => McpErrorCode.PermissionDenied,
            FileImportErrorCodes.UrlNotAllowed => McpErrorCode.InvalidArgument,
            FileImportErrorCodes.KindMismatch => McpErrorCode.InvalidArgument,
            FileImportErrorCodes.UnsupportedFormat => McpErrorCode.InvalidArgument,
            FileImportErrorCodes.NotRegularFile => McpErrorCode.InvalidArgument,
            FileImportErrorCodes.PathNotFound => McpErrorCode.NotFound,
            FileImportErrorCodes.UnreadableFile => McpErrorCode.InvalidArgument,
            FileImportErrorCodes.CloudPlaceholderUnavailable => McpErrorCode.Unavailable,
            FileImportErrorCodes.NoSearchRoot => McpErrorCode.InvalidArgument,
            FileImportErrorCodes.SearchRootUnavailable => McpErrorCode.Unavailable,
            FileImportErrorCodes.MoveFailed => McpErrorCode.Internal,
            FileImportErrorCodes.RecoveryFailed => McpErrorCode.Internal,
            FileImportErrorCodes.StyleConflict => McpErrorCode.RevisionConflict,
            FileImportErrorCodes.StyleInvalid => McpErrorCode.InvalidContent,
            _ => McpErrorCode.Internal
        };
    }

    private async Task<McpViewCommandResult> ViewCoreAsync(McpViewRequest request,
        CancellationToken cancellationToken)
    {
        McpViewCommandResult Fail(McpErrorCode code, string detail)
        {
            return new McpViewCommandResult(
                McpCommandResult<McpViewMeta, McpViewImageMetadata>.Fail(code, detail), null);
        }

        if (string.IsNullOrWhiteSpace(request.Uri))
        {
            return Fail(McpErrorCode.InvalidArgument, "uri is required.");
        }

        if (!_viewEnabled)
        {
            return Fail(McpErrorCode.PermissionDenied, "view is disabled by the host's MCP tool switches.");
        }

        if (_pageView is null)
        {
            return Fail(McpErrorCode.Unavailable, "The host does not expose source image reading.");
        }

        Result<McpUriParseResult> parsed = McpResourceUris.Parse(request.Uri);
        if (parsed.IsFailure)
        {
            return Fail(McpErrorCode.InvalidArgument, parsed.ErrorMessage ?? "Invalid URI.");
        }

        McpUriParseResult target = parsed.Value;
        bool isTranslation = target.Kind == McpUriKind.TranslationPage;
        if (target.Kind is McpUriKind.Document or McpUriKind.TranslationDocument)
        {
            return Fail(McpErrorCode.InvalidArgument,
                "view requires a page URI; a document catalog needs a page specification.");
        }

        if (target.Kind is not (McpUriKind.Page or McpUriKind.Evidence or McpUriKind.TranslationPage) ||
            target.DocumentId is null || target.PageIndex is null)
        {
            return Fail(McpErrorCode.InvalidArgument,
                "view supports page, box/evidence and translation page/box URIs.");
        }

        McpServerSettings? permissions = await PermissionSettingsAsync(cancellationToken);
        if (!Allowed(permissions, "view", target.CanonicalUri))
        {
            return Fail(McpErrorCode.PermissionDenied, "view is disabled for this domain.");
        }

        if (isTranslation &&
            !Allowed(permissions, "view", McpResourceUris.DocumentUri(target.DocumentId.Value)))
        {
            // Translation-source images also check the source text domain.
            return Fail(McpErrorCode.PermissionDenied, "view is disabled for the source text domain.");
        }

        PageImageViewContext context = request.Context switch
        {
            null or "page" => PageImageViewContext.Page,
            "region" => PageImageViewContext.Region,
            _ => (PageImageViewContext)(-1)
        };
        if ((int)context < 0)
        {
            return Fail(McpErrorCode.InvalidArgument, "context must be \"page\" or \"region\".");
        }

        string revision = await CurrentLibraryRevisionAsync(cancellationToken);
        Result<PageImageViewResult> viewed = await _pageView.ViewAsync(
            new PageImageViewRequest(
                target.DocumentId.Value,
                target.PageIndex.Value,
                target.TreeRevisionId,
                target.BoxId,
                isTranslation,
                context,
                request.MaxEdge ?? PageImageViewRequest.DefaultMaxEdge,
                request.Boxes),
            cancellationToken);
        if (viewed.IsFailure)
        {
            return new McpViewCommandResult(
                McpCommandResult<McpViewMeta, McpViewImageMetadata>.Fail(
                    MapViewError(viewed.ErrorCode), viewed.ErrorMessage ?? "View failed."),
                null);
        }

        PageImageViewResult image = viewed.Value;
        McpViewImageMetadata metadata = new(
            image.CanonicalUri,
            image.ViewType,
            image.MimeType,
            image.Width,
            image.Height,
            [image.CropRange.X, image.CropRange.Y, image.CropRange.Width, image.CropRange.Height],
            image.ImageIndex,
            image.BoxNumbers?.Select(box => new McpViewBoxNumber(box.Index, box.BoxId.ToString()))
                .ToArray());
        McpEnvelope<McpViewMeta, McpViewImageMetadata> envelope =
            McpEnvelope<McpViewMeta, McpViewImageMetadata>.Create(new McpViewMeta(revision), [metadata]);
        return new McpViewCommandResult(
            McpCommandResult<McpViewMeta, McpViewImageMetadata>.Ok(envelope), image.PngBytes);
    }

    private static McpErrorCode MapViewError(string? errorCode)
    {
        return errorCode switch
        {
            AppErrorCodes.InvalidArgument => McpErrorCode.InvalidArgument,
            AppErrorCodes.NotFound => McpErrorCode.NotFound,
            PageImageViewErrorCodes.TargetNotFound => McpErrorCode.NotFound,
            PageImageViewErrorCodes.StaleRevision => McpErrorCode.NotFound,
            PageImageViewErrorCodes.StaleCoordinates => McpErrorCode.InvalidArgument,
            PageImageViewErrorCodes.SourceMissing => McpErrorCode.Unavailable,
            PageImageViewErrorCodes.SourceChanged => McpErrorCode.RevisionConflict,
            PageImageViewErrorCodes.RenderFailed => McpErrorCode.Internal,
            PageImageViewErrorCodes.ImageTooLarge => McpErrorCode.InvalidArgument,
            PageImageViewErrorCodes.PageSpecificationRequired => McpErrorCode.InvalidArgument,
            _ => McpErrorCode.Internal
        };
    }

    private async Task<string> CurrentLibraryRevisionAsync(CancellationToken cancellationToken)
    {
        try
        {
            Result<McpLibraryStateResponse> state = await _read.GetCurrentLibraryStateAsync(cancellationToken);
            return state.IsSuccess && !string.IsNullOrWhiteSpace(state.Value.LibraryRevision)
                ? state.Value.LibraryRevision
                : "lib:0";
        }
        catch
        {
            return "lib:0";
        }
    }

    private static McpCommandResult<TMeta, TEntry> WithLegacyWarnings<TMeta, TEntry>(
        McpCommandResult<TMeta, TEntry> result, IEnumerable<string?> addresses, TMeta fallbackMeta)
    {
        string[] warnings = addresses.Where(value => value is not null)
            .Select(value => McpResourceUris.ObsoleteWarning(value!)).OfType<string>().Distinct(StringComparer.Ordinal)
            .ToArray();
        if (warnings.Length == 0)
        {
            return result;
        }

        McpEnvelope<TMeta, TEntry> envelope = result.Envelope ?? McpEnvelope<TMeta, TEntry>.Create(fallbackMeta, []);
        return result with
        {
            Envelope = envelope with
            {
                Message = new McpMessage(
                    envelope.Message?.Error ?? result.Error?.ToTerminalLine(),
                    (envelope.Message?.Warnings ?? []).Concat(warnings).Distinct(StringComparer.Ordinal).ToArray())
            }
        };
    }

    private async Task<McpCommandResult<McpFindMeta, object>> FindCoreAsync(McpFindRequest request,
        CancellationToken cancellationToken = default)
    {
        Result<McpLibraryStateResponse> state = await _read.GetCurrentLibraryStateAsync(cancellationToken);
        if (state.IsFailure)
        {
            return McpCommandResult<McpFindMeta, object>.Fail(
                McpErrorMappings.ToReadError(state.ErrorCode),
                state.ErrorMessage ?? "Library state is unavailable.");
        }

        List<string> warnings = [];

        string? query = request.Query;
        if (query is not null && query.Length > 0 && string.IsNullOrWhiteSpace(query))
        {
            AddWarning(warnings, McpWarningCodes.WhitespaceQueryTreatedAsBrowse);
            query = null;
        }

        IReadOnlyList<McpWhereClause>? where = NormalizeWhere(request.Where, warnings);

        McpUriParseResult? scope = null;
        string? scopeUri = null;
        if (request.In is not null)
        {
            Result<McpUriParseResult> parsedScope = McpResourceUris.Parse(request.In);
            if (parsedScope.IsFailure)
            {
                return McpCommandResult<McpFindMeta, object>.Fail(McpErrorCode.InvalidArgument,
                    parsedScope.ErrorMessage ?? "Invalid in scope.");
            }

            scope = parsedScope.Value;
            scopeUri = NormalizeIn(request.In);
        }

        bool literal = request.Literal;
        McpCursor? cursor = null;
        if (request.Cursor is not null)
        {
            McpCursor? decoded = McpCursor.TryDecode(request.Cursor);
            if (decoded is null)
            {
                return McpCommandResult<McpFindMeta, object>.Fail(McpErrorCode.InvalidArgument,
                    "Invalid cursor.");
            }

            if (CursorConflicts(scopeUri, query, literal, where, decoded))
            {
                AddWarning(warnings, McpWarningCodes.CursorContextRestored);
            }

            if (decoded.Scope is null)
            {
                scope = null;
                scopeUri = null;
            }
            else
            {
                Result<McpUriParseResult> parsedScope = McpResourceUris.Parse(decoded.Scope);
                if (parsedScope.IsFailure)
                {
                    return McpCommandResult<McpFindMeta, object>.Fail(McpErrorCode.InvalidArgument,
                        "Invalid cursor scope.");
                }

                scope = parsedScope.Value;
                scopeUri = McpResourceUris.Normalize(decoded.Scope);
            }

            query = decoded.Query;
            literal = decoded.Literal;
            where = decoded.Where;
            cursor = decoded;
        }

        McpServerSettings? permissions = await PermissionSettingsAsync(cancellationToken);
        if (!Allowed(permissions, "find", scopeUri))
        {
            return McpCommandResult<McpFindMeta, object>.Fail(McpErrorCode.PermissionDenied,
                "find is disabled for this domain.");
        }

        McpUriKind scopeKind = scope?.Kind ?? McpUriKind.Root;
        if (scopeKind == McpUriKind.Root)
        {
            scope = null;
        }

        string? matrixError = ValidateScopeMatrix(scopeKind, query, where);
        if (matrixError is not null)
        {
            return McpCommandResult<McpFindMeta, object>.Fail(McpErrorCode.InvalidArgument, matrixError);
        }

        string? exposureError = ValidateExposure(where);
        if (exposureError is not null)
        {
            return McpCommandResult<McpFindMeta, object>.Fail(McpErrorCode.PermissionDenied, exposureError);
        }

        int limit = Math.Clamp(request.Limit, 1, MaxLimit);
        bool longMode = request.Long;

        FindPage page;
        if (scopeKind == McpUriKind.Root)
        {
            page = BrowseRoot(limit, cursor?.Offset ?? 0, permissions);
        }
        else if (IsFileScope(scopeKind))
        {
            page = await BrowseFileSingletonAsync(scope!, query, longMode, where, warnings, cancellationToken);
        }
        else if (string.IsNullOrWhiteSpace(query))
        {
            page = await BrowseScopeAsync(scope!, scopeKind, scopeUri!, limit, cursor?.Offset ?? 0, where, longMode,
                warnings, cancellationToken);
        }
        else
        {
            page = await SearchScopeAsync(scope!, scopeKind, scopeUri!, query, literal, limit, cursor, where, longMode,
                warnings, cancellationToken);
        }

        if (page.HasError)
        {
            return McpCommandResult<McpFindMeta, object>.Fail((McpErrorCode)page.Error!.Code,
                page.Error.Detail ?? page.Error.Name);
        }

        if (request.Cursor is not null || page.Continuation is not null)
        {
            AddWarning(warnings, McpWarningCodes.ResultSetMayHaveChanged);
        }

        McpFindMeta meta = new(state.Value.LibraryRevision, page.DomainTotal, page.FilteredTotal, page.Entries.Count);
        McpMessage? message = warnings.Count == 0 ? null : new McpMessage(null, warnings);
        McpEnvelope<McpFindMeta, object> envelope =
            McpEnvelope<McpFindMeta, object>.Create(meta, page.Entries, page.Continuation, message);
        return McpCommandResult<McpFindMeta, object>.Ok(envelope);
    }

    private async Task<McpCommandResult<McpFetchMeta, McpFetchResult>> FetchCoreAsync(McpFetchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Uris is null || request.Uris.Count == 0)
        {
            return McpCommandResult<McpFetchMeta, McpFetchResult>.Fail(McpErrorCode.InvalidArgument,
                "fetch requires at least one URI.");
        }

        int limitBytes = request.LimitBytes ?? DefaultLimitBytes;
        if (limitBytes <= 0)
        {
            return McpCommandResult<McpFetchMeta, McpFetchResult>.Fail(McpErrorCode.InvalidArgument,
                "limit_bytes must be positive.");
        }

        limitBytes = Math.Min(limitBytes, MaxLimitBytes);

        Result<McpLibraryStateResponse> state = await _read.GetCurrentLibraryStateAsync(cancellationToken);
        if (state.IsFailure)
        {
            return McpCommandResult<McpFetchMeta, McpFetchResult>.Fail(
                McpErrorMappings.ToReadError(state.ErrorCode),
                state.ErrorMessage ?? "Library state is unavailable.");
        }

        List<McpFetchResult> entries = [];
        McpServerSettings? permissions = await PermissionSettingsAsync(cancellationToken);
        if (request.Uris.Any(uri => !Allowed(permissions, "fetch", uri)))
        {
            return McpCommandResult<McpFetchMeta, McpFetchResult>.Fail(McpErrorCode.PermissionDenied,
                "fetch is disabled for a requested domain.");
        }

        foreach (string uri in request.Uris)
        {
            entries.Add(await FetchSingleAsync(uri, request.Range, limitBytes, state.Value, cancellationToken));
        }

        List<string> warnings = [];
        if (request.LimitBytes is > MaxLimitBytes)
        {
            warnings.Add(
                $"LIMIT_BYTES_CLAMPED: limit_bytes was clamped to the server hard maximum of {MaxLimitBytes}.");
        }

        string? topError = null;
        if (entries.Any(entry => entry.Truncated))
        {
            topError = McpToolError.From(McpErrorCode.ResponseTruncated,
                    "At least one response exceeds limit_bytes; partial content is available and must not be treated as complete.")
                .ToTerminalLine();
        }
        else if (entries.Count > 0 && entries.All(entry => entry.Error is not null))
        {
            topError = entries[0].Error;
        }

        McpMessage? message = warnings.Count == 0 && topError is null
            ? null
            : new McpMessage(topError, warnings);
        McpEnvelope<McpFetchMeta, McpFetchResult> envelope =
            McpEnvelope<McpFetchMeta, McpFetchResult>.Create(
                new McpFetchMeta(state.Value.LibraryRevision), entries, null, message);
        return topError is null
            ? McpCommandResult<McpFetchMeta, McpFetchResult>.Ok(envelope)
            : McpCommandResult<McpFetchMeta, McpFetchResult>.Partial(envelope,
                McpToolError.TryGetCode(topError, out McpErrorCode topErrorCode) ? topErrorCode : McpErrorCode.Internal,
                topError);
    }

    private async Task<McpCommandResult<McpPutMeta, McpPutResult>> PutCoreAsync(McpPutRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Uri))
        {
            return McpCommandResult<McpPutMeta, McpPutResult>.Fail(McpErrorCode.InvalidArgument,
                "uri is required.");
        }

        if (!_putEnabled || !Allowed(await PermissionSettingsAsync(cancellationToken), "put", request.Uri))
        {
            return McpCommandResult<McpPutMeta, McpPutResult>.Fail(McpErrorCode.PermissionDenied,
                "put is disabled by the host's MCP tool switches.");
        }

        if (request.Content is null == request.Boxes is null || request.Boxes is { Count: 0 or > 100 })
        {
            return McpCommandResult<McpPutMeta, McpPutResult>.Fail(McpErrorCode.InvalidArgument,
                "Supply either content or between 1 and 100 boxes.");
        }

        Result<McpUriParseResult> parsed = McpResourceUris.Parse(request.Uri);
        if (parsed.IsFailure)
        {
            return McpCommandResult<McpPutMeta, McpPutResult>.Fail(McpErrorCode.InvalidArgument,
                parsed.ErrorMessage ?? "Invalid URI.");
        }

        McpUriKind kind = parsed.Value.Kind;
        if (parsed.Value.TreeRevisionId is not null || parsed.Value.BoxId is not null)
        {
            return McpCommandResult<McpPutMeta, McpPutResult>.Fail(McpErrorCode.PermissionDenied,
                "Only current page resources can be written; remove revision and box selectors.");
        }

        if (!parsed.Value.IsLegacy && kind is McpUriKind.Page or McpUriKind.TranslationPage)
        {
            return await PutTranslationAsync(request, parsed.Value, cancellationToken);
        }

        if (kind == McpUriKind.DocumentMap)
        {
            return await PutMapAsync(request, parsed.Value, cancellationToken);
        }

        if (request.Boxes is not null)
        {
            return McpCommandResult<McpPutMeta, McpPutResult>.Fail(McpErrorCode.InvalidArgument,
                "Box replacements require a current XML page URI.");
        }

        if (kind is McpUriKind.Document or McpUriKind.Page or McpUriKind.Evidence or McpUriKind.Library
            or McpUriKind.TranslationsScope or McpUriKind.TranslationDocument
            or McpUriKind.RunsScope or McpUriKind.RunsOcrScope or McpUriKind.RunOcrStatus
            or McpUriKind.RunsTranslationScope or McpUriKind.RunTranslationStatus
            or McpUriKind.RunsMetadataScope or McpUriKind.RunMetadataStatus
            or McpUriKind.RunEndpoint)
        {
            return McpCommandResult<McpPutMeta, McpPutResult>.Fail(McpErrorCode.PermissionDenied,
                $"'{request.Uri}' is read-only; only items/*.yml, csl-styles/*.csl, " +
                "texts/{document-id}/map.yml, and translations/{document-id}/page-{page-index}.xml " +
                "can be replaced.");
        }

        if (kind == McpUriKind.TranslationPage)
        {
            return await PutTranslationAsync(request, parsed.Value, cancellationToken);
        }

        if (kind is not (McpUriKind.Item or McpUriKind.Style))
        {
            return McpCommandResult<McpPutMeta, McpPutResult>.Fail(McpErrorCode.InvalidArgument,
                "Only item, csl-style, and translation page resources can be put.");
        }

        Result<McpLibraryStateResponse> beforeWrite = await _read.GetCurrentLibraryStateAsync(cancellationToken);
        string baseRevision = beforeWrite.IsSuccess ? beforeWrite.Value.LibraryRevision : "lib:0";

        McpPutRequest effectiveRequest = _exposeLibraryTags
            ? request
            : request with { PreserveTags = true };
        Result<McpPutResponse> result = await _write.PutAsync(effectiveRequest, cancellationToken);
        if (result.IsFailure)
        {
            McpErrorCode code = McpErrorMappings.ToWriteError(result.ErrorCode);
            string detail = result.ErrorMessage ?? result.ErrorCode ?? "Put failed.";
            McpEnvelope<McpPutMeta, McpPutResult> failed =
                McpEnvelope<McpPutMeta, McpPutResult>.Create(new McpPutMeta(baseRevision), []);
            McpMessage message = new(McpToolError.From(code, detail).ToTerminalLine(), []);
            failed = failed with { Message = message };
            return McpCommandResult<McpPutMeta, McpPutResult>.Partial(failed, code, detail);
        }

        Result<McpLibraryStateResponse> afterWrite = await _read.GetCurrentLibraryStateAsync(cancellationToken);
        string newRevision = afterWrite.IsSuccess ? afterWrite.Value.LibraryRevision : baseRevision;
        McpPutResult putResult = new(parsed.Value.CanonicalUri, result.Value.ResourceType, result.Value.Committed,
            result.Value.ContentBytes, SourceRevision: result.Value.SourceRevision,
            TranslationVersion: result.Value.TranslationVersion, ModifiedBoxIds: result.Value.ModifiedBoxIds);
        IReadOnlyList<string> warnings = result.Value.Warnings ?? [];
        McpEnvelope<McpPutMeta, McpPutResult> envelope =
            McpEnvelope<McpPutMeta, McpPutResult>.Create(new McpPutMeta(newRevision), [putResult],
                message: warnings.Count == 0 ? null : new McpMessage(null, warnings));
        return McpCommandResult<McpPutMeta, McpPutResult>.Ok(envelope);
    }

    private async Task<McpCommandResult<McpPutMeta, McpPutResult>> PutTranslationAsync(McpPutRequest request,
        McpUriParseResult target, CancellationToken cancellationToken)
    {
        DocumentInstanceId documentId = target.DocumentId!.Value;
        int pageIndex = target.PageIndex!.Value;
        Result<McpDocumentOutlineResponse> outline = await _read.GetDocumentOutlineAsync(documentId, cancellationToken);
        if (outline.IsFailure)
        {
            return McpCommandResult<McpPutMeta, McpPutResult>.Fail(
                McpErrorMappings.ToReadError(outline.ErrorCode),
                outline.ErrorMessage ?? outline.ErrorCode ?? "Document was not found.");
        }

        McpDocumentPageRef? pageRef = outline.Value.Pages.FirstOrDefault(page => page.PageIndex + 1 == pageIndex);
        if (pageRef is null)
        {
            return McpCommandResult<McpPutMeta, McpPutResult>.Fail(McpErrorCode.NotFound,
                $"Page '{pageIndex}' does not exist in document '{documentId}'.");
        }

        Result<McpLibraryStateResponse> beforeWrite = await _read.GetCurrentLibraryStateAsync(cancellationToken);
        string baseRevision = beforeWrite.IsSuccess ? beforeWrite.Value.LibraryRevision : "lib:0";
        Result<McpPutResponse> result = target.IsLegacy
            ? await _write.PutPageTranslationAsync(target.CanonicalUri, documentId, pageRef.PageId,
                request.Content ?? string.Empty, cancellationToken)
            : await _write.PutPageXmlAsync(request, documentId, pageRef.PageId, pageIndex,
                target.Kind == McpUriKind.TranslationPage, cancellationToken);
        if (result.IsFailure)
        {
            McpErrorCode code = McpErrorMappings.ToWriteError(result.ErrorCode);
            string detail = result.ErrorMessage ?? result.ErrorCode ?? "Put failed.";
            McpPutResult rejected = new(
                target.CanonicalUri,
                target.Kind == McpUriKind.TranslationPage ? "translation_page" : "text_page",
                false,
                request.Boxes?.Sum(box => Encoding.UTF8.GetByteCount(box.Content)) ??
                Encoding.UTF8.GetByteCount(request.Content ?? string.Empty),
                ExtractTranslationErrors(result.Details), BoxErrors: result.Details is BoxContentFailureDetails failures
                    ? failures.Errors.Select(error => new McpBoxPutError(error.BoxId, error.Message)).ToArray()
                    : null);
            McpEnvelope<McpPutMeta, McpPutResult> failed = McpEnvelope<McpPutMeta, McpPutResult>.Create(
                new McpPutMeta(baseRevision), [rejected],
                message: new McpMessage(McpToolError.From(code, detail).ToTerminalLine(), []));
            return McpCommandResult<McpPutMeta, McpPutResult>.Partial(failed, code, detail);
        }

        Result<McpLibraryStateResponse> afterWrite = await _read.GetCurrentLibraryStateAsync(cancellationToken);
        string newRevision = afterWrite.IsSuccess ? afterWrite.Value.LibraryRevision : baseRevision;
        McpPutResult putResult = new(target.CanonicalUri, result.Value.ResourceType, result.Value.Committed,
            result.Value.ContentBytes, SourceRevision: result.Value.SourceRevision,
            TranslationVersion: result.Value.TranslationVersion,
            ModifiedBoxIds: result.Value.ModifiedBoxIds);
        IReadOnlyList<string> warnings = result.Value.Warnings ?? [];
        McpEnvelope<McpPutMeta, McpPutResult> envelope = McpEnvelope<McpPutMeta, McpPutResult>.Create(
            new McpPutMeta(newRevision), [putResult],
            message: warnings.Count == 0 ? null : new McpMessage(null, warnings));
        return McpCommandResult<McpPutMeta, McpPutResult>.Ok(envelope);
    }

    private static IReadOnlyList<McpTranslationStructureError>? ExtractTranslationErrors(
        IResultFailureDetails? details)
    {
        return details is TranslationStructureFailureDetails failure
            ? failure.Errors.Select(error => new McpTranslationStructureError(
                error.BlockIndex, error.Expected, error.Actual)).ToArray()
            : null;
    }

    /// <summary>
    /// Replaces a document's chapter/pagination map from whole-resource <c>map.yml</c> content.
    /// The texts-domain permission and the put switch are enforced by <see cref="PutCoreAsync"/>
    /// before dispatch; box replacements are not supported for maps.
    /// </summary>
    private async Task<McpCommandResult<McpPutMeta, McpPutResult>> PutMapAsync(McpPutRequest request,
        McpUriParseResult target, CancellationToken cancellationToken)
    {
        if (request.Boxes is not null)
        {
            return McpCommandResult<McpPutMeta, McpPutResult>.Fail(McpErrorCode.InvalidArgument,
                "map.yml takes whole-resource content only; box replacements are not supported.");
        }

        if (request.Content is null)
        {
            return McpCommandResult<McpPutMeta, McpPutResult>.Fail(McpErrorCode.InvalidArgument,
                "content is required.");
        }

        Result<McpDocumentOutlineResponse> outline = await _read.GetDocumentOutlineAsync(
            target.DocumentId!.Value, cancellationToken);
        if (outline.IsFailure)
        {
            return McpCommandResult<McpPutMeta, McpPutResult>.Fail(
                McpErrorMappings.ToReadError(outline.ErrorCode),
                outline.ErrorMessage ?? outline.ErrorCode ?? "Document was not found.");
        }

        Result<McpLibraryStateResponse> beforeWrite = await _read.GetCurrentLibraryStateAsync(cancellationToken);
        string baseRevision = beforeWrite.IsSuccess ? beforeWrite.Value.LibraryRevision : "lib:0";

        Result<McpPutResponse> result = await _write.PutDocumentMapAsync(
            target.CanonicalUri, target.DocumentId.Value, request.Content, cancellationToken);
        if (result.IsFailure)
        {
            McpErrorCode code = McpErrorMappings.ToWriteError(result.ErrorCode);
            string detail = result.ErrorMessage ?? result.ErrorCode ?? "Put failed.";
            McpPutResult rejected = new(
                target.CanonicalUri,
                "document_page_map",
                false,
                Encoding.UTF8.GetByteCount(request.Content));
            McpEnvelope<McpPutMeta, McpPutResult> failed = McpEnvelope<McpPutMeta, McpPutResult>.Create(
                new McpPutMeta(baseRevision), [rejected],
                message: new McpMessage(McpToolError.From(code, detail).ToTerminalLine(), []));
            return McpCommandResult<McpPutMeta, McpPutResult>.Partial(failed, code, detail);
        }

        Result<McpLibraryStateResponse> afterWrite = await _read.GetCurrentLibraryStateAsync(cancellationToken);
        string newRevision = afterWrite.IsSuccess ? afterWrite.Value.LibraryRevision : baseRevision;
        McpPutResult putResult = new(target.CanonicalUri, result.Value.ResourceType, result.Value.Committed,
            result.Value.ContentBytes);
        IReadOnlyList<string> warnings = result.Value.Warnings ?? [];
        McpEnvelope<McpPutMeta, McpPutResult> envelope = McpEnvelope<McpPutMeta, McpPutResult>.Create(
            new McpPutMeta(newRevision), [putResult],
            message: warnings.Count == 0 ? null : new McpMessage(null, warnings));
        return McpCommandResult<McpPutMeta, McpPutResult>.Ok(envelope);
    }

    private async Task<McpCommandResult<McpCiteMeta, McpCitationResult>> CiteCoreAsync(McpCiteRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Refs is null || request.Refs.Count == 0)
        {
            return McpCommandResult<McpCiteMeta, McpCitationResult>.Fail(McpErrorCode.InvalidArgument,
                "At least one reference is required.");
        }

        Result<McpLibraryStateResponse> state = await _read.GetCurrentLibraryStateAsync(cancellationToken);
        if (state.IsFailure)
        {
            return McpCommandResult<McpCiteMeta, McpCitationResult>.Fail(
                McpErrorMappings.ToReadError(state.ErrorCode),
                state.ErrorMessage ?? "Library state is unavailable.");
        }

        McpServerSettings? permissions = await PermissionSettingsAsync(cancellationToken);
        if (!Allowed(permissions, "cite", "patchouli://items/") ||
            !Allowed(permissions, "cite", "patchouli://csl-styles/") ||
            request.Refs.Any(uri => !Allowed(permissions, "cite", uri)))
        {
            return McpCommandResult<McpCiteMeta, McpCitationResult>.Fail(McpErrorCode.PermissionDenied,
                "cite is disabled for a required domain.");
        }

        string? styleId = null;
        if (request.Style is not null)
        {
            Result<McpUriParseResult> parsedStyle = McpResourceUris.Parse(request.Style);
            if (parsedStyle.IsFailure || parsedStyle.Value.Kind != McpUriKind.Style)
            {
                return McpCommandResult<McpCiteMeta, McpCitationResult>.Fail(McpErrorCode.InvalidArgument,
                    parsedStyle.ErrorMessage ??
                    $"'{request.Style}' is not a CSL style URI; expected patchouli://csl-styles/{{style-id}}.csl.");
            }

            styleId = parsedStyle.Value.StyleId;
        }

        List<string> warnings = [];
        List<ItemId> itemIds = [];
        string? effectiveStyleId = null;
        McpCitationResult[] results = new McpCitationResult[request.Refs.Count];

        for (int index = 0; index < request.Refs.Count; index++)
        {
            string reference = request.Refs[index];
            Result<McpUriParseResult> parsed = McpResourceUris.Parse(reference);
            if (parsed.IsFailure)
            {
                results[index] = new McpCitationResult(reference, null, null,
                    McpToolError.From(McpErrorCode.InvalidArgument,
                        parsed.ErrorMessage ?? $"'{reference}' is not a valid URI.").ToTerminalLine());
                continue;
            }

            reference = parsed.Value.CanonicalUri;
            Result<ItemId> resolved = await ResolveCitationItemAsync(parsed.Value, state.Value.LibraryId,
                cancellationToken);
            if (resolved.IsFailure)
            {
                McpErrorCode code = string.Equals(resolved.ErrorCode, AppErrorCodes.NotCitable,
                    StringComparison.Ordinal)
                    ? McpErrorCode.NotCitable
                    : McpErrorMappings.ToReadError(resolved.ErrorCode);
                results[index] = new McpCitationResult(reference, null, null,
                    McpToolError.From(code,
                        resolved.ErrorMessage ?? "Reference cannot be resolved to a citable item.").ToTerminalLine());
                continue;
            }

            Result<McpRenderBibliographyResponse> rendered = await _read.RenderItemBibliographyAsync(
                resolved.Value, styleId, request.Locale, cancellationToken);
            if (rendered.IsFailure)
            {
                McpErrorCode code = McpErrorMappings.ToReadError(rendered.ErrorCode);
                results[index] = new McpCitationResult(reference, null, null,
                    McpToolError.From(code,
                        rendered.ErrorMessage ?? rendered.ErrorCode ?? "Citation rendering failed.").ToTerminalLine());
                continue;
            }

            effectiveStyleId ??= rendered.Value.StyleId;
            warnings.AddRange(rendered.Value.Warnings.Select(McpWarningCodes.ToTerminalLine));
            results[index] = new McpCitationResult(reference, McpResourceUris.ItemUri(resolved.Value),
                rendered.Value.RenderedText, null);
            itemIds.Add(resolved.Value);
        }

        string? bibliography = null;
        if (request.Bibliography && itemIds.Count > 0)
        {
            Result<McpRenderBibliographyResponse> bibliographyRender = await _read.RenderItemsBibliographyAsync(
                new McpRenderBibliographyRequest(itemIds.Distinct().ToArray(), styleId, request.Locale,
                    true), cancellationToken);
            if (bibliographyRender.IsSuccess)
            {
                bibliography = bibliographyRender.Value.RenderedText;
                effectiveStyleId ??= bibliographyRender.Value.StyleId;
                warnings.AddRange(bibliographyRender.Value.Warnings.Select(McpWarningCodes.ToTerminalLine));
            }
        }

        string? effectiveStyleUri = effectiveStyleId is null ? null : McpResourceUris.StyleUri(effectiveStyleId);
        effectiveStyleUri ??= request.Style is null ? null : McpResourceUris.Normalize(request.Style);

        string? topError = null;
        if (results.Length > 0 && results.All(result => result.Error is not null))
        {
            topError = results[0].Error;
        }

        McpCiteMeta meta = new(state.Value.LibraryRevision, effectiveStyleUri, request.Locale ?? "en-US",
            request.Html ? "html" : "text", bibliography);
        McpMessage? message = warnings.Count == 0 && topError is null
            ? null
            : new McpMessage(topError, warnings.Distinct(StringComparer.Ordinal).ToArray());
        McpEnvelope<McpCiteMeta, McpCitationResult> envelope =
            McpEnvelope<McpCiteMeta, McpCitationResult>.Create(meta, results, null, message);
        return topError is null
            ? McpCommandResult<McpCiteMeta, McpCitationResult>.Ok(envelope)
            : McpCommandResult<McpCiteMeta, McpCitationResult>.Partial(envelope,
                McpToolError.TryGetCode(topError, out McpErrorCode topErrorCode) ? topErrorCode : McpErrorCode.Internal,
                topError);
    }

    private async Task<McpFetchResult> FetchSingleAsync(string uri, string? range, int limitBytes,
        McpLibraryStateResponse state, CancellationToken cancellationToken)
    {
        Result<McpUriParseResult> parsed = McpResourceUris.Parse(uri);
        if (parsed.IsFailure)
        {
            return FailedFetch(uri, McpToolError.From(McpErrorCode.InvalidArgument,
                parsed.ErrorMessage ?? "Invalid URI."), limitBytes);
        }

        if (!parsed.Value.IsLegacy &&
            parsed.Value.Kind is McpUriKind.Page or McpUriKind.Evidence or McpUriKind.TranslationPage)
        {
            return await FetchXmlPageAsync(parsed.Value, range, limitBytes, state, cancellationToken);
        }

        McpFetchResult fetched = parsed.Value.Kind switch
        {
            McpUriKind.RunEndpoint => await FetchEndpointDescriptionAsync(parsed.Value,
                range, limitBytes, state, cancellationToken),
            McpUriKind.Item => await FetchItemAsync(parsed.Value, range, limitBytes, state, cancellationToken),
            McpUriKind.Document => await FetchDocumentAsync(parsed.Value, range, limitBytes, state, cancellationToken),
            McpUriKind.Page => await FetchPageAsync(parsed.Value, range, limitBytes, state, cancellationToken),
            McpUriKind.DocumentMap => await FetchMapAsync(parsed.Value, range, limitBytes, state, cancellationToken),
            McpUriKind.Style => await FetchStyleAsync(parsed.Value, range, limitBytes, state, cancellationToken),
            McpUriKind.Evidence => await FetchEvidenceAsync(parsed.Value, range, limitBytes, state,
                cancellationToken),
            McpUriKind.TranslationPage => await FetchTranslationAsync(parsed.Value, range, limitBytes, state,
                cancellationToken),
            McpUriKind.Library => await FetchLibraryAsync(range, limitBytes, state, cancellationToken,
                parsed.Value.IsLegacy),
            McpUriKind.RunOcrStatus => await FetchOcrRunStatusAsync(parsed.Value, range, limitBytes, state,
                cancellationToken),
            McpUriKind.RunTranslationStatus => await FetchTranslationRunStatusAsync(parsed.Value, range, limitBytes,
                state, cancellationToken),
            McpUriKind.RunMetadataStatus => await FetchMetadataRunStatusAsync(parsed.Value, range, limitBytes,
                state, cancellationToken),
            _ => FailedFetch(uri, McpToolError.From(McpErrorCode.InvalidArgument,
                "Scopes cannot be fetched; use find to browse a scope."), limitBytes)
        };
        return fetched with { Uri = parsed.Value.CanonicalUri, ContentType = parsed.Value.ContentType };
    }

    private async Task<McpFetchResult> FetchXmlPageAsync(McpUriParseResult target, string? range,
        int limitBytes, McpLibraryStateResponse state, CancellationToken cancellationToken)
    {
        string uri = target.CanonicalUri;
        Result<McpDocumentOutlineResponse> outline =
            await _read.GetDocumentOutlineAsync(target.DocumentId!.Value, cancellationToken);
        if (outline.IsFailure)
        {
            return FailedFetch(uri,
                McpToolError.From(McpErrorMappings.ToReadError(outline.ErrorCode), outline.ErrorMessage), limitBytes);
        }

        McpDocumentPageRef? page = outline.Value.Pages.FirstOrDefault(value => value.PageIndex + 1 == target.PageIndex);
        if (page is null)
        {
            return FailedFetch(uri, McpToolError.From(McpErrorCode.NotFound, "Page not found."), limitBytes);
        }

        Result<McpPageXmlResponse> xml = await _read.GetPageXmlAsync(new McpPageXmlRequest(
            target.DocumentId.Value, page.PageId, target.PageIndex!.Value,
            target.Kind == McpUriKind.TranslationPage, target.TreeRevisionId, target.BoxId), cancellationToken);
        if (xml.IsFailure)
        {
            return FailedFetch(uri, McpToolError.From(McpErrorMappings.ToReadError(xml.ErrorCode), xml.ErrorMessage),
                limitBytes);
        }

        string? rangeError = ValidateRange(range, "boxes");
        if (rangeError is not null)
        {
            return FailedFetch(uri, McpToolError.From(McpErrorCode.InvalidArgument, rangeError), limitBytes);
        }

        XDocument document = XDocument.Parse(xml.Value.Xml, LoadOptions.PreserveWhitespace);
        XElement root = document.Root!;
        XElement[] boxes = root.Descendants("box").ToArray();
        int start = 1;
        int end = boxes.Length;
        if (TryParseRange(range, out _, out int requestedStart, out int requestedEnd))
        {
            start = requestedStart;
            end = Math.Min(requestedEnd, boxes.Length);
        }

        if (target.BoxId is not null)
        {
            int index = Array.FindIndex(boxes, box => (string?)box.Attribute("id") == target.BoxId.Value.ToString());
            if (index < 0)
            {
                return FailedFetch(uri,
                    McpToolError.From(McpErrorCode.NotFound, "Box does not belong to this page revision."), limitBytes);
            }

            start = index + 1;
            end = start;
        }

        if (start > boxes.Length && (boxes.Length > 0 || range is not null))
        {
            return FailedFetch(uri, McpToolError.From(McpErrorCode.InvalidArgument, "Box range starts past the page."),
                limitBytes);
        }

        string? itemUri = outline.Value.ItemId is null ? null : McpResourceUris.ItemUri(outline.Value.ItemId.Value);
        int selectedEnd = end;
        while (true)
        {
            XDocument selected = new(document);
            XElement selectedRoot = selected.Root!;
            XElement[] selectedBoxes = selectedRoot.Descendants("box").ToArray();
            HashSet<string> keep = selectedBoxes.Skip(Math.Max(0, start - 1)).Take(Math.Max(0, selectedEnd - start + 1))
                .SelectMany(box => box.AncestorsAndSelf("box")).Select(box => (string)box.Attribute("id")!)
                .ToHashSet(StringComparer.Ordinal);
            foreach (XElement box in selectedBoxes.Reverse())
            {
                if (!keep.Contains((string)box.Attribute("id")!))
                {
                    box.Remove();
                }
            }

            bool complete = start == 1 && selectedEnd == boxes.Length && target.BoxId is null;
            selectedRoot.SetAttributeValue("complete", complete ? "true" : "false");
            if (!complete)
            {
                selectedRoot.SetAttributeValue("range", $"boxes:{start}-{selectedEnd}");
            }

            string content = selected.ToString(SaveOptions.DisableFormatting);
            McpFetchResult candidate = CompleteFetch(uri,
                    target.Kind switch
                    {
                        McpUriKind.TranslationPage => "translation_page",
                        McpUriKind.Evidence => "evidence",
                        _ => "text_page"
                    },
                    itemUri, content, limitBytes) with
                {
                    ContentType = "application/xml", Translation = xml.Value.Translation,
                    Complete = complete,
                    Continuation = selectedEnd < boxes.Length && target.BoxId is null
                        ? $"boxes:{selectedEnd + 1}-{boxes.Length}"
                        : null,
                    NextRange = selectedEnd < boxes.Length && target.BoxId is null
                        ? $"boxes:{selectedEnd + 1}-{boxes.Length}"
                        : null
                };
            int size = SerializedSize(candidate, state.LibraryRevision);
            if (size <= limitBytes)
            {
                return candidate;
            }

            if (selectedEnd <= start)
            {
                return FailedFetch(uri, McpToolError.From(McpErrorCode.ResponseTruncated,
                    $"One XML box requires {size} response bytes; increase limit_bytes."), limitBytes);
            }

            selectedEnd--;
        }
    }

    private async Task<McpFetchResult> FetchOcrRunStatusAsync(McpUriParseResult target, string? range,
        int limitBytes, McpLibraryStateResponse state, CancellationToken cancellationToken)
    {
        string uri = McpResourceUris.OcrRunStatusUri(target.RunTaskId!);
        if (_ocrRuns is null)
        {
            return FailedFetch(uri, McpToolError.From(McpErrorCode.Unavailable,
                "OCR run projections are unavailable on this host."), limitBytes);
        }

        Result<McpOcrTaskStatusProjection> task =
            await _ocrRuns.TryGetTaskStatusAsync(target.RunTaskId!, cancellationToken);
        if (task.IsFailure)
        {
            return FailedFetch(uri, McpToolError.From(McpErrorMappings.ToReadError(task.ErrorCode),
                task.ErrorMessage ?? "OCR task was not found."), limitBytes);
        }

        string? rangeError = ValidateRange(range, "lines");
        if (rangeError is not null)
        {
            return FailedFetch(uri, McpToolError.From(McpErrorCode.InvalidArgument, rangeError), limitBytes);
        }

        string content = ApplyLines(DefaultToonEncoder(task.Value), range, "lines");
        return FitTextEntry(uri, "run_status", null, content, limitBytes, state.LibraryRevision);
    }

    private async Task<McpFetchResult> FetchTranslationRunStatusAsync(McpUriParseResult target, string? range,
        int limitBytes, McpLibraryStateResponse state, CancellationToken cancellationToken)
    {
        string uri = McpResourceUris.TranslationRunStatusUri(target.RunTaskId!);
        if (_translationRuns is null)
        {
            return FailedFetch(uri, McpToolError.From(McpErrorCode.Unavailable,
                "Translation run projections are unavailable on this host."), limitBytes);
        }

        Result<McpTranslationRunStatusProjection> run =
            await _translationRuns.TryGetRunStatusAsync(target.RunTaskId!, cancellationToken);
        if (run.IsFailure)
        {
            return FailedFetch(uri, McpToolError.From(McpErrorMappings.ToReadError(run.ErrorCode),
                run.ErrorMessage ?? "Translation run was not found."), limitBytes);
        }

        string? rangeError = ValidateRange(range, "lines");
        if (rangeError is not null)
        {
            return FailedFetch(uri, McpToolError.From(McpErrorCode.InvalidArgument, rangeError), limitBytes);
        }

        string content = ApplyLines(DefaultToonEncoder(run.Value), range, "lines");
        return FitTextEntry(uri, "run_status", null, content, limitBytes, state.LibraryRevision);
    }

    private async Task<McpFetchResult> FetchMetadataRunStatusAsync(McpUriParseResult target, string? range,
        int limitBytes, McpLibraryStateResponse state, CancellationToken cancellationToken)
    {
        string uri = McpResourceUris.MetadataRunStatusUri(target.RunTaskId!);
        if (_metadataRuns is null)
        {
            return FailedFetch(uri, McpToolError.From(McpErrorCode.Unavailable,
                "Metadata task projections are unavailable on this host."), limitBytes);
        }

        Result<McpMetadataTaskStatusProjection> task =
            await _metadataRuns.TryGetRunStatusAsync(target.RunTaskId!, cancellationToken);
        if (task.IsFailure)
        {
            return FailedFetch(uri, McpToolError.From(McpErrorMappings.ToReadError(task.ErrorCode),
                task.ErrorMessage ?? "Metadata task was not found."), limitBytes);
        }

        string? rangeError = ValidateRange(range, "lines");
        if (rangeError is not null)
        {
            return FailedFetch(uri, McpToolError.From(McpErrorCode.InvalidArgument, rangeError), limitBytes);
        }

        string content = ApplyLines(DefaultToonEncoder(task.Value), range, "lines");
        return FitTextEntry(uri, "run_status", null, content, limitBytes, state.LibraryRevision);
    }

    private async Task<McpFetchResult> FetchLibraryAsync(string? range, int limitBytes,
        McpLibraryStateResponse state, CancellationToken cancellationToken, bool legacy = false)
    {
        string uri = McpResourceUris.LibraryUri();
        Result<McpLibraryProjection> projection = await _read.GetLibraryProjectionAsync(
            _exposeLibraryTags, _exposeLibraryCollections, cancellationToken);
        if (projection.IsFailure)
        {
            return FailedFetch(uri,
                McpToolError.From(McpErrorMappings.ToReadError(projection.ErrorCode),
                    projection.ErrorMessage ?? "Library projection is unavailable."), limitBytes);
        }

        string? rangeError = ValidateRange(range, "lines");
        if (rangeError is not null)
        {
            return FailedFetch(uri, McpToolError.From(McpErrorCode.InvalidArgument, rangeError), limitBytes);
        }

        string content =
            ApplyLines(legacy ? DefaultToonEncoder(projection.Value) : McpYamlCodec.Encode(projection.Value), range,
                "lines");
        return FitTextEntry(uri, legacy ? "library_toon" : "library_yaml", uri, content, limitBytes,
            state.LibraryRevision);
    }

    private async Task<McpFetchResult> FetchItemAsync(McpUriParseResult target, string? range, int limitBytes,
        McpLibraryStateResponse state, CancellationToken cancellationToken)
    {
        string uri = McpResourceUris.ItemUri(target.ItemId!.Value);

        Result<ItemLifecycleInfo>
            lifecycle = await _items.GetItemLifecycleAsync(target.ItemId.Value, cancellationToken);
        if (lifecycle.IsFailure)
        {
            return FailedFetch(uri,
                McpToolError.From(McpErrorMappings.ToReadError(lifecycle.ErrorCode),
                    lifecycle.ErrorMessage ?? "Item was not found."), limitBytes);
        }

        if (lifecycle.Value.State == ItemLifecycleState.Trash)
        {
            return FailedFetch(uri,
                McpToolError.From(McpErrorCode.ItemInTrash,
                    $"Item {target.ItemId.Value} is in trash (deleted_at: {lifecycle.Value.DeletedAt:O})."),
                limitBytes);
        }

        if (lifecycle.Value.State == ItemLifecycleState.Merged)
        {
            return FailedFetch(uri,
                McpToolError.From(McpErrorCode.ItemMerged,
                    $"Item {target.ItemId.Value} is merged into {lifecycle.Value.MergedIntoItemId}."),
                limitBytes);
        }

        if (lifecycle.Value.State == ItemLifecycleState.Purged)
        {
            return FailedFetch(uri,
                McpToolError.From(McpErrorCode.NotFound,
                    $"Item {target.ItemId.Value} was purged (purged_at: {lifecycle.Value.PurgedAt:O})."),
                limitBytes);
        }

        Result<McpItemMetadataResponse> metadata = await _read.GetItemMetadataAsync(target.ItemId.Value,
            cancellationToken);
        if (metadata.IsFailure)
        {
            return FailedFetch(uri,
                McpToolError.From(McpErrorMappings.ToReadError(metadata.ErrorCode),
                    metadata.ErrorMessage ?? metadata.ErrorCode ?? "Item was not found."), limitBytes);
        }

        Result<string> exported = target.IsLegacy
            ? await _biblatex.ExportItemForAgentAsync(target.ItemId.Value, _exposeLibraryTags, cancellationToken)
            : await _read.GetItemYamlAsync(target.ItemId.Value, _exposeLibraryTags, cancellationToken);
        if (exported.IsFailure)
        {
            return FailedFetch(uri,
                McpToolError.From(McpErrorMappings.ToReadError(exported.ErrorCode),
                    exported.ErrorMessage ?? exported.ErrorCode ?? "BibLaTeX export failed."), limitBytes);
        }

        string? rangeError = ValidateRange(range, "lines");
        if (rangeError is not null)
        {
            return FailedFetch(uri, McpToolError.From(McpErrorCode.InvalidArgument, rangeError), limitBytes);
        }

        string content = ApplyLines(exported.Value, range, "lines");
        return FitTextEntry(uri, target.IsLegacy ? "item_bib" : "item_yaml",
            McpResourceUris.ItemUri(target.ItemId.Value), content, limitBytes,
            state.LibraryRevision);
    }

    private async Task<McpFetchResult> FetchDocumentAsync(McpUriParseResult target, string? range, int limitBytes,
        McpLibraryStateResponse state, CancellationToken cancellationToken)
    {
        string uri = McpResourceUris.DocumentUri(target.DocumentId!.Value);
        Result<McpDocumentOutlineResponse> outline = await _read.GetDocumentOutlineAsync(target.DocumentId.Value,
            cancellationToken);
        if (outline.IsFailure)
        {
            return FailedFetch(uri,
                McpToolError.From(McpErrorMappings.ToReadError(outline.ErrorCode),
                    outline.ErrorMessage ?? outline.ErrorCode ?? "Document was not found."), limitBytes);
        }

        string? rangeError = ValidateRange(range, "pages");
        if (rangeError is not null)
        {
            return FailedFetch(uri, McpToolError.From(McpErrorCode.InvalidArgument, rangeError), limitBytes);
        }

        string? itemUri = outline.Value.ItemId is null
            ? null
            : McpResourceUris.ItemUri(outline.Value.ItemId.Value);
        McpDocumentPageRef[] selected;
        if (TryParseRange(range, out string kind, out int start, out int end) && kind == "pages")
        {
            selected = outline.Value.Pages
                .Where(page => page.PageIndex + 1 >= start && page.PageIndex + 1 <= end)
                .ToArray();
        }
        else
        {
            selected = outline.Value.Pages.ToArray();
        }

        return FitDocumentEntry(uri, itemUri, outline.Value.Title, outline.Value.DocumentInstanceId, selected,
            limitBytes, state.LibraryRevision);
    }

    /// <summary>
    /// Fetches the <c>map.yml</c> projection of a document's chapter/pagination map. Plain
    /// fetch triggers no OCR or AI work. Documents without a stored map get the discoverable,
    /// first-put-able empty-map projection. The map itself is not citable.
    /// </summary>
    private async Task<McpFetchResult> FetchMapAsync(McpUriParseResult target, string? range, int limitBytes,
        McpLibraryStateResponse state, CancellationToken cancellationToken)
    {
        string uri = McpResourceUris.MapUri(target.DocumentId!.Value);
        Result<McpDocumentOutlineResponse> outline = await _read.GetDocumentOutlineAsync(target.DocumentId.Value,
            cancellationToken);
        if (outline.IsFailure)
        {
            return FailedFetch(uri,
                McpToolError.From(McpErrorMappings.ToReadError(outline.ErrorCode),
                    outline.ErrorMessage ?? outline.ErrorCode ?? "Document was not found."), limitBytes);
        }

        string? rangeError = ValidateRange(range, "lines");
        if (rangeError is not null)
        {
            return FailedFetch(uri, McpToolError.From(McpErrorCode.InvalidArgument, rangeError), limitBytes);
        }

        Result<string> yaml = await _read.GetDocumentMapYamlAsync(target.DocumentId.Value, cancellationToken);
        if (yaml.IsFailure)
        {
            return FailedFetch(uri,
                McpToolError.From(McpErrorMappings.ToReadError(yaml.ErrorCode),
                    yaml.ErrorMessage ?? yaml.ErrorCode ?? "Page map was not found."), limitBytes);
        }

        string? itemUri = outline.Value.ItemId is null
            ? null
            : McpResourceUris.ItemUri(outline.Value.ItemId.Value);
        string content = ApplyLines(yaml.Value, range, "lines");
        return FitTextEntry(uri, "document_page_map", itemUri, content, limitBytes, state.LibraryRevision);
    }

    private async Task<McpFetchResult> FetchPageAsync(McpUriParseResult target, string? range, int limitBytes,
        McpLibraryStateResponse state, CancellationToken cancellationToken)
    {
        string uri = McpResourceUris.PageUri(target.DocumentId!.Value, target.PageIndex!.Value);
        Result<McpDocumentOutlineResponse> outline = await _read.GetDocumentOutlineAsync(target.DocumentId.Value,
            cancellationToken);
        if (outline.IsFailure)
        {
            return FailedFetch(uri,
                McpToolError.From(McpErrorMappings.ToReadError(outline.ErrorCode),
                    outline.ErrorMessage ?? outline.ErrorCode ?? "Document was not found."), limitBytes);
        }

        McpDocumentPageRef? pageRef = outline.Value.Pages.FirstOrDefault(page =>
            page.PageIndex + 1 == target.PageIndex!.Value);
        if (pageRef is null)
        {
            return FailedFetch(uri,
                McpToolError.From(McpErrorCode.NotFound,
                    $"Page '{target.PageIndex}' does not exist in document '{target.DocumentId}'."), limitBytes);
        }

        string? rangeError = ValidateRange(range, "lines");
        if (rangeError is not null)
        {
            return FailedFetch(uri, McpToolError.From(McpErrorCode.InvalidArgument, rangeError), limitBytes);
        }

        Result<McpPageTextResponse> page = await _read.GetPageTextAsync(
            new McpPageTextRequest(pageRef.PageId), cancellationToken);
        if (page.IsFailure)
        {
            return FailedFetch(uri,
                McpToolError.From(McpErrorMappings.ToReadError(page.ErrorCode),
                    page.ErrorMessage ?? page.ErrorCode ?? "Page was not found."), limitBytes);
        }

        string? itemUri = outline.Value.ItemId is null
            ? null
            : McpResourceUris.ItemUri(outline.Value.ItemId.Value);
        string content = ApplyLines(page.Value.Text, range, "lines");
        return FitTextEntry(uri, "text_page", itemUri, content, limitBytes, state.LibraryRevision);
    }

    private async Task<McpFetchResult> FetchStyleAsync(McpUriParseResult target, string? range, int limitBytes,
        McpLibraryStateResponse state, CancellationToken cancellationToken)
    {
        string uri = McpResourceUris.StyleUri(target.StyleId!);
        Result<McpCslStyleResponse> style = await _read.GetCslStyleAsync(target.StyleId!, cancellationToken);
        if (style.IsFailure)
        {
            return FailedFetch(uri,
                McpToolError.From(McpErrorMappings.ToReadError(style.ErrorCode),
                    style.ErrorMessage ?? style.ErrorCode ?? "Style was not found."), limitBytes);
        }

        string? rangeError = ValidateRange(range, "lines");
        if (rangeError is not null)
        {
            return FailedFetch(uri, McpToolError.From(McpErrorCode.InvalidArgument, rangeError), limitBytes);
        }

        string content = ApplyLines(style.Value.ContentXml, range, "lines");
        return FitTextEntry(uri, "csl_style", null, content, limitBytes, state.LibraryRevision);
    }

    private async Task<McpFetchResult> FetchEvidenceAsync(McpUriParseResult target, string? range, int limitBytes,
        McpLibraryStateResponse state, CancellationToken cancellationToken)
    {
        DocumentInstanceId documentId = target.DocumentId!.Value;
        int pageIndex = target.PageIndex!.Value;
        string uri = McpResourceUris.EvidencePageUri(documentId, pageIndex, target.TreeRevisionId, target.BoxId);

        Result<McpDocumentOutlineResponse> outline = await _read.GetDocumentOutlineAsync(documentId, cancellationToken);
        if (outline.IsFailure)
        {
            return FailedFetch(uri,
                McpToolError.From(McpErrorMappings.ToReadError(outline.ErrorCode),
                    outline.ErrorMessage ?? outline.ErrorCode ?? "Document was not found."), limitBytes);
        }

        if (outline.Value.Pages.All(page => page.PageIndex + 1 != pageIndex))
        {
            return FailedFetch(uri,
                McpToolError.From(McpErrorCode.NotFound,
                    $"Page '{pageIndex}' does not exist in document '{documentId}'."), limitBytes);
        }

        Result<EvidencePageText> evidence = await _evidenceReader.GetBoxTextAsync(
            documentId, pageIndex, target.TreeRevisionId, target.BoxId, cancellationToken);
        if (evidence.IsFailure)
        {
            return FailedFetch(uri,
                McpToolError.From(McpErrorMappings.ToReadError(evidence.ErrorCode),
                    evidence.ErrorMessage ?? "Evidence was not found."), limitBytes);
        }

        string? rangeError = ValidateRange(range, "lines");
        if (rangeError is not null)
        {
            return FailedFetch(uri, McpToolError.From(McpErrorCode.InvalidArgument, rangeError), limitBytes);
        }

        string content = ApplyLines(BuildEvidenceContent(evidence.Value), range, "lines");
        Result<ItemId> owner = await _read.GetItemIdForDocumentAsync(documentId, cancellationToken);
        string? itemUri = owner.IsSuccess ? McpResourceUris.ItemUri(owner.Value) : null;
        return FitTextEntry(uri, "evidence", itemUri, content, limitBytes, state.LibraryRevision);
    }

    private async Task<McpFetchResult> FetchTranslationAsync(McpUriParseResult target, string? range, int limitBytes,
        McpLibraryStateResponse state, CancellationToken cancellationToken)
    {
        DocumentInstanceId documentId = target.DocumentId!.Value;
        int pageIndex = target.PageIndex!.Value;
        string uri = McpResourceUris.TranslationPageUri(documentId, pageIndex);

        Result<McpDocumentOutlineResponse> outline = await _read.GetDocumentOutlineAsync(documentId, cancellationToken);
        if (outline.IsFailure)
        {
            return FailedFetch(uri,
                McpToolError.From(McpErrorMappings.ToReadError(outline.ErrorCode),
                    outline.ErrorMessage ?? outline.ErrorCode ?? "Document was not found."), limitBytes);
        }

        McpDocumentPageRef? pageRef = outline.Value.Pages.FirstOrDefault(page =>
            page.PageIndex + 1 == pageIndex);
        if (pageRef is null)
        {
            return FailedFetch(uri,
                McpToolError.From(McpErrorCode.NotFound,
                    $"Page '{pageIndex}' does not exist in document '{documentId}'."), limitBytes);
        }

        Result<McpPageTranslationResponse> translation = await _read.GetPageTranslationAsync(
            new McpPageTranslationRequest(documentId, pageRef.PageId), cancellationToken);
        if (translation.IsFailure)
        {
            if (string.Equals(translation.ErrorCode, AppErrorCodes.NotFound, StringComparison.Ordinal))
            {
                return FailedFetch(uri,
                    McpToolError.From(McpErrorCode.NotFound,
                        $"Page {pageIndex} of document {documentId} has no translation yet. " +
                        $"Fetch the source page at patchouli://texts/{documentId}/page-{pageIndex}.xml, " +
                        $"translate it, and put the complete result back to {uri}."), limitBytes);
            }

            return FailedFetch(uri,
                McpToolError.From(McpErrorMappings.ToReadError(translation.ErrorCode),
                    translation.ErrorMessage ?? translation.ErrorCode ?? "Page translation was not found."),
                limitBytes);
        }

        string? rangeError = ValidateRange(range, "lines");
        if (rangeError is not null)
        {
            return FailedFetch(uri, McpToolError.From(McpErrorCode.InvalidArgument, rangeError), limitBytes);
        }

        string? itemUri = outline.Value.ItemId is null
            ? null
            : McpResourceUris.ItemUri(outline.Value.ItemId.Value);
        string content = ApplyLines(translation.Value.Markdown, range, "lines");
        McpFetchResult fitted =
            FitTextEntry(uri, "translation_page", itemUri, content, limitBytes, state.LibraryRevision);
        return fitted with { Translation = translation.Value.Status };
    }

    private static string BuildEvidenceContent(EvidencePageText evidence)
    {
        StringBuilder builder = new();
        if (!string.IsNullOrWhiteSpace(evidence.SourceTitle))
        {
            builder.Append("source: ").Append(evidence.SourceTitle).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(evidence.PageLabel))
        {
            builder.Append("page: ").Append(evidence.PageLabel).Append('\n');
        }

        if (builder.Length > 0)
        {
            builder.Append('\n');
        }

        builder.Append(evidence.Markdown);
        return builder.ToString();
    }

    private static string BuildOutline(DocumentInstanceId documentId, string? title,
        IReadOnlyList<McpDocumentPageRef> pages)
    {
        StringBuilder builder = new();
        if (!string.IsNullOrWhiteSpace(title))
        {
            builder.Append(title).Append('\n');
        }

        builder.Append("pages:").Append('\n');
        foreach (McpDocumentPageRef page in pages)
        {
            int oneBased = page.PageIndex + 1;
            string label = string.IsNullOrWhiteSpace(page.PageLabel) ? oneBased.ToString() : page.PageLabel;
            builder.Append(oneBased).Append('\t').Append(label).Append('\t')
                .Append(McpResourceUris.PageUri(documentId, oneBased)).Append('\n');
        }

        return builder.ToString();
    }

    private static McpFetchResult FitDocumentEntry(string uri, string? itemUri, string? title,
        DocumentInstanceId documentId, IReadOnlyList<McpDocumentPageRef> pages, int limitBytes, string libraryRevision)
    {
        for (int count = pages.Count; count >= 0; count--)
        {
            string content = BuildOutline(documentId, title, pages.Take(count).ToArray());
            McpFetchResult candidate = CompleteFetch(uri, "text_document", itemUri, content, limitBytes);
            if (SerializedSize(candidate, libraryRevision) <= limitBytes || count == 0)
            {
                if (count == pages.Count && SerializedSize(candidate, libraryRevision) <= limitBytes)
                {
                    return candidate;
                }

                string? nextRange = count < pages.Count
                    ? $"pages:{pages[count].PageIndex + 1}-{pages[^1].PageIndex + 1}"
                    : $"pages:{pages[^1].PageIndex + 1}-{pages[^1].PageIndex + 1}";
                int returned = Encoding.UTF8.GetByteCount(content);
                McpToolError error = McpToolError.From(McpErrorCode.ResponseTruncated,
                    "Response exceeds limit_bytes; partial content is available and must not be treated as complete.");
                return new McpFetchResult(uri, "text_document", itemUri, content, false, true, returned, limitBytes,
                    nextRange, nextRange, error.ToTerminalLine());
            }
        }

        McpToolError unreachable = McpToolError.From(McpErrorCode.Internal, "Document outline fitting failed.");
        return FailedFetch(uri, unreachable, limitBytes);
    }

    private static McpFetchResult FitTextEntry(string uri, string resourceType, string? itemUri, string fullContent,
        int limitBytes, string libraryRevision)
    {
        McpFetchResult full = CompleteFetch(uri, resourceType, itemUri, fullContent, limitBytes);
        if (SerializedSize(full, libraryRevision) <= limitBytes)
        {
            return full;
        }

        int low = 0;
        int high = fullContent.Length;
        string best = string.Empty;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            string candidate = TakeSafePrefix(fullContent, middle);
            McpFetchResult candidateEntry = CompleteFetch(uri, resourceType, itemUri, candidate, limitBytes);
            if (SerializedSize(candidateEntry, libraryRevision) <= limitBytes)
            {
                best = candidate;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        int consumedLines = CountLines(best);
        int totalLines = CountLines(fullContent);
        string? nextRange = consumedLines >= totalLines ? null : $"lines:{consumedLines + 1}-{totalLines}";
        int returned = Encoding.UTF8.GetByteCount(best);
        McpToolError error = McpToolError.From(McpErrorCode.ResponseTruncated,
            "Response exceeds limit_bytes; partial content is available and must not be treated as complete.");
        return new McpFetchResult(uri, resourceType, itemUri, best, false, true, returned, limitBytes, nextRange,
            nextRange, error.ToTerminalLine());
    }

    private static McpFetchResult CompleteFetch(string uri, string resourceType, string? itemUri, string content,
        int limitBytes)
    {
        return new McpFetchResult(uri, resourceType, itemUri, content, true, false,
            Encoding.UTF8.GetByteCount(content), limitBytes, null, null, null);
    }

    private static McpFetchResult FailedFetch(string uri, McpToolError error, int limitBytes)
    {
        return new McpFetchResult(uri, null, null, null, false, false, 0, limitBytes, null, null,
            error.ToTerminalLine());
    }

    private static int SerializedSize(McpFetchResult entry, string libraryRevision)
    {
        McpEnvelope<McpFetchMeta, McpFetchResult> envelope =
            McpEnvelope<McpFetchMeta, McpFetchResult>.Create(new McpFetchMeta(libraryRevision), [entry]);
        return Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(envelope));
    }

    private async Task<Result<ItemId>> ResolveCitationItemAsync(McpUriParseResult reference, string libraryId,
        CancellationToken cancellationToken)
    {
        switch (reference.Kind)
        {
            case McpUriKind.Item:
                return Result<ItemId>.Success(reference.ItemId!.Value);

            case McpUriKind.Document:
                return await _read.GetItemIdForDocumentAsync(reference.DocumentId!.Value, cancellationToken);

            case McpUriKind.Page:
            {
                Result<McpDocumentOutlineResponse> outline = await _read.GetDocumentOutlineAsync(
                    reference.DocumentId!.Value, cancellationToken);
                if (outline.IsFailure)
                {
                    return Result<ItemId>.Failure(outline.ErrorCode!, outline.ErrorMessage!);
                }

                if (outline.Value.Pages.All(page => page.PageIndex + 1 != reference.PageIndex!.Value))
                {
                    return Result<ItemId>.Failure(AppErrorCodes.NotFound,
                        $"Page '{reference.PageIndex}' does not belong to document '{reference.DocumentId}'.");
                }

                return await _read.GetItemIdForDocumentAsync(reference.DocumentId.Value, cancellationToken);
            }

            case McpUriKind.Evidence:
            {
                Result<McpDocumentOutlineResponse> outline = await _read.GetDocumentOutlineAsync(
                    reference.DocumentId!.Value, cancellationToken);
                if (outline.IsFailure)
                {
                    return Result<ItemId>.Failure(outline.ErrorCode!, outline.ErrorMessage!);
                }

                if (outline.Value.Pages.All(page => page.PageIndex + 1 != reference.PageIndex!.Value))
                {
                    return Result<ItemId>.Failure(AppErrorCodes.NotFound,
                        $"Page '{reference.PageIndex}' does not belong to document '{reference.DocumentId}'.");
                }

                Result<EvidencePageText> evidence = await _evidenceReader.GetBoxTextAsync(
                    reference.DocumentId.Value,
                    reference.PageIndex!.Value,
                    reference.TreeRevisionId,
                    reference.BoxId,
                    cancellationToken);
                if (evidence.IsFailure)
                {
                    return Result<ItemId>.Failure(evidence.ErrorCode!, evidence.ErrorMessage!);
                }

                return await _read.GetItemIdForDocumentAsync(reference.DocumentId.Value, cancellationToken);
            }

            default:
                return Result<ItemId>.Failure(AppErrorCodes.NotCitable,
                    $"{reference.Kind} is not a citation-capable resource.");
        }
    }

    private async Task<FindPage> BrowseScopeAsync(McpUriParseResult scope, McpUriKind kind, string scopeUri,
        int limit, int offset, IReadOnlyList<McpWhereClause>? where, bool longMode, List<string> warnings,
        CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case McpUriKind.ItemsScope:
            {
                Result<McpBrowseItemPage> page = await _read.BrowseItemsAsync(offset, limit, where,
                    cancellationToken);
                if (page.IsFailure)
                {
                    return FindPage.Failed(McpErrorMappings.ToReadError(page.ErrorCode),
                        page.ErrorMessage ?? page.ErrorCode ?? "Browse items failed.");
                }

                List<object> entries = page.Value.Rows.Select(row => BuildItemEntry(row, longMode))
                    .Cast<object>().ToList();
                string? continuation = page.Value.HasMore
                    ? EncodeCursor(scopeUri, null, false, where, offset + entries.Count, null)
                    : null;
                return new FindPage(entries, continuation, page.Value.DomainTotal, page.Value.FilteredTotal);
            }

            case McpUriKind.TextsScope:
            {
                Result<McpBrowseDocumentPage> page = await _read.BrowseDocumentsAsync(offset, limit, where,
                    cancellationToken);
                if (page.IsFailure)
                {
                    return FindPage.Failed(McpErrorMappings.ToReadError(page.ErrorCode),
                        page.ErrorMessage ?? page.ErrorCode ?? "Browse texts failed.");
                }

                List<object> entries = [];
                foreach (McpBrowseDocumentRow row in page.Value.Rows)
                {
                    entries.Add(BuildDocumentEntryAsync(row, longMode));
                }

                string? continuation = page.Value.HasMore
                    ? EncodeCursor(scopeUri, null, false, where, offset + entries.Count, null)
                    : null;
                return new FindPage(entries, continuation, page.Value.DomainTotal, page.Value.FilteredTotal);
            }

            case McpUriKind.StylesScope:
            {
                Result<McpBrowseStylePage> page = await _read.BrowseStylesAsync(offset, limit, where,
                    cancellationToken);
                if (page.IsFailure)
                {
                    return FindPage.Failed(McpErrorMappings.ToReadError(page.ErrorCode),
                        page.ErrorMessage ?? page.ErrorCode ?? "Browse styles failed.");
                }

                List<object> entries = page.Value.Rows.Select(row => BuildStyleEntry(row, longMode))
                    .Cast<object>().ToList();
                string? continuation = page.Value.HasMore
                    ? EncodeCursor(scopeUri, null, false, where, offset + entries.Count, null)
                    : null;
                return new FindPage(entries, continuation, page.Value.DomainTotal, page.Value.FilteredTotal);
            }

            case McpUriKind.TranslationsScope:
            {
                Result<McpBrowseTranslationPage> page = await _read.BrowseTranslationsAsync(offset, limit, null, where,
                    cancellationToken);
                if (page.IsFailure)
                {
                    return FindPage.Failed(McpErrorMappings.ToReadError(page.ErrorCode),
                        page.ErrorMessage ?? page.ErrorCode ?? "Browse translations failed.");
                }

                List<object> entries = page.Value.Rows.Select(BuildTranslationDocumentEntry).Cast<object>().ToList();
                string? continuation = page.Value.HasMore
                    ? EncodeCursor(scopeUri, null, false, where, offset + entries.Count, null)
                    : null;
                return new FindPage(entries, continuation, page.Value.DomainTotal, page.Value.FilteredTotal);
            }

            case McpUriKind.TranslationDocument:
            {
                Result<McpTranslationOutlineResponse> outline = await _read.GetTranslationOutlineAsync(
                    scope.DocumentId!.Value, null, cancellationToken);
                if (outline.IsFailure)
                {
                    return FindPage.Failed(McpErrorMappings.ToReadError(outline.ErrorCode),
                        outline.ErrorMessage ?? outline.ErrorCode ?? "Translation outline was not found.");
                }

                bool keep = await MatchesResourceWhereAsync(outline.Value.ItemId, scope.DocumentId.Value,
                    outline.Value.ItemId is not null, where, cancellationToken);
                if (!keep)
                {
                    return new FindPage([], null, 0, 0);
                }

                object[] all = outline.Value.Pages.Select(BuildTranslationPageEntry).Cast<object>().ToArray();
                object[] entries = all.Skip(offset).Take(limit).ToArray();
                string? continuation = offset + entries.Length < all.Length
                    ? EncodeCursor(scopeUri, null, false, where, offset + entries.Length, null)
                    : null;
                return new FindPage(entries, continuation, all.Length, all.Length);
            }

            case McpUriKind.RunsScope:
            {
                // The volatile runs root exposes its independent runtime subtrees: OCR tasks,
                // direct translation runs and metadata enrich_batch tasks. Their status
                // vocabularies stay separate and are never unified.
                object[] runRoots =
                [
                    new McpFindEntry(McpResourceUris.RunsOcrScopeUri(), "/runs/ocr", "directory"),
                    new McpFindEntry(McpResourceUris.RunsTranslationScopeUri(), "/runs/translation", "directory"),
                    new McpFindEntry(McpResourceUris.RunsMetadataScopeUri(), "/runs/metadata", "directory")
                ];
                object[] page = runRoots.Skip(offset).Take(limit).ToArray();
                string? continuation = offset + page.Length < runRoots.Length
                    ? EncodeCursor(scopeUri, null, false, where, offset + page.Length, null)
                    : null;
                return new FindPage(page, continuation, runRoots.Length, runRoots.Length);
            }

            case McpUriKind.RunsOcrScope:
            {
                McpServerSettings? permissions = await PermissionSettingsAsync(cancellationToken);
                Result<IReadOnlyList<McpOcrTaskStatusProjection>> tasks = _ocrRuns is null
                    ? Result<IReadOnlyList<McpOcrTaskStatusProjection>>.Success([])
                    : await _ocrRuns.ListTasksAsync(cancellationToken);
                if (tasks.IsFailure)
                {
                    return FindPage.Failed(McpErrorMappings.ToReadError(tasks.ErrorCode),
                        tasks.ErrorMessage ?? "Cannot list runs.");
                }

                object[] all = tasks.Value
                    .Select(task => (object)new McpFindEntry(
                        McpResourceUris.OcrRunStatusUri(task.TaskId),
                        string.IsNullOrWhiteSpace(task.ItemTitle) ? task.TaskId : task.ItemTitle,
                        "file"))
                    .Concat(EndpointVisible(permissions, Core.Actions.RunEndpointCatalog.Uri("ocr"))
                        ? new object[]
                            { new McpFindEntry(Core.Actions.RunEndpointCatalog.Uri("ocr"), "control", "endpoint") }
                        : [])
                    .ToArray();
                object[] page = all.Skip(offset).Take(limit).ToArray();
                string? continuation = offset + page.Length < all.Length
                    ? EncodeCursor(scopeUri, null, false, where, offset + page.Length, null)
                    : null;
                return new FindPage(page, continuation, all.Length, all.Length);
            }

            case McpUriKind.RunsTranslationScope:
            {
                McpServerSettings? permissions = await PermissionSettingsAsync(cancellationToken);
                Result<IReadOnlyList<McpTranslationRunStatusProjection>> runs = _translationRuns is null
                    ? Result<IReadOnlyList<McpTranslationRunStatusProjection>>.Success([])
                    : await _translationRuns.ListRunsAsync(cancellationToken);
                if (runs.IsFailure)
                {
                    return FindPage.Failed(McpErrorMappings.ToReadError(runs.ErrorCode),
                        runs.ErrorMessage ?? "Cannot list runs.");
                }

                object[] all = runs.Value
                    .Select(run => (object)new McpFindEntry(
                        McpResourceUris.TranslationRunStatusUri(run.TaskId),
                        string.IsNullOrWhiteSpace(run.DocumentId) ? run.TaskId : run.DocumentId,
                        "file"))
                    .Concat(EndpointVisible(permissions, Core.Actions.RunEndpointCatalog.Uri("translation"))
                        ? new object[]
                        {
                            new McpFindEntry(Core.Actions.RunEndpointCatalog.Uri("translation"), "control", "endpoint")
                        }
                        : [])
                    .ToArray();
                object[] page = all.Skip(offset).Take(limit).ToArray();
                string? continuation = offset + page.Length < all.Length
                    ? EncodeCursor(scopeUri, null, false, where, offset + page.Length, null)
                    : null;
                return new FindPage(page, continuation, all.Length, all.Length);
            }

            case McpUriKind.RunsMetadataScope:
            {
                McpServerSettings? permissions = await PermissionSettingsAsync(cancellationToken);
                Result<IReadOnlyList<McpMetadataTaskStatusProjection>> tasks = _metadataRuns is null
                    ? Result<IReadOnlyList<McpMetadataTaskStatusProjection>>.Success([])
                    : await _metadataRuns.ListRunsAsync(cancellationToken);
                if (tasks.IsFailure)
                {
                    return FindPage.Failed(McpErrorMappings.ToReadError(tasks.ErrorCode),
                        tasks.ErrorMessage ?? "Cannot list metadata tasks.");
                }

                object[] all = tasks.Value
                    .Select(task => (object)new McpFindEntry(
                        McpResourceUris.MetadataRunStatusUri(task.TaskId),
                        string.IsNullOrWhiteSpace(task.TaskId) ? task.TaskId : $"metadata batch {task.TaskId}",
                        "file"))
                    .Concat(EndpointVisible(permissions, Core.Actions.RunEndpointCatalog.Uri("metadata"))
                        ? new object[]
                            { new McpFindEntry(Core.Actions.RunEndpointCatalog.Uri("metadata"), "control", "endpoint") }
                        : [])
                    .ToArray();
                object[] page = all.Skip(offset).Take(limit).ToArray();
                string? continuation = offset + page.Length < all.Length
                    ? EncodeCursor(scopeUri, null, false, where, offset + page.Length, null)
                    : null;
                return new FindPage(page, continuation, all.Length, all.Length);
            }

            default:
                return FindPage.Failed(McpErrorCode.InvalidArgument, "Unsupported browse scope.");
        }
    }

    private async Task<FindPage> SearchScopeAsync(McpUriParseResult scope, McpUriKind kind, string scopeUri,
        string query, bool literal, int limit, McpCursor? cursor, IReadOnlyList<McpWhereClause>? where,
        bool longMode, List<string> warnings, CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case McpUriKind.ItemsScope:
            {
                int skip = cursor?.Offset ?? 0;
                Result<McpBrowseItemPage> page = await _read.SearchItemsAsync(query, literal, skip, limit, where,
                    cancellationToken);
                if (page.IsFailure)
                {
                    return FindPage.Failed(McpErrorMappings.ToReadError(page.ErrorCode),
                        page.ErrorMessage ?? page.ErrorCode ?? "Search items failed.");
                }

                List<object> entries = page.Value.Rows.Select(row => BuildItemEntry(row, longMode))
                    .Cast<object>().ToList();
                string? continuation = page.Value.HasMore
                    ? EncodeCursor(scopeUri, query, literal, where, skip + entries.Count, null)
                    : null;
                return new FindPage(entries, continuation, page.Value.DomainTotal, page.Value.FilteredTotal);
            }

            case McpUriKind.TextsScope:
            {
                McpSearchLibraryRequest searchRequest = new(query, limit,
                    cursor?.SearchCursor, IncludeRewritePlan: false, DisableQueryRewrite: literal);
                Result<McpSearchLibraryResponse> search = await _read.SearchLibraryAsync(searchRequest,
                    cancellationToken);
                if (search.IsFailure)
                {
                    return FindPage.Failed(McpErrorMappings.ToReadError(search.ErrorCode),
                        search.ErrorMessage ?? search.ErrorCode ?? "Search failed.");
                }

                Result<IReadOnlyList<McpTextResourceProjection>> projections =
                    await _read.GetTextResourceProjectionsAsync(
                        search.Value.Results.Select(result => result.DocumentInstanceId).Distinct().ToArray(), where,
                        cancellationToken);
                if (projections.IsFailure)
                {
                    return FindPage.Failed(McpErrorMappings.ToReadError(projections.ErrorCode),
                        projections.ErrorMessage ?? projections.ErrorCode ?? "Text resource projection failed.");
                }

                Dictionary<DocumentInstanceId, McpTextResourceProjection> projectionByDocument = projections.Value
                    .ToDictionary(projection => projection.DocumentInstanceId);
                List<object> entries = [];
                foreach (McpSearchPageResult page in search.Value.Results)
                {
                    if (!projectionByDocument.TryGetValue(page.DocumentInstanceId,
                            out McpTextResourceProjection? projection))
                    {
                        continue;
                    }

                    foreach (McpMatchedUnit unit in page.MatchedUnits)
                    {
                        entries.Add(BuildEvidenceEntry(page, unit, projection, longMode));
                    }
                }

                int filtered = search.Value.EstimatedTotal ?? entries.Count;
                string? continuation = search.Value.NextCursor is null
                    ? null
                    : EncodeCursor(scopeUri, query, literal, where, 0, search.Value.NextCursor);
                return new FindPage(entries, continuation, filtered, filtered);
            }

            case McpUriKind.StylesScope:
                return await SearchStylesAsync(query, literal, limit, cursor?.Offset ?? 0, where, longMode,
                    cancellationToken);

            case McpUriKind.TranslationsScope:
            {
                int skip = cursor?.Offset ?? 0;
                Result<McpBrowseTranslationPage> page = await _read.BrowseTranslationsAsync(skip, limit, query, where,
                    cancellationToken);
                if (page.IsFailure)
                {
                    return FindPage.Failed(McpErrorMappings.ToReadError(page.ErrorCode),
                        page.ErrorMessage ?? page.ErrorCode ?? "Search translations failed.");
                }

                List<object> entries = page.Value.Rows.Select(BuildTranslationDocumentEntry).Cast<object>().ToList();
                string? continuation = page.Value.HasMore
                    ? EncodeCursor(scopeUri, query, literal, where, skip + entries.Count, null)
                    : null;
                return new FindPage(entries, continuation, page.Value.DomainTotal, page.Value.FilteredTotal);
            }

            case McpUriKind.TranslationDocument:
            {
                Result<McpTranslationOutlineResponse> outline = await _read.GetTranslationOutlineAsync(
                    scope.DocumentId!.Value, query, cancellationToken);
                if (outline.IsFailure)
                {
                    return FindPage.Failed(McpErrorMappings.ToReadError(outline.ErrorCode),
                        outline.ErrorMessage ?? outline.ErrorCode ?? "Translation outline was not found.");
                }

                bool keep = await MatchesResourceWhereAsync(outline.Value.ItemId, scope.DocumentId.Value,
                    outline.Value.ItemId is not null, where, cancellationToken);
                if (!keep)
                {
                    return new FindPage([], null, 0, 0);
                }

                object[] all = outline.Value.Pages.Select(BuildTranslationPageEntry).Cast<object>().ToArray();
                int skip = cursor?.Offset ?? 0;
                object[] entries = all.Skip(skip).Take(limit).ToArray();
                string? continuation = skip + entries.Length < all.Length
                    ? EncodeCursor(scopeUri, query, literal, where, skip + entries.Length, null)
                    : null;
                return new FindPage(entries, continuation, all.Length, all.Length);
            }

            default:
                return FindPage.Failed(McpErrorCode.InvalidArgument, "Unsupported search scope.");
        }
    }

    private async Task<FindPage> SearchStylesAsync(string query, bool literal, int limit, int offset,
        IReadOnlyList<McpWhereClause>? where, bool longMode, CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<McpCslStyleSummary>> styles = await _read.ListCslStylesAsync(cancellationToken);
        if (styles.IsFailure)
        {
            return FindPage.Failed(McpErrorMappings.ToReadError(styles.ErrorCode),
                styles.ErrorMessage ?? styles.ErrorCode ?? "List styles failed.");
        }

        bool? enabledFilter = where?.FirstOrDefault(clause => clause.Key == "style_enabled")?.Value switch
        {
            "true" => true,
            "false" => false,
            _ => null
        };

        List<McpCslStyleSummary> matching = styles.Value
            .Where(style => (enabledFilter is null || style.Enabled == enabledFilter.Value) &&
                            (style.StyleId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                             style.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        List<object> entries = matching.Skip(offset).Take(limit)
            .Select(style => BuildStyleSummaryEntry(style, longMode))
            .Cast<object>().ToList();
        bool hasMore = offset + entries.Count < matching.Count;
        string? continuation = hasMore
            ? EncodeCursor("patchouli://csl-styles/", query, literal, where, offset + entries.Count, null)
            : null;
        return new FindPage(entries, continuation, styles.Value.Count, matching.Count);
    }

    private async Task<FindPage> BrowseFileSingletonAsync(McpUriParseResult target, string? query, bool longMode,
        IReadOnlyList<McpWhereClause>? where, List<string> warnings, CancellationToken cancellationToken)
    {
        AddWarning(warnings, McpWarningCodes.FileUriSingletonScope);
        SingletonResource? singleton = await ResolveSingletonAsync(target, cancellationToken);
        if (singleton is null)
        {
            return new FindPage([], null, 1, 0);
        }

        if (!string.IsNullOrWhiteSpace(query) &&
            !singleton.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return new FindPage([], null, 1, 0);
        }

        bool keep = await MatchesResourceWhereAsync(singleton.ItemId, singleton.DocumentId, singleton.Citable,
            where, cancellationToken);
        object entry = longMode
            ? BuildLongEntryFromSingleton(singleton)
            : new McpFindEntry(singleton.Uri,
                singleton.Title, singleton.Type);
        List<object> entries = keep ? [entry] : [];

        // The document scope also discovers the fixed map.yml child: the chapter/pagination map
        // is always addressable (and first-put-able) even when no map is stored yet.
        if (target.Kind == McpUriKind.Document && target.DocumentId.HasValue && keep)
        {
            string mapUri = McpResourceUris.MapUri(target.DocumentId.Value);
            string mapTitle = singleton.Title + " — page map (map.yml)";
            if (string.IsNullOrWhiteSpace(query) ||
                mapTitle.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                entries.Add(new McpFindEntry(mapUri, mapTitle, "file"));
            }
        }

        return new FindPage(entries, null, 1, entries.Count);
    }

    private async Task<SingletonResource?> ResolveSingletonAsync(McpUriParseResult target,
        CancellationToken cancellationToken)
    {
        switch (target.Kind)
        {
            case McpUriKind.RunEndpoint:
            {
                McpServerSettings? permissions = await PermissionSettingsAsync(cancellationToken);
                return EndpointVisible(permissions, target.CanonicalUri)
                    ? new SingletonResource(target.CanonicalUri, "control", "endpoint", false)
                    : null;
            }

            case McpUriKind.Item:
            {
                Result<McpItemMetadataResponse> metadata = await _read.GetItemMetadataAsync(target.ItemId!.Value,
                    cancellationToken);
                if (metadata.IsFailure)
                {
                    return null;
                }

                Result<string> primaryStatus = await _read.GetPrimaryDocumentOcrIndexStatusAsync(target.ItemId.Value,
                    cancellationToken);
                string uri = McpResourceUris.ItemUri(target.ItemId.Value);
                return new SingletonResource(uri, metadata.Value.Title, "file",
                    IsCitableItem(metadata.Value.ItemType, metadata.Value.Title),
                    target.ItemId, ItemStatus: metadata.Value.Status ?? "unset",
                    PrimaryDocumentOcrIndexStatus: primaryStatus.IsSuccess
                        ? primaryStatus.Value
                        : "no_primary_document");
            }

            case McpUriKind.Document:
            {
                Result<McpDocumentOutlineResponse> outline = await _read.GetDocumentOutlineAsync(
                    target.DocumentId!.Value, cancellationToken);
                if (outline.IsFailure)
                {
                    return null;
                }

                return await BuildDocumentSingletonAsync(outline.Value, cancellationToken);
            }

            case McpUriKind.Page:
            {
                Result<McpDocumentOutlineResponse> outline = await _read.GetDocumentOutlineAsync(
                    target.DocumentId!.Value, cancellationToken);
                if (outline.IsFailure)
                {
                    return null;
                }

                McpDocumentPageRef? page = outline.Value.Pages.FirstOrDefault(candidate =>
                    candidate.PageIndex + 1 == target.PageIndex!.Value);
                if (page is null)
                {
                    return null;
                }

                SingletonResource document = await BuildDocumentSingletonAsync(outline.Value, cancellationToken);
                return document with
                {
                    Uri = McpResourceUris.PageUri(target.DocumentId.Value, target.PageIndex!.Value),
                    Type = "file",
                    DocumentId = target.DocumentId.Value
                };
            }

            case McpUriKind.DocumentMap:
            {
                // The map is discoverable even without a stored map; it is never citable.
                Result<McpDocumentOutlineResponse> outline = await _read.GetDocumentOutlineAsync(
                    target.DocumentId!.Value, cancellationToken);
                if (outline.IsFailure)
                {
                    return null;
                }

                return new SingletonResource(McpResourceUris.MapUri(target.DocumentId.Value),
                    (outline.Value.Title ?? target.DocumentId.Value.ToString()) + " — page map (map.yml)",
                    "file", false, outline.Value.ItemId, null, target.DocumentId.Value);
            }

            case McpUriKind.Style:
            {
                Result<McpCslStyleResponse> style = await _read.GetCslStyleAsync(target.StyleId!,
                    cancellationToken);
                if (style.IsFailure)
                {
                    return null;
                }

                return new SingletonResource(McpResourceUris.StyleUri(target.StyleId!), style.Value.DisplayName,
                    "file", false, StyleEnabled: style.Value.Enabled);
            }

            case McpUriKind.Evidence:
            {
                DocumentInstanceId documentId = target.DocumentId!.Value;
                int pageIndex = target.PageIndex!.Value;
                Result<EvidencePageText> evidence = await _evidenceReader.GetBoxTextAsync(
                    documentId, pageIndex, target.TreeRevisionId, target.BoxId, cancellationToken);
                if (evidence.IsFailure)
                {
                    return null;
                }

                string uri =
                    McpResourceUris.EvidencePageUri(documentId, pageIndex, target.TreeRevisionId, target.BoxId);
                Result<ItemId> owner = await _read.GetItemIdForDocumentAsync(documentId, cancellationToken);
                string? itemUri = owner.IsSuccess ? McpResourceUris.ItemUri(owner.Value) : null;
                string? itemStatus = null;
                string documentStatus = "missing_source";
                string sourceStatus = "unavailable";
                string ocrIndexStatus = "no_ocr";
                if (owner.IsSuccess)
                {
                    Result<McpItemMetadataResponse> metadata = await _read.GetItemMetadataAsync(owner.Value,
                        cancellationToken);
                    if (metadata.IsSuccess)
                    {
                        itemStatus = metadata.Value.Status ?? "unset";
                    }
                }

                Result<IReadOnlyList<McpTextResourceProjection>> projections =
                    await _read.GetTextResourceProjectionsAsync([documentId], cancellationToken: cancellationToken);
                McpTextResourceProjection? projection =
                    projections.IsSuccess ? projections.Value.SingleOrDefault() : null;
                if (projection is not null)
                {
                    documentStatus = projection.DocumentStatus;
                    sourceStatus = projection.SourceStatus;
                    ocrIndexStatus = projection.OcrIndexStatus;
                }

                bool citable = owner.IsSuccess;
                return new SingletonResource(uri, evidence.Value.SourceTitle ?? uri, "file",
                    citable, owner.IsSuccess ? owner.Value : null, itemUri,
                    documentId, itemStatus, documentStatus, sourceStatus,
                    OcrIndexStatus: ocrIndexStatus);
            }

            case McpUriKind.TranslationPage:
            {
                DocumentInstanceId documentId = target.DocumentId!.Value;
                int pageIndex = target.PageIndex!.Value;
                Result<McpDocumentOutlineResponse> outline = await _read.GetDocumentOutlineAsync(documentId,
                    cancellationToken);
                if (outline.IsFailure)
                {
                    return null;
                }

                McpDocumentPageRef? page = outline.Value.Pages.FirstOrDefault(candidate =>
                    candidate.PageIndex + 1 == pageIndex);
                if (page is null)
                {
                    return null;
                }

                Result<McpPageTranslationResponse> translation = await _read.GetPageTranslationAsync(
                    new McpPageTranslationRequest(documentId, page.PageId), cancellationToken);
                SingletonResource document = await BuildDocumentSingletonAsync(outline.Value, cancellationToken);
                return document with
                {
                    Uri = McpResourceUris.TranslationPageUri(documentId, pageIndex),
                    Type = "file",
                    DocumentId = documentId,
                    TranslationStatus = translation.IsSuccess
                        ? TranslationStatusName(translation.Value.Status)
                        : McpTranslationStatus.Untranslated,
                    TranslatedBoxCount = translation.IsSuccess ? translation.Value.Status.TranslatedBoxCount : 0,
                    TotalBoxCount = translation.IsSuccess ? translation.Value.Status.TotalBoxCount : 0
                };
            }

            case McpUriKind.Library:
            {
                Result<McpLibraryProjection> projection = await _read.GetLibraryProjectionAsync(
                    _exposeLibraryTags, _exposeLibraryCollections, cancellationToken);
                return projection.IsSuccess
                    ? new SingletonResource(McpResourceUris.LibraryUri(), projection.Value.DisplayName, "file", false,
                        IsLibrary: true)
                    : null;
            }

            case McpUriKind.RunOcrStatus:
            {
                if (_ocrRuns is null)
                {
                    return null;
                }

                Result<McpOcrTaskStatusProjection> task =
                    await _ocrRuns.TryGetTaskStatusAsync(target.RunTaskId!, cancellationToken);
                return task.IsSuccess
                    ? new SingletonResource(
                        McpResourceUris.OcrRunStatusUri(target.RunTaskId!),
                        string.IsNullOrWhiteSpace(task.Value.ItemTitle) ? target.RunTaskId! : task.Value.ItemTitle,
                        "file",
                        false)
                    : null;
            }

            case McpUriKind.RunTranslationStatus:
            {
                if (_translationRuns is null)
                {
                    return null;
                }

                Result<McpTranslationRunStatusProjection> run =
                    await _translationRuns.TryGetRunStatusAsync(target.RunTaskId!, cancellationToken);
                return run.IsSuccess
                    ? new SingletonResource(
                        McpResourceUris.TranslationRunStatusUri(target.RunTaskId!),
                        string.IsNullOrWhiteSpace(run.Value.DocumentId) ? target.RunTaskId! : run.Value.DocumentId,
                        "file",
                        false)
                    : null;
            }

            case McpUriKind.RunMetadataStatus:
            {
                if (_metadataRuns is null)
                {
                    return null;
                }

                Result<McpMetadataTaskStatusProjection> task =
                    await _metadataRuns.TryGetRunStatusAsync(target.RunTaskId!, cancellationToken);
                return task.IsSuccess
                    ? new SingletonResource(
                        McpResourceUris.MetadataRunStatusUri(target.RunTaskId!),
                        target.RunTaskId!,
                        "file",
                        false)
                    : null;
            }

            default:
                return null;
        }
    }

    private async Task<SingletonResource> BuildDocumentSingletonAsync(McpDocumentOutlineResponse outline,
        CancellationToken cancellationToken)
    {
        DocumentInstanceId documentId = outline.DocumentInstanceId;
        string uri = McpResourceUris.DocumentUri(documentId);
        bool citable = outline.ItemId is not null;
        string? itemUri = outline.ItemId is null ? null : McpResourceUris.ItemUri(outline.ItemId.Value);
        string? itemStatus = null;
        string documentStatus = "missing_source";
        string sourceStatus = "unavailable";
        string ocrIndexStatus = "no_ocr";
        if (outline.ItemId is { } itemId)
        {
            Result<McpItemMetadataResponse> metadata = await _read.GetItemMetadataAsync(itemId, cancellationToken);
            if (metadata.IsSuccess)
            {
                itemStatus = metadata.Value.Status ?? "unset";
            }
        }

        Result<IReadOnlyList<McpTextResourceProjection>> projections =
            await _read.GetTextResourceProjectionsAsync([documentId], cancellationToken: cancellationToken);
        McpTextResourceProjection? projection = projections.IsSuccess ? projections.Value.SingleOrDefault() : null;
        if (projection is not null)
        {
            documentStatus = projection.DocumentStatus;
            sourceStatus = projection.SourceStatus;
            ocrIndexStatus = projection.OcrIndexStatus;
        }

        return new SingletonResource(uri, outline.Title ?? documentId.ToString(), "directory", citable,
            outline.ItemId, itemUri, documentId, itemStatus,
            documentStatus, sourceStatus,
            OcrIndexStatus: ocrIndexStatus);
    }

    private static object BuildLongEntryFromSingleton(SingletonResource singleton)
    {
        return singleton.IsLibrary
            ? new McpLibraryLongEntry(singleton.Uri, singleton.Title, singleton.Type)
            : singleton.TranslationStatus is not null
                ? new McpTranslationPageEntry(singleton.Uri, singleton.Title, singleton.Type,
                    singleton.TranslationStatus, singleton.TranslatedBoxCount, singleton.TotalBoxCount)
                : singleton.StyleEnabled is { } styleEnabled
                    ? new McpStyleLongEntry(singleton.Uri, singleton.Title, singleton.Type, styleEnabled)
                    : singleton.DocumentId is not null
                        ? new McpTextLongEntry(singleton.Uri, singleton.Title, singleton.Type, singleton.ItemUri,
                            singleton.ItemStatus, singleton.DocumentStatus ?? "missing_source",
                            singleton.SourceStatus ?? "unavailable",
                            PrimaryDocumentOcrIndexState.FromValue(singleton.OcrIndexStatus).Value, singleton.Citable)
                        : new McpItemLongEntry(singleton.Uri, singleton.Title, singleton.Type,
                            singleton.ItemStatus ?? "unset",
                            PrimaryDocumentOcrIndexState.FromValue(singleton.PrimaryDocumentOcrIndexStatus).Value,
                            singleton.Citable);
    }

    private static string TranslationStatusName(McpTranslationStatusProjection status)
    {
        if (status.TranslatedBoxCount <= 0)
        {
            return McpTranslationStatus.Untranslated;
        }

        return status.TranslatedBoxCount >= status.TotalBoxCount
            ? McpTranslationStatus.Translated
            : McpTranslationStatus.Partial;
    }

    private static object BuildItemEntry(McpBrowseItemRow row, bool longMode)
    {
        string uri = McpResourceUris.ItemUri(row.ItemId);
        bool citable = IsCitableItem(row.ItemType, row.Title);
        if (!longMode)
        {
            return new McpFindEntry(uri, row.Title, "file");
        }

        return new McpItemLongEntry(uri, row.Title, "file", row.Status ?? "unset",
            PrimaryDocumentOcrIndexState.FromValue(row.PrimaryDocumentOcrIndexStatus).Value, citable);
    }

    private static object BuildDocumentEntryAsync(McpBrowseDocumentRow row, bool longMode)
    {
        string uri = McpResourceUris.DocumentUri(row.DocumentInstanceId);
        string title = row.Title ?? row.DocumentInstanceId.ToString();
        bool citable = row.ItemId is not null;
        string? itemUri = row.ItemId is null ? null : McpResourceUris.ItemUri(row.ItemId.Value);
        if (!longMode)
        {
            return new McpFindEntry(uri, title, "directory");
        }

        return new McpTextLongEntry(uri, title, "directory", itemUri, row.ItemStatus, row.DocumentStatus,
            row.SourceStatus, PrimaryDocumentOcrIndexState.FromValue(row.OcrIndexStatus).Value, row.Citable);
    }

    private static object BuildStyleEntry(McpBrowseStyleRow row, bool longMode)
    {
        string uri = McpResourceUris.StyleUri(row.StyleId);
        if (!longMode)
        {
            return new McpFindEntry(uri, row.DisplayName, "file");
        }

        return new McpStyleLongEntry(uri, row.DisplayName, "file", row.Enabled);
    }

    private static object BuildStyleSummaryEntry(McpCslStyleSummary style, bool longMode)
    {
        string uri = McpResourceUris.StyleUri(style.StyleId);
        if (!longMode)
        {
            return new McpFindEntry(uri, style.DisplayName, "file");
        }

        return new McpStyleLongEntry(uri, style.DisplayName, "file", style.Enabled);
    }

    private static object BuildTranslationDocumentEntry(McpTranslationDocumentRow row)
    {
        return new McpTranslationDocumentEntry(
            McpResourceUris.TranslationDocumentUri(row.DocumentInstanceId),
            row.Title ?? row.DocumentInstanceId.ToString(),
            "directory",
            row.PageCount,
            row.TranslatedPageCount,
            row.PartialPageCount,
            row.UntranslatedPageCount,
            row.StalePageCount);
    }

    private static object BuildTranslationPageEntry(McpTranslationPageRow row)
    {
        int oneBased = row.PageIndex + 1;
        string title = string.IsNullOrWhiteSpace(row.PageLabel) ? $"page {oneBased}" : row.PageLabel!;
        return new McpTranslationPageEntry(row.Uri, title, "file", row.Status, row.TranslatedBoxCount,
            row.TotalBoxCount);
    }

    private static object BuildEvidenceEntry(McpSearchPageResult page, McpMatchedUnit unit,
        McpTextResourceProjection projection, bool longMode)
    {
        int pageIndex = page.PageIndex + 1;
        string uri =
            McpResourceUris.EvidencePageUri(page.DocumentInstanceId, pageIndex, unit.TreeRevisionId, unit.BoxId);
        string title = page.ItemTitle;
        if (!longMode)
        {
            return new McpFindEntry(uri, title, "file");
        }

        string? itemUri = projection.ItemId is null ? null : McpResourceUris.ItemUri(projection.ItemId.Value);
        return new McpTextLongEntry(uri, title, "file", itemUri, projection.ItemStatus, projection.DocumentStatus,
            projection.SourceStatus, PrimaryDocumentOcrIndexState.FromValue(projection.OcrIndexStatus).Value,
            projection.Citable);
    }

    private async Task<bool> MatchesResourceWhereAsync(ItemId? itemId, DocumentInstanceId? documentId, bool citable,
        IReadOnlyList<McpWhereClause>? where, CancellationToken cancellationToken)
    {
        if (where is null || where.Count == 0)
        {
            return true;
        }

        foreach (McpWhereClause clause in where)
        {
            switch (clause.Key)
            {
                case "citable":
                {
                    bool want = string.Equals(clause.Value, "true", StringComparison.OrdinalIgnoreCase);
                    if (citable != want)
                    {
                        return false;
                    }

                    break;
                }

                case "item_id":
                    if (itemId is null ||
                        !string.Equals(itemId.Value.ToString(), clause.Value, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    break;

                case "item_type":
                case "item_status":
                    if (itemId is null)
                    {
                        return false;
                    }

                {
                    Result<McpItemMetadataResponse> metadata = await _read.GetItemMetadataAsync(itemId.Value,
                        cancellationToken);
                    if (metadata.IsFailure)
                    {
                        return false;
                    }

                    string actual = clause.Key == "item_type"
                        ? metadata.Value.ItemType
                        : metadata.Value.Status ?? "unset";
                    if (!string.Equals(actual, clause.Value, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    break;
                }

                case "document_status":
                case "source_status":
                case "ocr_index_status":
                    if (documentId is null)
                    {
                        return false;
                    }

                {
                    Result<IReadOnlyList<McpTextResourceProjection>> projections =
                        await _read.GetTextResourceProjectionsAsync([documentId.Value],
                            cancellationToken: cancellationToken);
                    McpTextResourceProjection? projection = projections.IsSuccess
                        ? projections.Value.SingleOrDefault()
                        : null;
                    if (projection is null)
                    {
                        return false;
                    }

                    string actual = clause.Key switch
                    {
                        "document_status" => projection.DocumentStatus,
                        "source_status" => projection.SourceStatus,
                        _ => PrimaryDocumentOcrIndexState.FromValue(projection.OcrIndexStatus).Value
                    };
                    if (!string.Equals(actual, clause.Value, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    break;
                }

                case "primary_document_ocr_index_status":
                    if (itemId is null)
                    {
                        return false;
                    }

                    Result<string> primaryStatus = await _read.GetPrimaryDocumentOcrIndexStatusAsync(itemId.Value,
                        cancellationToken);
                    if (primaryStatus.IsFailure || !string.Equals(primaryStatus.Value, clause.Value,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    break;

                case "tag":
                    if (itemId is null)
                    {
                        return false;
                    }

                    Result<McpItemMetadataResponse> tagMetadata = await _read.GetItemMetadataAsync(itemId.Value,
                        cancellationToken);
                    if (tagMetadata.IsFailure ||
                        !TagsContainExact(tagMetadata.Value.TagsJson, clause.Value.Trim()))
                    {
                        return false;
                    }

                    break;

                case "collection_id":
                    if (itemId is null)
                    {
                        return false;
                    }

                    Result<IReadOnlyList<CollectionId>> membership = await _read.GetItemCollectionIdsAsync(
                        itemId.Value, cancellationToken);
                    if (membership.IsFailure || !membership.Value.Any(id =>
                            string.Equals(id.ToString(), clause.Value.Trim(), StringComparison.Ordinal)))
                    {
                        return false;
                    }

                    break;

                default:
                    return false;
            }
        }

        return true;
    }

    private FindPage BrowseRoot(int limit, int offset, McpServerSettings? permissions)
    {
        McpFindEntry[] all =
        [
            new("patchouli://items/", "/items", "directory"),
            new("patchouli://texts/", "/texts", "directory"),
            new("patchouli://translations/", "/translations", "directory"),
            new("patchouli://csl-styles/", "/csl-styles", "directory"),
            new(McpResourceUris.RunsScopeUri(), "/runs", "directory"),
            new(McpResourceUris.LibraryUri(), "/library.yml", "file")
        ];
        all = all.Where(entry => Allowed(permissions, "find", entry.Uri)).ToArray();
        int from = Math.Clamp(offset, 0, all.Length);
        object[] page = all.Skip(from).Take(limit).Cast<object>().ToArray();
        string? continuation = from + page.Length < all.Length
            ? EncodeCursor(null, null, false, null, from + page.Length, null)
            : null;
        return new FindPage(page, continuation, all.Length, all.Length);
    }

    private Task<McpServerSettings?> PermissionSettingsAsync(CancellationToken cancellationToken)
    {
        return _permissionSettings is null
            ? Task.FromResult<McpServerSettings?>(null)
            : LoadPermissionsAsync(cancellationToken);
    }

    private async Task<McpServerSettings?> LoadPermissionsAsync(CancellationToken cancellationToken)
    {
        return await _permissionSettings!(cancellationToken).ConfigureAwait(false);
    }

    private static bool Allowed(McpServerSettings? settings, string verb, string? uri)
    {
        return settings is null || McpPermissionPolicy.Allows(settings, verb, uri);
    }

    private string? ValidateExposure(IReadOnlyList<McpWhereClause>? where)
    {
        if (where is null || where.Count == 0)
        {
            return null;
        }

        foreach (McpWhereClause clause in where)
        {
            if (clause.Key == "tag" && !_exposeLibraryTags)
            {
                return "tag filtering is disabled by the host's MCP exposure settings.";
            }

            if (clause.Key == "collection_id" && !_exposeLibraryCollections)
            {
                return "collection filtering is disabled by the host's MCP exposure settings.";
            }
        }

        return null;
    }

    private static string? ValidateScopeMatrix(McpUriKind kind, string? query, IReadOnlyList<McpWhereClause>? where)
    {
        if (kind == McpUriKind.Root)
        {
            if (!string.IsNullOrWhiteSpace(query))
            {
                return
                    "The root scope is discovery-only and accepts no query; choose a returned VFS directory before searching.";
            }

            if (where is { Count: > 0 })
            {
                return "The root scope is discovery-only and accepts no where filters.";
            }

            return null;
        }

        if (kind is McpUriKind.Library or McpUriKind.RunEndpoint)
        {
            if (!string.IsNullOrWhiteSpace(query))
            {
                return "The library projection resource is a singleton and does not accept a query.";
            }

            return where is { Count: > 0 }
                ? "The library projection resource does not accept where filters."
                : null;
        }

        IReadOnlyList<string>? allowed = kind switch
        {
            McpUriKind.ItemsScope =>
                new[]
                {
                    "item_type", "item_status", "primary_document_ocr_index_status", "citable", "tag", "collection_id"
                },
            McpUriKind.Item =>
                new[]
                {
                    "item_type", "item_status", "primary_document_ocr_index_status", "citable", "tag",
                    "collection_id"
                },
            McpUriKind.TextsScope or McpUriKind.Document or McpUriKind.Page or McpUriKind.Evidence
                or McpUriKind.DocumentMap =>
                new[]
                {
                    "item_id", "item_type", "item_status", "document_status", "source_status", "ocr_index_status",
                    "citable"
                },
            McpUriKind.TranslationsScope or McpUriKind.TranslationDocument or McpUriKind.TranslationPage =>
                new[]
                {
                    "item_id", "item_type", "item_status", "document_status", "source_status", "ocr_index_status",
                    "citable"
                },
            McpUriKind.StylesScope or McpUriKind.Style => new[] { "style_enabled" },
            McpUriKind.RunsScope or McpUriKind.RunsOcrScope or McpUriKind.RunsTranslationScope
                or McpUriKind.RunsMetadataScope => [],
            _ => null
        };
        if (allowed is null)
        {
            return "in must be a resource scope URI.";
        }

        foreach (McpWhereClause clause in where ?? [])
        {
            if (!allowed.Contains(clause.Key, StringComparer.Ordinal))
            {
                return $"Unsupported where key '{clause.Key}' for this scope.";
            }
        }

        return null;
    }

    private static bool IsFileScope(McpUriKind kind)
    {
        return kind is McpUriKind.Item or McpUriKind.Document or McpUriKind.Page or McpUriKind.Style
            or McpUriKind.Evidence or McpUriKind.TranslationPage or McpUriKind.Library
            or McpUriKind.RunOcrStatus or McpUriKind.RunTranslationStatus or McpUriKind.RunMetadataStatus
            or McpUriKind.DocumentMap or McpUriKind.RunEndpoint;
    }

    private static IReadOnlyList<McpWhereClause>? NormalizeWhere(IReadOnlyList<McpWhereClause>? where,
        List<string>? warnings)
    {
        if (where is null || where.Count == 0)
        {
            return where;
        }

        Dictionary<string, string> map = new(StringComparer.Ordinal);
        foreach (McpWhereClause clause in where)
        {
            if (map.ContainsKey(clause.Key) && warnings is not null)
            {
                AddWarning(warnings, McpWarningCodes.DuplicateWhereKeyLastWins);
            }

            map[clause.Key] = clause.Value;
        }

        return map.Select(pair => new McpWhereClause(pair.Key, pair.Value)).ToArray();
    }

    private static bool CursorConflicts(string? requestScope, string? requestQuery, bool requestLiteral,
        IReadOnlyList<McpWhereClause>? requestWhere, McpCursor cursor)
    {
        bool scopeConflict = !string.Equals(requestScope ?? string.Empty, cursor.Scope ?? string.Empty,
            StringComparison.Ordinal);
        bool queryConflict = !string.Equals(requestQuery ?? string.Empty, cursor.Query ?? string.Empty,
            StringComparison.Ordinal);
        bool literalConflict = requestLiteral != cursor.Literal;
        bool whereConflict = !WhereEqual(requestWhere, cursor.Where);
        return scopeConflict || queryConflict || literalConflict || whereConflict;
    }

    private static bool WhereEqual(IReadOnlyList<McpWhereClause>? left, IReadOnlyList<McpWhereClause>? right)
    {
        Dictionary<string, string> leftMap = (left ?? []).ToDictionary(clause => clause.Key, clause => clause.Value,
            StringComparer.Ordinal);
        Dictionary<string, string> rightMap = (right ?? []).ToDictionary(clause => clause.Key, clause => clause.Value,
            StringComparer.Ordinal);
        if (leftMap.Count != rightMap.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, string> pair in leftMap)
        {
            if (!rightMap.TryGetValue(pair.Key, out string? value) ||
                !string.Equals(value, pair.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string? NormalizeIn(string? inScope)
    {
        if (inScope is null || string.Equals(inScope, "patchouli://", StringComparison.Ordinal))
        {
            return null;
        }

        return McpResourceUris.Normalize(inScope);
    }

    private static string EncodeCursor(string? scope, string? query, bool literal,
        IReadOnlyList<McpWhereClause>? where, int offset, string? searchCursor)
    {
        return McpCursor.Encode(scope, query, literal, where, offset, searchCursor);
    }

    private static bool IsCitableItem(string itemType, string? title)
    {
        return !string.Equals(itemType, "general", StringComparison.Ordinal) ||
               !string.IsNullOrWhiteSpace(title);
    }

    /// <summary>Exact, case-sensitive tag membership test over an Item's JSON tag array.</summary>
    private static bool TagsContainExact(string tagsJson, string tag)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(tagsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String &&
                    string.Equals(element.GetString(), tag, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void AddWarning(List<string> warnings, string code)
    {
        string line = McpWarningCodes.ToTerminalLine(code);
        if (!warnings.Contains(line, StringComparer.Ordinal))
        {
            warnings.Add(line);
        }
    }

    private static string TakeSafePrefix(string text, int maximumCharacters)
    {
        if (maximumCharacters >= text.Length)
        {
            return text;
        }

        int length = Math.Max(0, maximumCharacters);
        if (length > 0 && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        int newline = text.LastIndexOf('\n', Math.Max(0, length - 1));
        return newline >= 0 ? text[..(newline + 1)] : text[..length];
    }

    private static int CountLines(string text)
    {
        return text.Length == 0
            ? 0
            : text.Count(character => character == '\n') +
              (text.EndsWith('\n') ? 0 : 1);
    }

    private static string ApplyLines(string text, string? range, string expectedKind)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            return text;
        }

        if (!TryParseRange(range, out string kind, out int start, out int end) || kind != expectedKind)
        {
            return text;
        }

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int from = Math.Clamp(start, 1, lines.Length);
        int to = Math.Clamp(end, from, lines.Length);
        return string.Join("\n", lines[(from - 1)..to]);
    }

    private static string? ValidateRange(string? range, string expectedKind)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            return null;
        }

        if (!TryParseRange(range, out string kind, out _, out _))
        {
            return $"Invalid range '{range}'; expected {expectedKind}:S-E with S>=1 and E>=S.";
        }

        if (kind != expectedKind)
        {
            return $"Range kind '{kind}' is not valid for this resource; expected {expectedKind}.";
        }

        return null;
    }

    private static bool TryParseRange(string? range, out string kind, out int start, out int end)
    {
        kind = string.Empty;
        start = 0;
        end = 0;
        if (string.IsNullOrWhiteSpace(range))
        {
            return false;
        }

        string[] parts = range.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts[0] is not ("lines" or "pages" or "boxes"))
        {
            return false;
        }

        string[] bounds = parts[1].Split('-', 2, StringSplitOptions.TrimEntries);
        if (bounds.Length != 2 || !int.TryParse(bounds[0], out start) || !int.TryParse(bounds[1], out end) ||
            start < 1 || end < start)
        {
            return false;
        }

        kind = parts[0];
        return true;
    }

    private sealed record SingletonResource(
        string Uri,
        string Title,
        string Type,
        bool Citable,
        ItemId? ItemId = null,
        string? ItemUri = null,
        DocumentInstanceId? DocumentId = null,
        string? ItemStatus = null,
        string? DocumentStatus = null,
        string? SourceStatus = null,
        bool? StyleEnabled = null,
        string PrimaryDocumentOcrIndexStatus = "no_primary_document",
        string OcrIndexStatus = "no_ocr",
        bool IsLibrary = false,
        string? TranslationStatus = null,
        int TranslatedBoxCount = 0,
        int TotalBoxCount = 0);

    private sealed record FindPage(
        IReadOnlyList<object> Entries,
        string? Continuation,
        int DomainTotal,
        int FilteredTotal,
        McpToolError? Error = null)
    {
        public bool HasError => Error is not null;

        public static FindPage Failed(McpErrorCode code, string message)
        {
            return new FindPage([], null, 0, 0, McpToolError.From(code, message));
        }
    }

    private sealed record McpCursor(
        int Version,
        string? Scope,
        string? Query,
        bool Literal,
        IReadOnlyList<McpWhereClause>? Where,
        int Offset,
        string? SearchCursor)
    {
        public static string Encode(string? scope, string? query, bool literal,
            IReadOnlyList<McpWhereClause>? where, int offset, string? searchCursor)
        {
            McpCursor cursor = new(1, scope, query, literal, where, offset, searchCursor);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cursor)));
        }

        public static McpCursor? TryDecode(string token)
        {
            try
            {
                string json = Encoding.UTF8.GetString(Convert.FromBase64String(token));
                McpCursor? decoded = JsonSerializer.Deserialize<McpCursor>(json);
                if (decoded is null || decoded.Version != 1 || decoded.Offset < 0)
                {
                    return null;
                }

                return decoded;
            }
            catch (JsonException)
            {
                return null;
            }
            catch (FormatException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }
    }
}
