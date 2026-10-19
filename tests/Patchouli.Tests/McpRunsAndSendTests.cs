using System.Text.Json;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Mcp;
using Patchouli.Core.Mcp;
using Patchouli.Infrastructure.Mcp;

namespace Patchouli.Tests;

/// <summary>
///     In-process contract tests for the S4 <c>runs/</c> projections and the <c>send</c> verb over
///     <see cref="McpCommandService" /> (no network). Covers V3-AC10 root discovery (domain
///     directories plus one file), V3-AC26 (volatile runs/ without library_revision advancement),
///     and V3-AC28 (text-only data boundary, user tool switches returning the surface to read-only).
/// </summary>
public sealed class McpRunsAndSendTests
{
    [Fact]
    public async Task Domain_permissions_gate_every_verb_and_filter_root_discovery()
    {
        McpServerSettings settings = McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow) with
        {
            DomainPermissions =
            [
                new McpDomainPermission("texts", "find", false), new McpDomainPermission("texts", "fetch", false),
                new McpDomainPermission("translations", "put", false), new McpDomainPermission("items", "cite", false),
                new McpDomainPermission("runs", "ocr.cancel", false)
            ]
        };
        McpServerSettings[] currentSettings = [settings];
        StubWriteApi writes = new();
        McpCommandService commands = CreateService(new StubReadApi(), write: writes,
            permissions: _ => Task.FromResult(currentSettings[0]));
        (await commands.FindAsync(new McpFindRequest(null, "patchouli://texts/", null))).Error!.Name.Should()
            .Be("PERMISSION_DENIED");
        (await commands.FetchAsync(new McpFetchRequest(["patchouli://library.toon", "patchouli://texts/doc/page-1.md"],
                null, null)))
            .Error!.Name.Should().Be("PERMISSION_DENIED");
        (await commands.PutAsync(new McpPutRequest("patchouli://translations/doc/page-1.md", "text"))).Error!.Name
            .Should().Be("PERMISSION_DENIED");
        (await commands.CiteAsync(new McpCiteRequest(["patchouli://items/item.bib"], null, null, false, false))).Error!
            .Name.Should().Be("PERMISSION_DENIED");
        (await commands.SendAsync(new McpActionSendRequest("patchouli://texts/11111111-1111-1111-1111-111111111111/",
                "ocr.start", JsonSerializer.SerializeToElement(new { })))).Error!.Name.Should()
            .Be("PERMISSION_DENIED");
        (await commands.SendAsync(new McpActionSendRequest("patchouli://runs/ocr/22222222-2222-2222-2222-222222222222/",
                "ocr.cancel", JsonSerializer.SerializeToElement(new { }))))
            .Error!.Name.Should().Be("PERMISSION_DENIED");
        writes.CallCount.Should().Be(0);
        McpCommandResult<McpFindMeta, object> root = await commands.FindAsync(new McpFindRequest(null, null, null));
        root.Envelope!.Entries.Cast<McpFindEntry>().Select(entry => entry.Uri).Should()
            .NotContain("patchouli://texts/");
        // The same built-in command service consults the latest settings for every operation.
        currentSettings[0] = settings with { DomainPermissions = [] };
        (await commands.FetchAsync(new McpFetchRequest(["patchouli://library.toon"], null, null))).IsSuccess.Should()
            .BeTrue();
    }

    [Fact]
    public async Task Cursor_restored_domain_is_rechecked_after_permission_revocation()
    {
        McpServerSettings settings = McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow);
        McpServerSettings[] currentSettings = [settings];
        StubOcrRunsApi ocr = new();
        ocr.SetTask("task-1", "queued");
        ocr.SetTask("task-2", "queued");
        McpCommandService commands = CreateService(new StubReadApi(), ocr,
            permissions: _ => Task.FromResult(currentSettings[0]));
        McpCommandResult<McpFindMeta, object> first =
            await commands.FindAsync(new McpFindRequest(null, McpResourceUris.RunsOcrScopeUri(), null, Limit: 1));
        first.Envelope!.Continuation.Should().NotBeNull();
        currentSettings[0] = settings with { DomainPermissions = [new McpDomainPermission("runs", "find", false)] };
        (await commands.FindAsync(new McpFindRequest(null, "patchouli://items/", null,
                Cursor: first.Envelope.Continuation)))
            .Error!.Name.Should().Be("PERMISSION_DENIED");
    }

    [Fact]
    public async Task Root_discovery_lists_action_and_existing_domain_directories_and_library_file()
    {
        McpCommandService commands = CreateService(new StubReadApi());

        McpCommandResult<McpFindMeta, object> result =
            await commands.FindAsync(new McpFindRequest(null, null, null));

        result.IsSuccess.Should().BeTrue();
        result.Envelope!.Continuation.Should().BeNull();
        result.Envelope.Message.Should().BeNull();
        List<McpFindEntry> entries = result.Envelope.Entries.Cast<McpFindEntry>().ToList();
        entries.Should().HaveCount(7);
        entries.Count(entry => entry.Type == "directory").Should().Be(6);
        entries.Count(entry => entry.Type == "file").Should().Be(1);
        entries.Select(entry => entry.Uri).Should().BeEquivalentTo(
            "patchouli://items/",
            "patchouli://texts/",
            "patchouli://translations/",
            "patchouli://csl-styles/",
            "patchouli://runs/",
            "patchouli://actions/",
            "patchouli://library.yml");
        result.Envelope.Meta.DomainTotal.Should().Be(7);
        result.Envelope.Meta.FilteredTotal.Should().Be(7);
        result.Envelope.Meta.ShownTotal.Should().Be(7);
    }

    [Fact]
    public async Task Runs_browse_lists_ocr_and_translation_subtrees()
    {
        StubOcrRunsApi ocr = new();
        ocr.SetTask("task-1", "queued");
        StubTranslationRunsApi translation = new();
        translation.SetRun("44444444-4444-4444-4444-444444444444", "running", 1);
        McpCommandService commands = CreateService(new StubReadApi(), ocr, translation: translation);

        McpCommandResult<McpFindMeta, object> runs = await commands.FindAsync(
            new McpFindRequest(null, "patchouli://runs/", null));
        runs.Envelope!.Entries.Cast<McpFindEntry>().Select(entry => entry.Uri).Should().Equal(
            "patchouli://runs/ocr/", "patchouli://runs/translation/");

        McpCommandResult<McpFindMeta, object> tasks = await commands.FindAsync(
            new McpFindRequest(null, "patchouli://runs/ocr/", null));
        tasks.Envelope!.Entries.Cast<McpFindEntry>().Should().ContainSingle()
            .Which.Uri.Should().Be(McpResourceUris.OcrRunStatusUri("task-1"));

        McpCommandResult<McpFindMeta, object> translationRuns = await commands.FindAsync(
            new McpFindRequest(null, McpResourceUris.RunsTranslationScopeUri(), null));
        translationRuns.Envelope!.Entries.Cast<McpFindEntry>().Should().ContainSingle()
            .Which.Uri.Should().Be(
                McpResourceUris.TranslationRunStatusUri("44444444-4444-4444-4444-444444444444"));
    }

    [Fact]
    public async Task Runs_ocr_status_fetch_projects_queue_state()
    {
        StubOcrRunsApi ocr = new();
        ocr.SetTask("task-1", "processing", 2, 5);
        McpCommandService commands = CreateService(new StubReadApi(), ocr);

        McpCommandResult<McpFetchMeta, McpFetchResult> result = await commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.OcrRunStatusUri("task-1")], null, null));

        McpFetchResult entry = result.Envelope!.Entries.Should().ContainSingle().Subject;
        entry.ResourceType.Should().Be("run_status");
        using JsonDocument content = JsonDocument.Parse(McpToonCodec.DecodeToJson(entry.Content!));
        content.RootElement.GetProperty("task_id").GetString().Should().Be("task-1");
        content.RootElement.GetProperty("state").GetString().Should().Be("processing");
        content.RootElement.GetProperty("page_progress").GetProperty("succeeded").GetInt32().Should().Be(2);

        McpCommandResult<McpFetchMeta, McpFetchResult> missing = await commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.OcrRunStatusUri("unknown-task")], null, null));
        McpToolError.TryGetCode(missing.Envelope!.Entries[0].Error, out McpErrorCode code).Should().BeTrue();
        code.Should().Be(McpErrorCode.NotFound);
    }

    [Fact]
    public async Task Runs_translation_status_fetch_and_scope_listing_project_page_outcomes()
    {
        const string RunId = "44444444-4444-4444-4444-444444444444";
        StubTranslationRunsApi translation = new();
        translation.SetRun(RunId, "running", 2, 1,
            [new McpTranslationPageStatusProjection(1, "committed", 4)]);
        McpCommandService commands = CreateService(new StubReadApi(), translation: translation);

        McpResourceUris.Parse(McpResourceUris.TranslationRunStatusUri(RunId)).Value.Kind.Should()
            .Be(McpUriKind.RunTranslationStatus);
        McpResourceUris.Parse(McpResourceUris.RunsTranslationScopeUri()).Value.Kind.Should()
            .Be(McpUriKind.RunsTranslationScope);

        McpCommandResult<McpFetchMeta, McpFetchResult> result = await commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.TranslationRunStatusUri(RunId)], null, null));

        McpFetchResult entry = result.Envelope!.Entries.Should().ContainSingle().Subject;
        entry.ResourceType.Should().Be("run_status");
        using JsonDocument content = JsonDocument.Parse(McpToonCodec.DecodeToJson(entry.Content!));
        content.RootElement.GetProperty("task_id").GetString().Should().Be(RunId);
        content.RootElement.GetProperty("state").GetString().Should().Be("running");
        content.RootElement.GetProperty("total").GetInt32().Should().Be(2);
        content.RootElement.GetProperty("completed").GetInt32().Should().Be(1);
        content.RootElement.GetProperty("succeeded").GetInt32().Should().Be(1);
        JsonElement pages = content.RootElement.GetProperty("pages");
        pages.GetArrayLength().Should().Be(1);
        pages[0].GetProperty("page_index").GetInt32().Should().Be(1);
        pages[0].GetProperty("state").GetString().Should().Be("committed");
        pages[0].GetProperty("committed_box_count").GetInt32().Should().Be(4);

        McpCommandResult<McpFindMeta, object> listed = await commands.FindAsync(
            new McpFindRequest(null, McpResourceUris.RunsTranslationScopeUri(), null));
        listed.Envelope!.Entries.Cast<McpFindEntry>().Should().ContainSingle()
            .Which.Uri.Should().Be(McpResourceUris.TranslationRunStatusUri(RunId));

        McpCommandResult<McpFetchMeta, McpFetchResult> missing = await commands.FetchAsync(
            new McpFetchRequest(
                [McpResourceUris.TranslationRunStatusUri("55555555-5555-5555-5555-555555555555")], null, null));
        McpToolError.TryGetCode(missing.Envelope!.Entries[0].Error, out McpErrorCode code).Should().BeTrue();
        code.Should().Be(McpErrorCode.NotFound);
    }

    [Fact]
    public async Task Translation_actions_start_and_cancel_through_the_run_api()
    {
        Guid documentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        const string RunId = "44444444-4444-4444-4444-444444444444";
        StubTranslationRunsApi translation = new();
        translation.SetRun(RunId, "running", 3);
        McpServerSettings settings = McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow) with
        {
            DomainPermissions =
            [
                new McpDomainPermission("texts", "translation.start", true),
                new McpDomainPermission("runs", "translation.cancel", true)
            ]
        };
        McpCommandService commands = CreateService(new StubReadApi(), translation: translation,
            permissions: _ => Task.FromResult(settings));

        McpCommandResult<McpFindMeta, object> actions = await commands.FindAsync(
            new McpFindRequest(null, "patchouli://actions/", null));
        actions.Envelope!.Entries.Cast<McpFindEntry>().Select(entry => entry.Title).Should()
            .Contain(["translation.start", "translation.cancel"]);

        McpCommandResult<McpActionSendMeta, McpActionSendResult> started = await commands.SendAsync(
            new McpActionSendRequest(McpResourceUris.DocumentUri(new DocumentInstanceId(documentId)),
                "translation.start", JsonSerializer.SerializeToElement(new
                {
                    pages = "1,3-5", target_language = "zh", force = true
                })));

        started.Error.Should().BeNull();
        McpActionSendResult startEntry = started.Envelope!.Entries.Should().ContainSingle().Subject;
        startEntry.State.Should().Be("started");
        startEntry.RunUri.Should().Be(McpResourceUris.TranslationRunStatusUri(RunId));
        startEntry.Result.Should().BeOfType<McpTranslationActionResult>().Which.TotalPages.Should().Be(3);
        McpTranslationRunStartRequest request = translation.Starts.Should().ContainSingle().Subject;
        request.Pages.Should().Be("1,3-5");
        request.TargetLanguage.Should().Be("zh");
        request.ForceRetranslate.Should().BeTrue();

        McpCommandResult<McpActionSendMeta, McpActionSendResult> cancelled = await commands.SendAsync(
            new McpActionSendRequest(McpResourceUris.TranslationRunStatusUri(RunId), "translation.cancel",
                JsonSerializer.SerializeToElement(new { })));

        cancelled.Error.Should().BeNull();
        cancelled.Envelope!.Entries.Should().ContainSingle().Which.State.Should().Be("completed");
        translation.Cancelled.Should().Be(RunId);
    }

    [Fact]
    public async Task Put_to_runs_resources_is_permission_denied()
    {
        McpCommandService commands = CreateService(new StubReadApi());

        foreach (string uri in new[]
                 {
                     McpResourceUris.OcrRunStatusUri("task-1"),
                     McpResourceUris.RunsOcrScopeUri(),
                     McpResourceUris.RunsTranslationScopeUri(),
                     "patchouli://runs/"
                 })
        {
            McpCommandResult<McpPutMeta, McpPutResult> result =
                await commands.PutAsync(new McpPutRequest(uri, "content"));
            result.Error.Should().NotBeNull();
            result.Error!.Code.Should().Be((int)McpErrorCode.PermissionDenied);
            result.Error.Name.Should().Be("PERMISSION_DENIED");
        }
    }

    [Fact]
    public async Task Disabled_send_switch_returns_permission_denied_while_reads_still_work()
    {
        StubOcrRunsApi ocr = new();
        ocr.SetTask("task-1", "queued");
        McpCommandService commands = CreateService(new StubReadApi(), ocr, sendEnabled: false);

        McpCommandResult<McpActionSendMeta, McpActionSendResult> denied = await commands.SendAsync(
            new McpActionSendRequest("patchouli://runs/ocr/22222222-2222-2222-2222-222222222222/", "ocr.cancel",
                JsonSerializer.SerializeToElement(new { })));
        denied.Error.Should().NotBeNull();
        denied.Error!.Code.Should().Be((int)McpErrorCode.PermissionDenied);
        denied.Error.Name.Should().Be("PERMISSION_DENIED");

        McpCommandResult<McpFindMeta, object> find = await commands.FindAsync(new McpFindRequest(null, null, null));
        find.IsSuccess.Should().BeTrue();
        find.Envelope!.Entries.Should().HaveCount(7);

        McpCommandResult<McpFetchMeta, McpFetchResult> fetch = await commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.OcrRunStatusUri("task-1")], null, null));
        fetch.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Disabled_put_switch_returns_permission_denied()
    {
        McpCommandService commands = CreateService(new StubReadApi(), putEnabled: false);

        McpCommandResult<McpPutMeta, McpPutResult> denied = await commands.PutAsync(
            new McpPutRequest("patchouli://items/00000000-0000-0000-0000-000000000001.bib", "@book{x}"));

        denied.Error.Should().NotBeNull();
        denied.Error!.Code.Should().Be((int)McpErrorCode.PermissionDenied);
    }

    private static McpCommandService CreateService(StubReadApi read,
        StubOcrRunsApi? ocr = null, bool putEnabled = true,
        bool sendEnabled = true, StubWriteApi? write = null,
        Func<CancellationToken, Task<McpServerSettings>>? permissions = null,
        StubTranslationRunsApi? translation = null)
    {
        return new McpCommandService(read, write ?? new StubWriteApi(), new StubBiblatexImportService(),
            new StubItemService(), new StubEvidenceReader(), putEnabled: putEnabled, sendEnabled: sendEnabled,
            ocrRuns: ocr, permissionSettings: permissions,
            translationRuns: translation);
    }

    private sealed class StubOcrRunsApi : IMcpOcrRunsApi
    {
        private readonly Dictionary<string, McpOcrTaskStatusProjection> _tasks = new(StringComparer.Ordinal);

        public void SetTask(string taskId, string state, int succeeded = 0, int total = 0)
        {
            _tasks[taskId] = new McpOcrTaskStatusProjection(taskId, "Item", "document-ocr", state, "engine",
                total, new McpOcrPageProgress(succeeded, 0, 0, total), null);
        }

        public Task<Result<IReadOnlyList<McpOcrTaskStatusProjection>>> ListTasksAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                Result<IReadOnlyList<McpOcrTaskStatusProjection>>.Success(
                    (IReadOnlyList<McpOcrTaskStatusProjection>)_tasks.Values.ToArray()));
        }

        public Task<Result<McpOcrTaskStatusProjection>> TryGetTaskStatusAsync(string taskId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(!_tasks.TryGetValue(taskId, out McpOcrTaskStatusProjection? task)
                ? Result<McpOcrTaskStatusProjection>.Failure(AppErrorCodes.NotFound,
                    $"The OCR task '{taskId}' does not exist.")
                : Result<McpOcrTaskStatusProjection>.Success(task));
        }
    }

    private sealed class StubTranslationRunsApi : IMcpTranslationRunsApi
    {
        private readonly Dictionary<string, McpTranslationRunStatusProjection> _runs = new(StringComparer.Ordinal);

        public readonly List<McpTranslationRunStartRequest> Starts = [];
        public string? Cancelled { get; private set; }

        public void SetRun(string taskId, string state, int total = 0, int completed = 0,
            IReadOnlyList<McpTranslationPageStatusProjection>? pages = null,
            string documentId = "11111111-1111-1111-1111-111111111111")
        {
            McpTranslationPageStatusProjection[] settled = pages is null ? [] : [.. pages];
            _runs[taskId] = new McpTranslationRunStatusProjection(
                taskId,
                documentId,
                state,
                total,
                completed,
                settled.Count(page => page.State == "committed"),
                settled.Count(page => page.State == "partial"),
                settled.Count(page => page.State == "failed"),
                settled.Count(page => page.State == "skipped"),
                settled.Count(page => page.State == "cancelled"),
                settled);
        }

        public Task<Result<McpTranslationRunStatusProjection>> StartAsync(
            McpTranslationRunStartRequest request, CancellationToken cancellationToken = default)
        {
            Starts.Add(request);
            if (_runs.Count == 0)
            {
                SetRun("44444444-4444-4444-4444-444444444444", "running", 1,
                    documentId: request.DocumentInstanceId.ToString());
            }

            return Task.FromResult(Result<McpTranslationRunStatusProjection>.Success(_runs.Values.First()));
        }

        public Task<Result<IReadOnlyList<McpTranslationRunStatusProjection>>> ListRunsAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<McpTranslationRunStatusProjection>>.Success(
                (IReadOnlyList<McpTranslationRunStatusProjection>)_runs.Values.ToArray()));
        }

        public Task<Result<McpTranslationRunStatusProjection>> TryGetRunStatusAsync(string taskId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(!_runs.TryGetValue(taskId, out McpTranslationRunStatusProjection? run)
                ? Result<McpTranslationRunStatusProjection>.Failure(AppErrorCodes.NotFound,
                    $"The translation run '{taskId}' does not exist.")
                : Result<McpTranslationRunStatusProjection>.Success(run));
        }

        public Task<Result<McpTranslationRunStatusProjection>> CancelAsync(string taskId,
            CancellationToken cancellationToken = default)
        {
            Cancelled = taskId;
            if (!_runs.TryGetValue(taskId, out McpTranslationRunStatusProjection? run))
            {
                return Task.FromResult(Result<McpTranslationRunStatusProjection>.Failure(AppErrorCodes.NotFound,
                    $"The translation run '{taskId}' does not exist."));
            }

            _runs[taskId] = run with { State = "cancelled" };
            return Task.FromResult(Result<McpTranslationRunStatusProjection>.Success(_runs[taskId]));
        }
    }

    private sealed class StubReadApi : IMcpReadApi
    {
        public Task<Result<McpLibraryStateResponse>> GetCurrentLibraryStateAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpLibraryStateResponse>.Success(
                new McpLibraryStateResponse("lib", "lib:1")));
        }

        public Task<Result<McpLibraryProjection>> GetLibraryProjectionAsync(bool includeTags,
            bool includeCollections, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpLibraryProjection>.Success(
                new McpLibraryProjection("lib", "Fake Library", includeTags ? [] : null,
                    includeCollections ? [] : null)));
        }

        public Task<Result<IReadOnlyList<CollectionId>>> GetItemCollectionIdsAsync(ItemId itemId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<CollectionId>>.Success([]));
        }

        public Task<Result<McpSearchLibraryResponse>> SearchLibraryAsync(McpSearchLibraryRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpSearchLibraryResponse>.Failure("fake", "x"));
        }

        public Task<Result<McpItemMetadataResponse>> GetItemMetadataAsync(ItemId itemId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpItemMetadataResponse>.Failure("fake", "x"));
        }

        public Task<Result<McpDocumentStatusResponse>> GetDocumentStatusAsync(
            DocumentInstanceId documentInstanceId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpDocumentStatusResponse>.Failure("fake", "x"));
        }

        public Task<Result<McpPageTextResponse>> GetPageTextAsync(McpPageTextRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpPageTextResponse>.Failure("fake", "x"));
        }

        public Task<Result<McpPageBlocksResponse>> GetPageBlocksAsync(McpPageBlocksRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpPageBlocksResponse>.Failure("fake", "x"));
        }

        public Task<Result<McpSearchContextResponse>> GetSearchResultContextAsync(
            McpSearchContextRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpSearchContextResponse>.Failure("fake", "x"));
        }

        public Task<Result<IReadOnlyList<McpCslStyleSummary>>> ListCslStylesAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<McpCslStyleSummary>>.Failure("fake", "x"));
        }

        public Task<Result<McpCslStyleResponse>> GetCslStyleAsync(string styleId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpCslStyleResponse>.Failure("fake", "x"));
        }

        public Task<Result<McpRenderBibliographyResponse>> RenderItemBibliographyAsync(ItemId itemId,
            string? styleId = null, string? locale = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpRenderBibliographyResponse>.Failure("fake", "x"));
        }

        public Task<Result<McpRenderBibliographyResponse>> RenderItemsBibliographyAsync(
            McpRenderBibliographyRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpRenderBibliographyResponse>.Failure("fake", "x"));
        }

        public Task<Result<McpBrowseItemPage>> BrowseItemsAsync(int skip, int limit,
            IReadOnlyList<McpWhereClause>? where = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpBrowseItemPage>.Failure("fake", "x"));
        }

        public Task<Result<McpBrowseItemPage>> SearchItemsAsync(string query, bool literal, int skip, int limit,
            IReadOnlyList<McpWhereClause>? where = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpBrowseItemPage>.Failure("fake", "x"));
        }

        public Task<Result<McpBrowseDocumentPage>> BrowseDocumentsAsync(int skip, int limit,
            IReadOnlyList<McpWhereClause>? where = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpBrowseDocumentPage>.Failure("fake", "x"));
        }

        public Task<Result<IReadOnlyList<McpTextResourceProjection>>> GetTextResourceProjectionsAsync(
            IReadOnlyList<DocumentInstanceId> documentInstanceIds, IReadOnlyList<McpWhereClause>? where = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<McpTextResourceProjection>>.Failure("fake", "x"));
        }

        public Task<Result<string>> GetPrimaryDocumentOcrIndexStatusAsync(ItemId itemId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<string>.Failure("fake", "x"));
        }

        public Task<Result<McpBrowseStylePage>> BrowseStylesAsync(int skip, int limit,
            IReadOnlyList<McpWhereClause>? where = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpBrowseStylePage>.Failure("fake", "x"));
        }

        public Task<Result<McpDocumentOutlineResponse>> GetDocumentOutlineAsync(
            DocumentInstanceId documentInstanceId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpDocumentOutlineResponse>.Failure("fake", "x"));
        }

        public Task<Result<ItemId>> GetItemIdForDocumentAsync(DocumentInstanceId documentInstanceId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<ItemId>.Failure("fake", "x"));
        }

        public Task<Result<McpBrowseTranslationPage>> BrowseTranslationsAsync(int skip, int limit,
            string? query, IReadOnlyList<McpWhereClause>? where = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpBrowseTranslationPage>.Failure("fake", "x"));
        }

        public Task<Result<McpTranslationOutlineResponse>> GetTranslationOutlineAsync(
            DocumentInstanceId documentInstanceId, string? query = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpTranslationOutlineResponse>.Failure("fake", "x"));
        }

        public Task<Result<McpPageTranslationResponse>> GetPageTranslationAsync(
            McpPageTranslationRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<McpPageTranslationResponse>.Failure("fake", "x"));
        }
    }

    private sealed class StubWriteApi : IMcpWriteApi
    {
        public int CallCount { get; private set; }

        public Task<Result<McpPutResponse>> PutAsync(McpPutRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(Result<McpPutResponse>.Failure("fake", "x"));
        }

        public Task<Result<McpPutResponse>> PutPageTranslationAsync(string uri,
            DocumentInstanceId documentInstanceId, PageId pageId, string content,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(Result<McpPutResponse>.Failure("fake", "x"));
        }
    }

    private sealed class StubBiblatexImportService : IBiblatexImportService
    {
        private static Result<T> Unavailable<T>()
        {
            return Result<T>.Failure(AppErrorCodes.UnsupportedOperation, "BibLaTeX import is unavailable.");
        }

        public Task<Result<IReadOnlyList<BiblatexEntryDto>>> ParseTextAsync(string text,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<IReadOnlyList<BiblatexEntryDto>>());
        }

        public Task<Result<IReadOnlyList<BiblatexEntryDto>>> ParseFileAsync(string path,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<IReadOnlyList<BiblatexEntryDto>>());
        }

        public Task<Result<BiblatexSingleImportPreview>> PreviewSingleAsync(BiblatexEntryDto entry,
            ItemId? targetItemId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<BiblatexSingleImportPreview>());
        }

        public Task<Result<BiblatexImportApplyResult>> ApplySingleAsync(BiblatexMappedItem source,
            ItemId? targetItemId, IReadOnlyDictionary<string, string>? fieldChoices, string? bibFileDirectory,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<BiblatexImportApplyResult>());
        }

        public Task<Result<BiblatexBatchImportPreview>> PreviewBatchAsync(
            IReadOnlyList<BiblatexEntryDto> entries, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<BiblatexBatchImportPreview>());
        }

        public Task<Result<BiblatexImportApplyResult>> ApplyBatchAsync(BiblatexBatchImportPlan plan,
            IReadOnlyDictionary<string, string>? linkChoices, string? bibFileDirectory,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<BiblatexImportApplyResult>());
        }

        public Task<Result<string>> ExportItemsAsync(IReadOnlyList<ItemId> itemIds,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<string>());
        }

        public Task<Result<string>> ExportItemForAgentAsync(ItemId itemId, bool includeKeywords,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<string>());
        }
    }

    private sealed class StubItemService : IItemService
    {
        private static Result<T> Unavailable<T>()
        {
            return Result<T>.Failure(AppErrorCodes.UnsupportedOperation, "Item service is unavailable.");
        }

        public Task<Result<ItemMetadata>> CreateItemAsync(CreateItemRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<ItemMetadata>());
        }

        public Task<Result<ItemMetadata>> CreateItemAsync(string itemType, string title, string? subtitle = null,
            string? titleShort = null, string? creatorsJson = null, string? date = null,
            string? publicationTitle = null, string? containerTitleShort = null, string? collectionTitle = null,
            string? publisher = null, string? place = null, string? edition = null, string? genre = null,
            string? number = null, string? chapterNumber = null, string? volume = null, string? version = null,
            string? issue = null, string? pages = null, string? language = null, string? status = null,
            string? note = null, string? abstractText = null, string? tagsJson = null, string? collectionsJson = null,
            string? customFieldsJson = null, IReadOnlyList<ItemCreatorInput>? creators = null,
            IReadOnlyList<ItemDateInput>? dates = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<ItemMetadata>());
        }

        public Task<Result<ItemMetadata>> GetItemAsync(ItemId itemId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<ItemMetadata>());
        }

        public Task<Result<ItemLifecycleInfo>> GetItemLifecycleAsync(ItemId itemId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<ItemLifecycleInfo>());
        }

        public Task<Result<ItemMetadata>> UpdateItemAsync(ItemId itemId, UpdateItemRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<ItemMetadata>());
        }

        public Task<Result<ItemMetadata>> ReplaceItemAsync(ItemId itemId, UpdateItemRequest request,
            IReadOnlyList<ItemIdentifierInput> identifiers, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<ItemMetadata>());
        }

        public Task<Result> DeleteItemAsync(ItemId itemId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Failure(AppErrorCodes.UnsupportedOperation, "unavailable"));
        }

        public Task<Result> DeleteItemsAsync(IReadOnlyList<ItemId> itemIds,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Failure(AppErrorCodes.UnsupportedOperation, "unavailable"));
        }

        public Task<Result<ItemMetadata>> RestoreItemAsync(ItemId itemId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<ItemMetadata>());
        }

        public Task<Result> RestoreItemsAsync(IReadOnlyList<ItemId> itemIds,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Failure(AppErrorCodes.UnsupportedOperation, "unavailable"));
        }

        public Task<Result<ItemListPage>> ListItemsAsync(ListItemsRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<ItemListPage>());
        }

        public Task<Result<ItemListPage>> ListTrashedItemsAsync(int pageSize = 50, string? cursor = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<ItemListPage>());
        }

        public Task<Result<ItemIdentifier>> AddIdentifierAsync(ItemId itemId, string scheme, string value,
            string? note, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<ItemIdentifier>());
        }

        public Task<Result<IReadOnlyList<ItemIdentifier>>> ListIdentifiersAsync(ItemId itemId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Unavailable<IReadOnlyList<ItemIdentifier>>());
        }

        public Task<Result> RemoveIdentifierAsync(ItemId itemId, IdentifierId identifierId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Failure(AppErrorCodes.UnsupportedOperation, "unavailable"));
        }
    }

    private sealed class StubEvidenceReader : IVersionedEvidenceReader
    {
        public Task<Result<EvidencePageText>> GetBoxTextAsync(DocumentInstanceId documentInstanceId,
            int pageIndex1Based, DocumentTreeRevisionId? revisionId = null, DocumentBoxId? boxId = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<EvidencePageText>.Failure(AppErrorCodes.NotFound, "no evidence"));
        }
    }
}
