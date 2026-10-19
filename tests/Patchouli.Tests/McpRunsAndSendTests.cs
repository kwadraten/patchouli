using System.Text.Json;
using FluentAssertions;
using Patchouli.Cli;
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
///     In-process contract tests for the S4 <c>runs/</c> + <c>workflows/</c> projections and the
///     <c>send</c> verb over <see cref="McpCommandService" /> (no network). Covers V3-AC10 root
///     discovery (domain directories plus one file), V3-AC26 (volatile runs/, read-only workflows/,
///     no library_revision advancement), V3-AC27 (monotonic event streams with ?after=,
///     last_sequence/next_after, accepted-vs-processed, message_id deduplication) and V3-AC28
///     (text-only data boundary, user tool switches returning the surface to read-only).
/// </summary>
public sealed class McpRunsAndSendTests
{
    private const string SessionId = "sess-0123456789abcdef";

    [Fact]
    public async Task Domain_permissions_gate_every_verb_and_filter_root_discovery()
    {
        McpServerSettings settings = McpServerSettingsService.DefaultSettings(DateTimeOffset.UtcNow) with
        {
            DomainPermissions =
            [
                new McpDomainPermission("texts", "find", false), new McpDomainPermission("texts", "fetch", false),
                new McpDomainPermission("translations", "put", false), new McpDomainPermission("items", "cite", false),
                new McpDomainPermission("workflows", "send", false), new McpDomainPermission("runs", "send", false)
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
        StubAgentRunsApi agent = new();
        agent.SetSession("one", "running");
        agent.SetSession("two", "running");
        McpCommandService commands = CreateService(new StubReadApi(), agent,
            permissions: _ => Task.FromResult(currentSettings[0]));
        McpCommandResult<McpFindMeta, object> first =
            await commands.FindAsync(new McpFindRequest(null, "patchouli://runs/agent/", null, Limit: 1));
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
        entries.Should().HaveCount(8);
        entries.Count(entry => entry.Type == "directory").Should().Be(7);
        entries.Count(entry => entry.Type == "file").Should().Be(1);
        entries.Select(entry => entry.Uri).Should().BeEquivalentTo(
            "patchouli://items/",
            "patchouli://texts/",
            "patchouli://translations/",
            "patchouli://csl-styles/",
            "patchouli://runs/",
            "patchouli://workflows/",
            "patchouli://actions/",
            "patchouli://library.yml");
        result.Envelope.Meta.DomainTotal.Should().Be(8);
        result.Envelope.Meta.FilteredTotal.Should().Be(8);
        result.Envelope.Meta.ShownTotal.Should().Be(8);
    }

    [Fact]
    public async Task Runs_agent_status_fetch_projects_live_snapshot_without_touching_writes()
    {
        StubAgentRunsApi agent = new();
        agent.SetSession(SessionId, "running", 4);
        StubWriteApi write = new();
        McpCommandService commands = CreateService(new StubReadApi(), agent, write: write);

        McpCommandResult<McpFetchMeta, McpFetchResult> result = await commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.AgentRunStatusUri(SessionId)], null, null));

