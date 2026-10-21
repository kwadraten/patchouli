using System.Reflection;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Documents;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Core.View;
using Patchouli.Mcp;

namespace Patchouli.Tests;

/// <summary>
/// Issue #11: MCP add/view command surface — schema validation, tool switches,
/// domain permissions, error-code mapping and the view image envelope.
/// Uses DispatchProxy stubs for the wide read/write/item/biblatex/evidence
/// surfaces that add/view never touch; IFileImportService and IPageImageViewService
/// are configured fakes.
/// </summary>
public sealed class McpAddViewCommandTests
{
    private static readonly byte[] FakePng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private const string DocId = "11111111-1111-1111-1111-111111111111";
    private const string RevId = "22222222-2222-2222-2222-222222222222";
    private const string BoxGuid = "33333333-3333-3333-3333-333333333333";

    // ---------- add ----------

    [Fact]
    public async Task Add_rejects_empty_path()
    {
        McpCommandService service = CreateService();

        McpCommandResult<McpAddMeta, McpAddResult> result = await service.AddAsync(new McpAddRequest("  "));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be((int)McpErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task Add_disabled_tool_switch_denies()
    {
        McpCommandService service = CreateService(addEnabled: false);

        McpCommandResult<McpAddMeta, McpAddResult> result = await service.AddAsync(new McpAddRequest("/tmp/paper.pdf"));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be((int)McpErrorCode.PermissionDenied);
    }

    [Fact]
    public async Task Add_without_host_import_service_is_unavailable()
    {
        McpCommandService service = CreateService(null);

        McpCommandResult<McpAddMeta, McpAddResult> result = await service.AddAsync(new McpAddRequest("/tmp/paper.pdf"));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be((int)McpErrorCode.Unavailable);
    }

    [Fact]
    public async Task Add_rejects_url_with_invalid_argument()
    {
        (_, Stub<IFileImportService> import) = Stub<IFileImportService>.Create();
        import.Handle(nameof(IFileImportService.ImportAsync), _ =>
            Task.FromResult(Result<FileImportResult>.Failure(
                FileImportErrorCodes.UrlNotAllowed, "URLs are not accepted.")));
        McpCommandService service = CreateService(import.Proxy);

        McpCommandResult<McpAddMeta, McpAddResult> result =
            await service.AddAsync(new McpAddRequest("https://example.com/p.pdf"));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be((int)McpErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task Add_success_maps_uris_issues_and_body_status()
    {
        (_, Stub<IFileImportService> import) = Stub<IFileImportService>.Create();
        FileImportResult imported = new(true, LocalImportKind.Pdf, false, true,
            "patchouli://items/item-1.yml", "patchouli://texts/" + DocId + "/", null, 12,
            "imported", "ingested",
            [new FileImportIssue("moved_to_search_root", "moved")], null);
        import.Handle(nameof(IFileImportService.ImportAsync),
            _ => Task.FromResult(Result<FileImportResult>.Success(imported)));
        McpCommandService service = CreateService(import.Proxy);

        McpCommandResult<McpAddMeta, McpAddResult> result = await service.AddAsync(new McpAddRequest("/tmp/paper.pdf"));

        result.IsSuccess.Should().BeTrue();
        McpAddResult entry = result.Envelope!.Entries.Should().ContainSingle().Subject;
        entry.Kind.Should().Be("pdf");
        entry.ItemUri.Should().Be("patchouli://items/item-1.yml");
        entry.TextUri.Should().Be("patchouli://texts/" + DocId + "/");
        entry.StyleUri.Should().BeNull();
        entry.Moved.Should().BeTrue();
        entry.BodyStatus.Should().Be("ingested");
        entry.Issues.Should().ContainSingle(i => i.Code == "moved_to_search_root");
    }

    [Fact]
    public async Task Add_denies_pdf_without_items_and_texts_add_permission()
    {
        (_, Stub<IFileImportService> import) = Stub<IFileImportService>.Create();
        FileImportRequest? seen = null;
        import.Handle(nameof(IFileImportService.ImportAsync), args =>
        {
            seen = (FileImportRequest)args[0]!;
            // Simulate the service running the authorization gate.
            return seen.AuthorizeKindAsync!(LocalImportKind.Pdf)
                .ContinueWith(t => t.Result.IsFailure
                    ? Result<FileImportResult>.Failure(t.Result.ErrorCode!, t.Result.ErrorMessage!)
                    : Result<FileImportResult>.Success(
                        new FileImportResult(true, LocalImportKind.Pdf, false, false,
                            "patchouli://items/i.yml", "patchouli://texts/d/", null, 1,
                            "imported", "ingested", [], null)));
        });
        McpServerSettings permissions = new(0, "127.0.0.1", false, [], false, null, [],
            DateTimeOffset.UtcNow)
        {
            DomainPermissions = [new McpDomainPermission("items", "add", false)]
        };
        McpCommandService service = CreateService(import.Proxy,
            permissions: _ => Task.FromResult(permissions));

        McpCommandResult<McpAddMeta, McpAddResult> result = await service.AddAsync(new McpAddRequest("/tmp/paper.pdf"));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be((int)McpErrorCode.PermissionDenied);
    }

    [Fact]
    public async Task Add_maps_import_errors_to_stable_mcp_codes()
    {
        (_, Stub<IFileImportService> import) = Stub<IFileImportService>.Create();
        import.Handle(nameof(IFileImportService.ImportAsync), _ =>
            Task.FromResult(Result<FileImportResult>.Failure(
                FileImportErrorCodes.StyleConflict, "conflict")));
        McpCommandService service = CreateService(import.Proxy);

        McpCommandResult<McpAddMeta, McpAddResult> result =
            await service.AddAsync(new McpAddRequest("/tmp/style.csl", "csl"));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be((int)McpErrorCode.RevisionConflict);
    }

    [Fact]
    public async Task Add_failure_never_reports_success_envelope_entries()
    {
        (_, Stub<IFileImportService> import) = Stub<IFileImportService>.Create();
        import.Handle(nameof(IFileImportService.ImportAsync), _ =>
            Task.FromResult(Result<FileImportResult>.Failure(
                FileImportErrorCodes.PathNotFound, "missing")));
        McpCommandService service = CreateService(import.Proxy);

        McpCommandResult<McpAddMeta, McpAddResult> result =
            await service.AddAsync(new McpAddRequest("/tmp/missing.pdf"));

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be((int)McpErrorCode.NotFound);
    }

    // ---------- view ----------

    [Fact]
    public async Task View_rejects_empty_uri()
    {
        McpCommandService service = CreateService();

        McpViewCommandResult result = await service.ViewAsync(new McpViewRequest(""));

        result.Result.IsSuccess.Should().BeFalse();
        result.Result.Error!.Code.Should().Be((int)McpErrorCode.InvalidArgument);
        result.PngBytes.Should().BeNull();
    }

    [Fact]
    public async Task View_disabled_tool_switch_denies()
    {
        McpCommandService service = CreateService(viewEnabled: false);

        McpViewCommandResult result =
            await service.ViewAsync(new McpViewRequest("patchouli://texts/" + DocId + "/page-1.xml"));

        result.Result.IsSuccess.Should().BeFalse();
        result.Result.Error!.Code.Should().Be((int)McpErrorCode.PermissionDenied);
    }

    [Fact]
    public async Task View_document_uri_requires_page_specification()
    {
        (_, Stub<IPageImageViewService> docPageView) = Stub<IPageImageViewService>.Create();
        McpCommandService service = CreateService(pageView: docPageView.Proxy);

        McpViewCommandResult result =
            await service.ViewAsync(new McpViewRequest("patchouli://texts/" + DocId + "/"));

        result.Result.IsSuccess.Should().BeFalse();
        result.Result.Error!.Code.Should().Be((int)McpErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task View_rejects_unknown_context()
    {
        (_, Stub<IPageImageViewService> ctxPageView) = Stub<IPageImageViewService>.Create();
        McpCommandService service = CreateService(pageView: ctxPageView.Proxy);

        McpViewCommandResult result = await service.ViewAsync(
            new McpViewRequest("patchouli://texts/" + DocId + "/page-1.xml", "thumbnail"));

        result.Result.IsSuccess.Should().BeFalse();
        result.Result.Error!.Code.Should().Be((int)McpErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task View_denies_without_texts_view_permission()
    {
        (_, Stub<IPageImageViewService> pageView) = Stub<IPageImageViewService>.Create();
        McpServerSettings permissions = new(0, "127.0.0.1", false, [], false, null, [],
            DateTimeOffset.UtcNow)
        {
            DomainPermissions = [new McpDomainPermission("texts", "view", false)]
        };
        McpCommandService service = CreateService(pageView: pageView.Proxy,
            permissions: _ => Task.FromResult(permissions));

        McpViewCommandResult result =
            await service.ViewAsync(new McpViewRequest("patchouli://texts/" + DocId + "/page-1.xml"));

        result.Result.IsSuccess.Should().BeFalse();
        result.Result.Error!.Code.Should().Be((int)McpErrorCode.PermissionDenied);
    }

    [Fact]
    public async Task View_success_returns_metadata_envelope_and_native_png_bytes()
    {
        (_, Stub<IPageImageViewService> pageView) = Stub<IPageImageViewService>.Create();
        PageImageViewResult image = new(
            "patchouli://texts/" + DocId + "/page-2.xml?rev=rev-7",
            PageImageViewTypes.Page, "image/png", 1200, 1600,
            new NormalizedBBox(0, 0, 1, 1), 0, "rev-7", FakePng);
        PageImageViewRequest? seen = null;
        pageView.Handle(nameof(IPageImageViewService.ViewAsync), args =>
        {
            seen = (PageImageViewRequest)args[0]!;
            return Task.FromResult(Result<PageImageViewResult>.Success(image));
        });
        McpCommandService service = CreateService(pageView: pageView.Proxy);

        McpViewCommandResult result =
            await service.ViewAsync(new McpViewRequest("patchouli://texts/" + DocId + "/page-2.xml"));

        result.Result.IsSuccess.Should().BeTrue();
        result.PngBytes.Should().BeEquivalentTo(FakePng);
        McpViewImageMetadata metadata =
            result.Result.Envelope!.Entries.Should().ContainSingle().Subject;
        metadata.Uri.Should().Be("patchouli://texts/" + DocId + "/page-2.xml?rev=rev-7");
        metadata.ViewType.Should().Be("page");
        metadata.MimeType.Should().Be("image/png");
        metadata.Width.Should().Be(1200);
        metadata.Height.Should().Be(1600);
        metadata.Crop.Should().BeEquivalentTo([0.0, 0.0, 1.0, 1.0]);
        metadata.ImageIndex.Should().Be(0);
        // The page index in the request is the one-based physical PDF page index.
        seen!.PageIndex1Based.Should().Be(2);
        seen.Context.Should().Be(PageImageViewContext.Page);
        seen.MaxEdge.Should().Be(PageImageViewRequest.DefaultMaxEdge);
    }

    [Fact]
    public async Task View_box_uri_with_region_context_requests_region_crop()
    {
        (_, Stub<IPageImageViewService> pageView) = Stub<IPageImageViewService>.Create();
        PageImageViewResult image = new(
            "patchouli://texts/" + DocId + "/page-1.xml?rev=" + RevId + "&box=" + BoxGuid + "",
            PageImageViewTypes.Box, "image/png", 400, 300,
            new NormalizedBBox(0.1, 0.2, 0.3, 0.4), 0, "rev-1", FakePng);
        PageImageViewRequest? seen = null;
        pageView.Handle(nameof(IPageImageViewService.ViewAsync), args =>
        {
            seen = (PageImageViewRequest)args[0]!;
            return Task.FromResult(Result<PageImageViewResult>.Success(image));
        });
        McpCommandService service = CreateService(pageView: pageView.Proxy);

        McpViewCommandResult result = await service.ViewAsync(new McpViewRequest(
            "patchouli://texts/" + DocId + "/page-1.xml?rev=" + RevId + "&box=" + BoxGuid + "", "region"));

        result.Result.IsSuccess.Should().BeTrue();
        seen!.Context.Should().Be(PageImageViewContext.Region);
        seen.BoxId.Should().NotBeNull();
        result.Result.Envelope!.Entries.Single().ViewType.Should().Be("box");
    }

    [Fact]
    public async Task View_translation_page_resolves_to_source_image_request()
    {
        (_, Stub<IPageImageViewService> pageView) = Stub<IPageImageViewService>.Create();
        PageImageViewResult image = new(
            "patchouli://translations/" + DocId + "/page-1.xml?rev=rev-1",
            PageImageViewTypes.TranslationPage, "image/png", 1200, 1600,
            new NormalizedBBox(0, 0, 1, 1), 0, "rev-1", FakePng);
        PageImageViewRequest? seen = null;
        pageView.Handle(nameof(IPageImageViewService.ViewAsync), args =>
        {
            seen = (PageImageViewRequest)args[0]!;
            return Task.FromResult(Result<PageImageViewResult>.Success(image));
        });
        McpCommandService service = CreateService(pageView: pageView.Proxy);

        McpViewCommandResult result = await service.ViewAsync(
            new McpViewRequest("patchouli://translations/" + DocId + "/page-1.xml"));

        result.Result.IsSuccess.Should().BeTrue();
        seen!.IsTranslation.Should().BeTrue();
        result.Result.Envelope!.Entries.Single().ViewType.Should().Be("translation_page");
    }

    [Fact]
    public async Task View_maps_stale_revision_to_not_found_not_silent_latest()
    {
        (_, Stub<IPageImageViewService> pageView) = Stub<IPageImageViewService>.Create();
        pageView.Handle(nameof(IPageImageViewService.ViewAsync), _ =>
            Task.FromResult(Result<PageImageViewResult>.Failure(
                PageImageViewErrorCodes.StaleRevision, "no such revision")));
        McpCommandService service = CreateService(pageView: pageView.Proxy);

        McpViewCommandResult result = await service.ViewAsync(new McpViewRequest(
            "patchouli://texts/" + DocId + "/page-1.xml?rev=44444444-4444-4444-4444-444444444444"));

        result.Result.IsSuccess.Should().BeFalse();
        result.Result.Error!.Code.Should().Be((int)McpErrorCode.NotFound);
        result.PngBytes.Should().BeNull();
    }

    [Fact]
    public async Task View_maps_source_changed_to_revision_conflict()
    {
        (_, Stub<IPageImageViewService> pageView) = Stub<IPageImageViewService>.Create();
        pageView.Handle(nameof(IPageImageViewService.ViewAsync), _ =>
            Task.FromResult(Result<PageImageViewResult>.Failure(
                PageImageViewErrorCodes.SourceChanged, "fingerprint changed")));
        McpCommandService service = CreateService(pageView: pageView.Proxy);

        McpViewCommandResult result =
            await service.ViewAsync(new McpViewRequest("patchouli://texts/" + DocId + "/page-1.xml"));

        result.Result.IsSuccess.Should().BeFalse();
        result.Result.Error!.Code.Should().Be((int)McpErrorCode.RevisionConflict);
        result.PngBytes.Should().BeNull();
    }

    // ---------- harness ----------

    private static McpCommandService CreateService(
        IFileImportService? fileImport = null,
        IPageImageViewService? pageView = null,
        bool addEnabled = true,
        bool viewEnabled = true,
        Func<CancellationToken, Task<McpServerSettings>>? permissions = null)
    {
        (_, Stub<IMcpReadApi> read) = Stub<IMcpReadApi>.Create();
        read.Handle(nameof(IMcpReadApi.GetCurrentLibraryStateAsync), _ =>
            Task.FromResult(Result<McpLibraryStateResponse>.Success(
                new McpLibraryStateResponse("lib", "lib:99"))));
        (_, Stub<IMcpWriteApi> write) = Stub<IMcpWriteApi>.Create();
        (_, Stub<IBiblatexImportService> biblatex) = Stub<Core.Bibliography.Biblatex.IBiblatexImportService>.Create();
        (_, Stub<IItemService> items) = Stub<Core.Bibliography.IItemService>.Create();
        (_, Stub<IVersionedEvidenceReader> evidence) = Stub<Core.Documents.IVersionedEvidenceReader>.Create();
        return new McpCommandService(read.Proxy, write.Proxy, biblatex.Proxy, items.Proxy,
            evidence.Proxy, permissionSettings: permissions, fileImport: fileImport,
            pageView: pageView, addEnabled: addEnabled, viewEnabled: viewEnabled);
    }

    /// <summary>
    /// Minimal DispatchProxy stub: unhandled methods throw loudly.
    /// Non-sealed because DispatchProxy generates a subclass at runtime.
    /// </summary>
    private class Stub<T> : DispatchProxy where T : class
    {
        private readonly Dictionary<string, Func<object?[], object?>> _handlers = new();

        public T Proxy => (T)(object)this;

        public void Handle(string methodName, Func<object?[], object?> handler)
        {
            _handlers[methodName] = handler;
        }

        public static (T Proxy, Stub<T> Impl) Create()
        {
            T proxy = Create<T, Stub<T>>();
            return (proxy, (Stub<T>)(object)proxy);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is not null &&
                _handlers.TryGetValue(targetMethod.Name, out Func<object?[], object?>? handler))
            {
                return handler(args ?? []);
            }

            throw new NotSupportedException(
                $"Stub method not configured: {targetMethod?.DeclaringType?.Name}.{targetMethod?.Name}");
        }
    }
}