        result.IsSuccess.Should().BeTrue();
        result.Envelope!.Meta.LibraryRevision.Should().Be("lib:1");
        McpFetchResult entry = result.Envelope.Entries.Should().ContainSingle().Subject;
        entry.ResourceType.Should().Be("run_status");
        entry.Complete.Should().BeTrue();
        entry.Truncated.Should().BeFalse();
        using JsonDocument content = JsonDocument.Parse(McpToonCodec.DecodeToJson(entry.Content!));
        content.RootElement.GetProperty("session_id").GetString().Should().Be(SessionId);
        content.RootElement.GetProperty("status").GetString().Should().Be("running");
        content.RootElement.GetProperty("event_seq").GetInt64().Should().Be(4);
        write.CallCount.Should().Be(0);
        agent.Sessions.Should().ContainKey(SessionId);
    }

    [Fact]
    public async Task Runs_agent_base_session_uri_serves_the_status_projection()
    {
        StubAgentRunsApi agent = new();
        agent.SetSession(SessionId, "awaiting_effect", 9);
        McpCommandService commands = CreateService(new StubReadApi(), agent);

        McpCommandResult<McpFetchMeta, McpFetchResult> result = await commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.AgentRunUri(SessionId)], null, null));

        McpFetchResult entry = result.Envelope!.Entries.Should().ContainSingle().Subject;
        entry.ResourceType.Should().Be("run_status");
        using JsonDocument content = JsonDocument.Parse(McpToonCodec.DecodeToJson(entry.Content!));
        content.RootElement.GetProperty("status").GetString().Should().Be("awaiting_effect");
    }

    [Fact]
    public async Task Runs_agent_events_support_after_incremental_reads_with_stream_positions()
    {
        StubAgentRunsApi agent = new();
        agent.SetSession(SessionId, "running", 3);
        agent.Logs[SessionId] =
        [
            new McpAgentSessionEvent(1, "launch", "{}", "2026-08-01T00:00:00Z"),
            new McpAgentSessionEvent(2, "inbox", "{}", "2026-08-01T00:00:01Z"),
            new McpAgentSessionEvent(3, "event", "{}", "2026-08-01T00:00:02Z")
        ];
        McpCommandService commands = CreateService(new StubReadApi(), agent);

        McpCommandResult<McpFetchMeta, McpFetchResult> incremental = await commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.AgentRunEventsUri(SessionId, 1)], null, null));

        McpFetchResult entry = incremental.Envelope!.Entries.Should().ContainSingle().Subject;
        entry.ResourceType.Should().Be("run_events");
        using JsonDocument content = JsonDocument.Parse(McpToonCodec.DecodeToJson(entry.Content!));
        JsonElement events = content.RootElement.GetProperty("events");
        events.GetArrayLength().Should().Be(2);
        events[0].GetProperty("seq").GetInt64().Should().Be(2);
        events[1].GetProperty("seq").GetInt64().Should().Be(3);
        content.RootElement.GetProperty("last_sequence").GetInt64().Should().Be(3);
        content.RootElement.GetProperty("next_after").GetInt64().Should().Be(3);

        McpCommandResult<McpFetchMeta, McpFetchResult> fromStart = await commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.AgentRunEventsUri(SessionId, 0)], null, null));
        using JsonDocument all = JsonDocument.Parse(
            McpToonCodec.DecodeToJson(fromStart.Envelope!.Entries[0].Content!));
        all.RootElement.GetProperty("events").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task Runs_agent_events_of_unknown_session_return_not_found()
    {
        StubAgentRunsApi agent = new();
        McpCommandService commands = CreateService(new StubReadApi(), agent);

        McpCommandResult<McpFetchMeta, McpFetchResult> result = await commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.AgentRunEventsUri("missing-session")], null, null));

        McpFetchResult entry = result.Envelope!.Entries.Should().ContainSingle().Subject;
        entry.ResourceType.Should().BeNull();
        McpToolError.TryGetCode(entry.Error, out McpErrorCode code).Should().BeTrue();
        code.Should().Be(McpErrorCode.NotFound);
    }

    [Fact]
    public async Task Runs_browse_lists_ocr_translation_and_agent_subtrees_and_sessions()
    {
        StubAgentRunsApi agent = new();
        agent.SetSession(SessionId, "running");
        StubOcrRunsApi ocr = new();
        ocr.SetTask("task-1", "queued");
        StubTranslationRunsApi translation = new();
        translation.SetRun("44444444-4444-4444-4444-444444444444", "running", 1);
        McpCommandService commands = CreateService(new StubReadApi(), agent, ocr: ocr, translation: translation);

        McpCommandResult<McpFindMeta, object> runs = await commands.FindAsync(
            new McpFindRequest(null, "patchouli://runs/", null));
        runs.Envelope!.Entries.Cast<McpFindEntry>().Select(entry => entry.Uri).Should().Equal(
            "patchouli://runs/ocr/", "patchouli://runs/translation/", "patchouli://runs/agent/");

        McpCommandResult<McpFindMeta, object> agents = await commands.FindAsync(
            new McpFindRequest(null, "patchouli://runs/agent/", null));
        agents.Envelope!.Entries.Cast<McpFindEntry>().Should().ContainSingle()
            .Which.Uri.Should().Be(McpResourceUris.AgentRunStatusUri(SessionId));

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
        McpCommandService commands = CreateService(new StubReadApi(), ocr: ocr);

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
    public async Task Workflows_scope_lists_definitions_and_fetch_returns_parameter_definitions()
    {
        StubWorkflowRunsApi workflows = new();
        workflows.SetWorkflow("builtin.full-text-translation", "Full-text translation",
        [
            new McpWorkflowParameter("documentId", "DocumentId", true, "Document to translate.", ""),
            new McpWorkflowParameter("targetLanguage", "Language", false, "Target language.", "zh")
        ]);
        McpCommandService commands = CreateService(new StubReadApi(), workflows: workflows);

        McpCommandResult<McpFindMeta, object> list = await commands.FindAsync(
            new McpFindRequest(null, "patchouli://workflows/", null));
        McpFindEntry entry = list.Envelope!.Entries.Cast<McpFindEntry>().Should().ContainSingle().Subject;
        entry.Uri.Should().Be("patchouli://workflows/builtin.full-text-translation");
        entry.Type.Should().Be("file");

        McpCommandResult<McpFetchMeta, McpFetchResult> fetched = await commands.FetchAsync(
            new McpFetchRequest(["patchouli://workflows/builtin.full-text-translation"], null, null));
        McpFetchResult workflowEntry = fetched.Envelope!.Entries.Should().ContainSingle().Subject;
        workflowEntry.ResourceType.Should().Be("workflow");
        using JsonDocument content = JsonDocument.Parse(McpToonCodec.DecodeToJson(workflowEntry.Content!));
        content.RootElement.GetProperty("workflow_id").GetString()
            .Should().Be("builtin.full-text-translation");
        JsonElement parameters = content.RootElement.GetProperty("parameters");
        parameters.GetArrayLength().Should().Be(2);
        parameters[0].GetProperty("name").GetString().Should().Be("documentId");
        parameters[0].GetProperty("required").GetBoolean().Should().BeTrue();
        parameters[1].GetProperty("default_value").GetString().Should().Be("zh");

        McpCommandResult<McpFetchMeta, McpFetchResult> missing = await commands.FetchAsync(
            new McpFetchRequest(["patchouli://workflows/nope"], null, null));
        McpToolError.TryGetCode(missing.Envelope!.Entries[0].Error, out McpErrorCode code).Should().BeTrue();
        code.Should().Be(McpErrorCode.NotFound);
    }

    [Fact]
    public async Task Put_to_runs_and_workflows_resources_is_permission_denied()
    {
        StubAgentRunsApi agent = new();
        agent.SetSession(SessionId, "running");
        StubWorkflowRunsApi workflows = new();
        workflows.SetWorkflow("wf-1", "Workflow");
        McpCommandService commands = CreateService(new StubReadApi(), agent, workflows);

        foreach (string uri in new[]
                 {
                     McpResourceUris.AgentRunStatusUri(SessionId),
                     McpResourceUris.AgentRunEventsUri(SessionId),
                     McpResourceUris.AgentRunUri(SessionId),
                     McpResourceUris.OcrRunStatusUri("task-1"),
                     "patchouli://workflows/wf-1",
                     "patchouli://workflows/",
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

    [Theory]
    [InlineData("start")]
    [InlineData("message")]
    [InlineData("cancel")]
    [InlineData("resume")]
    public async Task Legacy_send_overload_rejects_migration_without_launching_or_messaging_agent(string instruction)
    {
        StubAgentRunsApi agent = new();
        agent.SetSession(SessionId, "running");
        StubWorkflowRunsApi workflows = new();
        workflows.SetWorkflow("wf-1", "Translate");
        McpCommandService commands = CreateService(new StubReadApi(), agent, workflows);
        McpCommandResult<McpSendMeta, McpSendResult> rejected = await commands.SendAsync(
            new McpSendRequest(instruction, "patchouli://workflows/wf-1",
                Session: McpResourceUris.AgentRunUri(SessionId), MessageId: "m", Text: "go"));
        rejected.Error!.Name.Should().Be("INVALID_ARGUMENT");
        rejected.Error.Detail!.ToLowerInvariant().Should().Contain("target").And.Contain("action");
        workflows.Launches.Should().BeEmpty();
        agent.Messages.Should().BeEmpty();
        agent.Sessions[SessionId].Item1.Should().Be("running");
    }

    [Fact]
    public async Task Disabled_send_switch_returns_permission_denied_while_reads_still_work()
    {
        StubAgentRunsApi agent = new();
        agent.SetSession(SessionId, "running");
        StubWorkflowRunsApi workflows = new();
        workflows.SetWorkflow("wf-1", "Translate");
        McpCommandService commands = CreateService(new StubReadApi(), agent, workflows,
            sendEnabled: false);

        McpCommandResult<McpActionSendMeta, McpActionSendResult> denied = await commands.SendAsync(
            new McpActionSendRequest("patchouli://runs/ocr/22222222-2222-2222-2222-222222222222/", "ocr.cancel",
                JsonSerializer.SerializeToElement(new { })));
        denied.Error.Should().NotBeNull();
        denied.Error!.Code.Should().Be((int)McpErrorCode.PermissionDenied);
        denied.Error.Name.Should().Be("PERMISSION_DENIED");

        McpCommandResult<McpFindMeta, object> find = await commands.FindAsync(new McpFindRequest(null, null, null));
        find.IsSuccess.Should().BeTrue();
        find.Envelope!.Entries.Should().HaveCount(8);

        McpCommandResult<McpFetchMeta, McpFetchResult> fetch = await commands.FetchAsync(
            new McpFetchRequest([McpResourceUris.AgentRunStatusUri(SessionId)], null, null));
        fetch.IsSuccess.Should().BeTrue();

        McpCommandResult<McpFetchMeta, McpFetchResult> workflow = await commands.FetchAsync(
            new McpFetchRequest(["patchouli://workflows/wf-1"], null, null));
        workflow.IsSuccess.Should().BeTrue();
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

    [Theory]
    [InlineData("start")]
    [InlineData("message")]
    [InlineData("cancel")]
    [InlineData("resume")]
    public void Legacy_cli_agent_send_is_rejected_with_migration_hint(string instruction)
    {
        Action parse = () => CliArguments.BuildToolCall("send",
            [instruction, "--session", "patchouli://runs/agent/s-1"], false);
        parse.Should().Throw<CliUsageException>().Which.Message.ToLowerInvariant().Should().Contain("target");
    }

    private static McpCommandService CreateService(StubReadApi read, StubAgentRunsApi? agent = null,
        StubWorkflowRunsApi? workflows = null, StubOcrRunsApi? ocr = null, bool putEnabled = true,
        bool sendEnabled = true, StubWriteApi? write = null,
        Func<CancellationToken, Task<McpServerSettings>>? permissions = null,
        StubTranslationRunsApi? translation = null)
    {
        return new McpCommandService(read, write ?? new StubWriteApi(), new StubBiblatexImportService(),
            new StubItemService(), new StubEvidenceReader(), putEnabled: putEnabled, sendEnabled: sendEnabled,
            agentRuns: agent, workflowRuns: workflows, ocrRuns: ocr, permissionSettings: permissions,
            translationRuns: translation);
    }

    private sealed class StubAgentRunsApi : IMcpAgentRunsApi
    {
        public readonly Dictionary<string, (string Status, long Seq)> Sessions = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<McpAgentSessionEvent>> Logs = new(StringComparer.Ordinal);
        public readonly List<(string SessionId, string MessageId, string Text)> Messages = [];

        public void SetSession(string sessionId, string status, long seq = 0)
        {
            Sessions[sessionId] = (status, seq);
            Logs.TryAdd(sessionId, []);
            if (seq > 0 && Logs[sessionId].Count == 0)
            {
                Logs[sessionId].Add(new McpAgentSessionEvent(seq, "event", "{}", "2026-08-01T00:00:00Z"));
            }
        }

        public Task<Result<IReadOnlyList<McpAgentSessionStatusProjection>>> ListSessionsAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<McpAgentSessionStatusProjection>>.Success(
                Sessions.Select(pair => Project(pair.Key, pair.Value.Status, pair.Value.Seq)).ToArray()));
        }

        public Task<Result<McpAgentSessionStatusProjection>> TryGetSessionAsync(string sessionId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Sessions.TryGetValue(sessionId, out (string, long) session)
                ? Result<McpAgentSessionStatusProjection>.Success(
                    Project(sessionId, session.Item1, session.Item2))
                : Result<McpAgentSessionStatusProjection>.Failure(AppErrorCodes.NotFound,
                    $"The agent session '{sessionId}' does not exist."));
        }

        public Task<Result<IReadOnlyList<McpAgentSessionEvent>>> ReadSessionEventsAsync(string sessionId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(!Sessions.ContainsKey(sessionId)
                ? Result<IReadOnlyList<McpAgentSessionEvent>>.Failure(AppErrorCodes.NotFound,
                    $"The agent session '{sessionId}' does not exist.")
                : Result<IReadOnlyList<McpAgentSessionEvent>>.Success(
                    (IReadOnlyList<McpAgentSessionEvent>)Logs[sessionId].ToArray()));
        }

        public Task<Result<McpAgentMessageAck>> SendSessionMessageAsync(string sessionId, string messageId,
            string text, CancellationToken cancellationToken = default)
        {
            if (!Sessions.ContainsKey(sessionId))
            {
                return Task.FromResult(Result<McpAgentMessageAck>.Failure(AppErrorCodes.NotFound,
                    $"The agent session '{sessionId}' does not exist."));
            }

            bool duplicate = Messages.Any(message =>
                string.Equals(message.MessageId, messageId, StringComparison.Ordinal));
            if (!duplicate)
            {
                Messages.Add((sessionId, messageId, text));
            }

            return Task.FromResult(
                Result<McpAgentMessageAck>.Success(new McpAgentMessageAck(true, duplicate, 0)));
        }

        public Task<Result<McpAgentSessionStatusProjection>> CancelSessionAsync(string sessionId,
            CancellationToken cancellationToken = default)
        {
            if (!Sessions.ContainsKey(sessionId))
            {
                return Task.FromResult(Result<McpAgentSessionStatusProjection>.Failure(AppErrorCodes.NotFound,
                    $"The agent session '{sessionId}' does not exist."));
            }

            Sessions[sessionId] = ("cancelled", Sessions[sessionId].Seq);
            return Task.FromResult(
                Result<McpAgentSessionStatusProjection>.Success(Project(sessionId, "cancelled")));
        }

        public Task<Result<McpAgentSessionStatusProjection>> ResumeSessionAsync(string sessionId,
            CancellationToken cancellationToken = default)
        {
            if (!Sessions.ContainsKey(sessionId))
            {
                return Task.FromResult(Result<McpAgentSessionStatusProjection>.Failure(AppErrorCodes.NotFound,
                    $"The agent session '{sessionId}' does not exist."));
            }

            Sessions[sessionId] = ("running", Sessions[sessionId].Seq);
            return Task.FromResult(
                Result<McpAgentSessionStatusProjection>.Success(Project(sessionId, "running")));
        }

        private static McpAgentSessionStatusProjection Project(string sessionId, string status, long seq = 0)
        {
            return new McpAgentSessionStatusProjection(sessionId, status, seq, 0, 0, 0, null,
                "2026-08-01T00:00:00Z");
        }
    }

    private sealed class StubWorkflowRunsApi : IMcpWorkflowRunsApi
    {
        public readonly Dictionary<string,
            (string Name, string Description, IReadOnlyList<McpWorkflowParameter> Parameters)> Workflows =
            new(StringComparer.Ordinal);

        public readonly List<(string WorkflowId, IReadOnlyDictionary<string, string> Parameters)> Launches = [];

        public void SetWorkflow(string workflowId, string name,
            IReadOnlyList<McpWorkflowParameter>? parameters = null)
        {
            Workflows[workflowId] = (name, string.Empty, parameters ?? []);
        }

        public Task<Result<IReadOnlyList<McpWorkflowSummary>>> ListWorkflowsAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<McpWorkflowSummary>>.Success(
                Workflows.Select(pair => new McpWorkflowSummary(pair.Key, pair.Value.Name, pair.Value.Description))
                    .ToArray()));
        }

        public Task<Result<McpWorkflowDetail>> TryGetWorkflowAsync(string workflowId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(!Workflows.TryGetValue(workflowId,
                out (string Name, string Description, IReadOnlyList<McpWorkflowParameter> Parameters) workflow)
                ? Result<McpWorkflowDetail>.Failure(AppErrorCodes.NotFound,
                    $"The workflow '{workflowId}' does not exist.")
                : Result<McpWorkflowDetail>.Success(new McpWorkflowDetail(
                    workflowId,
                    workflow.Name,
                    workflow.Description,
                    "run",
                    "Nothing",
                    true,
                    true,
                    new McpWorkflowMenuPlacement("Tools/Workflows", 0, true),
                    workflow.Parameters)));
        }

        public Task<Result<McpWorkflowStartResult>> StartAsync(string workflowId,
            IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
        {
            if (!Workflows.ContainsKey(workflowId))
            {
                return Task.FromResult(Result<McpWorkflowStartResult>.Failure(AppErrorCodes.NotFound,
                    $"The workflow '{workflowId}' does not exist."));
            }

            List<McpWorkflowValidationIssue> issues = [];
            foreach (McpWorkflowParameter field in Workflows[workflowId].Parameters)
            {
                if (field.Required && (!parameters.TryGetValue(field.Name, out string? value) ||
                                       string.IsNullOrWhiteSpace(value)))
                {
                    issues.Add(new McpWorkflowValidationIssue(field.Name, "required",
                        "Required parameter is missing."));
                }
            }

            foreach (string key in parameters.Keys)
            {
                if (Workflows[workflowId].Parameters.All(field => field.Name != key))
                {
                    issues.Add(new McpWorkflowValidationIssue(key, "unknown_parameter", "Unknown parameter."));
                }
            }

            if (issues.Count > 0)
            {
                return Task.FromResult(Result<McpWorkflowStartResult>.Failure(AppErrorCodes.InvalidArgument,
                    "Workflow configuration is invalid.", details: new McpWorkflowValidationFailureDetails(issues)));
            }

            Launches.Add((workflowId, parameters));
            return Task.FromResult(
                Result<McpWorkflowStartResult>.Success(new McpWorkflowStartResult($"{workflowId}-session")));
        }
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
