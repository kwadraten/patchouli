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
///     discovery (six directories plus one file), V3-AC26 (volatile runs/, read-only workflows/,
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
        (await commands.SendAsync(new McpSendRequest("start", "patchouli://workflows/test"))).Error!.Name.Should()
            .Be("PERMISSION_DENIED");
        (await commands.SendAsync(new McpSendRequest("message", Session: McpResourceUris.AgentRunUri(SessionId),
                MessageId: "m", Text: "text")))
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
    public async Task Root_discovery_lists_six_directories_and_one_file()
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
        entries.Select(entry => entry.Uri).Should().Equal(
            "patchouli://items/",
            "patchouli://texts/",
            "patchouli://translations/",
            "patchouli://csl-styles/",
            "patchouli://runs/",
            "patchouli://workflows/",
            "patchouli://library.toon");
        result.Envelope.Meta.DomainTotal.Should().Be(7);
        result.Envelope.Meta.FilteredTotal.Should().Be(7);
        result.Envelope.Meta.ShownTotal.Should().Be(7);
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
    public async Task Runs_browse_lists_ocr_and_agent_subtrees_and_sessions()
    {
        StubAgentRunsApi agent = new();
        agent.SetSession(SessionId, "running");
        StubOcrRunsApi ocr = new();
        ocr.SetTask("task-1", "queued");
        McpCommandService commands = CreateService(new StubReadApi(), agent, ocr: ocr);

        McpCommandResult<McpFindMeta, object> runs = await commands.FindAsync(
            new McpFindRequest(null, "patchouli://runs/", null));
        runs.Envelope!.Entries.Cast<McpFindEntry>().Select(entry => entry.Uri).Should().Equal(
            "patchouli://runs/ocr/", "patchouli://runs/agent/");

        McpCommandResult<McpFindMeta, object> agents = await commands.FindAsync(
            new McpFindRequest(null, "patchouli://runs/agent/", null));
        agents.Envelope!.Entries.Cast<McpFindEntry>().Should().ContainSingle()
            .Which.Uri.Should().Be(McpResourceUris.AgentRunStatusUri(SessionId));

        McpCommandResult<McpFindMeta, object> tasks = await commands.FindAsync(
            new McpFindRequest(null, "patchouli://runs/ocr/", null));
        tasks.Envelope!.Entries.Cast<McpFindEntry>().Should().ContainSingle()
            .Which.Uri.Should().Be(McpResourceUris.OcrRunStatusUri("task-1"));
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

    [Fact]
    public async Task Send_start_returns_the_new_session_uri_and_distinguishes_accepted_processed()
    {
        StubWorkflowRunsApi workflows = new();
        workflows.SetWorkflow("wf-1", "Translate",
            [new McpWorkflowParameter("documentId", "DocumentId", true, "", "")]);
        McpCommandService commands = CreateService(new StubReadApi(), workflows: workflows);

        McpCommandResult<McpSendMeta, McpSendResult> result = await commands.SendAsync(
            new McpSendRequest("start", "patchouli://workflows/wf-1",
                [new McpSendParameter("documentId", "doc-1")]));

        result.IsSuccess.Should().BeTrue();
        result.Envelope!.Continuation.Should().BeNull();
        result.Envelope.Message.Should().BeNull();
        result.Envelope.Meta.Instruction.Should().Be("start");
        result.Envelope.Meta.Accepted.Should().BeTrue();
        result.Envelope.Meta.Processed.Should().Be("done");
        McpSendResult entry = result.Envelope.Entries.Should().ContainSingle().Subject;
        entry.Accepted.Should().BeTrue();
        entry.Processed.Should().Be("done");
        entry.Duplicate.Should().BeFalse();
        entry.MessageId.Should().BeNull();
        entry.SessionUri.Should().Be("patchouli://runs/agent/wf-1-session");
        workflows.Launches.Should().ContainSingle();
        workflows.Launches[0].WorkflowId.Should().Be("wf-1");
        workflows.Launches[0].Parameters.Should().ContainKey("documentId").WhoseValue.Should().Be("doc-1");
    }

    [Fact]
    public async Task Send_start_unknown_workflow_returns_workflow_not_found()
    {
        McpCommandService commands = CreateService(new StubReadApi(), workflows: new StubWorkflowRunsApi());

        McpCommandResult<McpSendMeta, McpSendResult> result = await commands.SendAsync(
            new McpSendRequest("start", "patchouli://workflows/missing"));

        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be((int)McpErrorCode.WorkflowNotFound);
        result.Error.Name.Should().Be("WORKFLOW_NOT_FOUND");
    }

    [Fact]
    public async Task Send_start_illegal_parameters_return_invalid_argument()
    {
        StubWorkflowRunsApi workflows = new();
        workflows.SetWorkflow("wf-1", "Translate",
            [new McpWorkflowParameter("documentId", "DocumentId", true, "", "")]);
        McpCommandService commands = CreateService(new StubReadApi(), workflows: workflows);

        McpCommandResult<McpSendMeta, McpSendResult> missingRequired = await commands.SendAsync(
            new McpSendRequest("start", "patchouli://workflows/wf-1"));
        missingRequired.Error!.Code.Should().Be((int)McpErrorCode.InvalidArgument);

        McpCommandResult<McpSendMeta, McpSendResult> unknown = await commands.SendAsync(
            new McpSendRequest("start", "patchouli://workflows/wf-1",
                [new McpSendParameter("bogus", "x"), new McpSendParameter("documentId", "d")]));
        unknown.Error!.Code.Should().Be((int)McpErrorCode.InvalidArgument);
        workflows.Launches.Should().BeEmpty();
    }

    [Fact]
    public async Task Send_message_accepts_into_inbox_and_reports_pending()
    {
        StubAgentRunsApi agent = new();
        agent.SetSession(SessionId, "running");
        McpCommandService commands = CreateService(new StubReadApi(), agent);

        McpCommandResult<McpSendMeta, McpSendResult> result = await commands.SendAsync(
            new McpSendRequest("message", Session: McpResourceUris.AgentRunUri(SessionId),
                MessageId: "m-1", Text: "please continue"));

        result.IsSuccess.Should().BeTrue();
        result.Envelope!.Meta.Processed.Should().Be("pending");
        McpSendResult entry = result.Envelope.Entries.Should().ContainSingle().Subject;
        entry.Accepted.Should().BeTrue();
        entry.Processed.Should().Be("pending");
        entry.Duplicate.Should().BeFalse();
        entry.MessageId.Should().Be("m-1");
        entry.SessionUri.Should().Be(McpResourceUris.AgentRunUri(SessionId));
        agent.Messages.Should().ContainSingle()
            .Which.Should().Be((SessionId, "m-1", "please continue"));
    }

    [Fact]
    public async Task Send_message_deduplicates_message_id_without_appending_twice()
    {
        StubAgentRunsApi agent = new();
        agent.SetSession(SessionId, "running");
        McpCommandService commands = CreateService(new StubReadApi(), agent);
        McpSendRequest request = new("message", Session: McpResourceUris.AgentRunStatusUri(SessionId),
            MessageId: "m-1", Text: "hello");

        McpCommandResult<McpSendMeta, McpSendResult> first = await commands.SendAsync(request);
        first.IsSuccess.Should().BeTrue();
        McpCommandResult<McpSendMeta, McpSendResult> retry = await commands.SendAsync(request);

        retry.IsSuccess.Should().BeTrue();
        retry.Envelope!.Message.Should().NotBeNull();
        retry.Envelope.Message!.Error.Should().BeNull();
        retry.Envelope.Message.Warnings.Should().ContainSingle()
            .Which.Should().StartWith("DUPLICATE_MESSAGE_ID:");
        McpSendResult entry = retry.Envelope.Entries.Should().ContainSingle().Subject;
        entry.Duplicate.Should().BeTrue();
        entry.Accepted.Should().BeTrue();
        entry.Processed.Should().Be("pending");
        agent.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task Send_message_unknown_session_returns_session_not_found()
    {
        McpCommandService commands = CreateService(new StubReadApi(), new StubAgentRunsApi());

        McpCommandResult<McpSendMeta, McpSendResult> result = await commands.SendAsync(
            new McpSendRequest("message", Session: McpResourceUris.AgentRunUri("missing"),
                MessageId: "m-1", Text: "x"));

        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be((int)McpErrorCode.SessionNotFound);
        result.Error.Name.Should().Be("SESSION_NOT_FOUND");
    }

    [Fact]
    public async Task Send_message_to_non_running_session_returns_session_state_invalid()
    {
        StubAgentRunsApi agent = new();
        agent.SetSession(SessionId, "finished");
        McpCommandService commands = CreateService(new StubReadApi(), agent);

        McpCommandResult<McpSendMeta, McpSendResult> result = await commands.SendAsync(
            new McpSendRequest("message", Session: McpResourceUris.AgentRunUri(SessionId),
                MessageId: "m-1", Text: "x"));

        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be((int)McpErrorCode.SessionStateInvalid);
        agent.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task Send_cancel_is_processed_immediately_and_rejects_terminal_sessions()
    {
        StubAgentRunsApi agent = new();
        agent.SetSession(SessionId, "running");
        McpCommandService commands = CreateService(new StubReadApi(), agent);

        McpCommandResult<McpSendMeta, McpSendResult> cancelled = await commands.SendAsync(
            new McpSendRequest("cancel", Session: McpResourceUris.AgentRunUri(SessionId)));
        cancelled.IsSuccess.Should().BeTrue();
        cancelled.Envelope!.Meta.Processed.Should().Be("done");
        cancelled.Envelope.Entries[0].Processed.Should().Be("done");
        agent.Sessions[SessionId].Status.Should().Be("cancelled");

        McpCommandResult<McpSendMeta, McpSendResult> again = await commands.SendAsync(
            new McpSendRequest("cancel", Session: McpResourceUris.AgentRunUri(SessionId)));
        again.Error!.Code.Should().Be((int)McpErrorCode.SessionStateInvalid);
    }

    [Fact]
    public async Task Send_resume_continues_stopped_sessions_and_rejects_cancelled_or_finished()
    {
        StubAgentRunsApi agent = new();
        agent.SetSession(SessionId, "stopped");
        McpCommandService commands = CreateService(new StubReadApi(), agent);

        McpCommandResult<McpSendMeta, McpSendResult> resumed = await commands.SendAsync(
            new McpSendRequest("resume", Session: McpResourceUris.AgentRunUri(SessionId)));
        resumed.IsSuccess.Should().BeTrue();
        resumed.Envelope!.Meta.Processed.Should().Be("done");
        agent.Sessions[SessionId].Status.Should().Be("running");

        agent.SetSession(SessionId, "cancelled");
        McpCommandResult<McpSendMeta, McpSendResult> cancelled = await commands.SendAsync(
            new McpSendRequest("resume", Session: McpResourceUris.AgentRunUri(SessionId)));
        cancelled.Error!.Code.Should().Be((int)McpErrorCode.SessionStateInvalid);

        agent.SetSession(SessionId, "finished");
        McpCommandResult<McpSendMeta, McpSendResult> finished = await commands.SendAsync(
            new McpSendRequest("resume", Session: McpResourceUris.AgentRunUri(SessionId)));
        finished.Error!.Code.Should().Be((int)McpErrorCode.SessionStateInvalid);
    }

    [Fact]
    public async Task Send_unknown_verb_returns_invalid_argument()
    {
        McpCommandService commands = CreateService(new StubReadApi());

        McpCommandResult<McpSendMeta, McpSendResult> result =
            await commands.SendAsync(new McpSendRequest("restart"));

        result.Error!.Code.Should().Be((int)McpErrorCode.InvalidArgument);
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

        McpCommandResult<McpSendMeta, McpSendResult> denied = await commands.SendAsync(
            new McpSendRequest("cancel", Session: McpResourceUris.AgentRunUri(SessionId)));
        denied.Error.Should().NotBeNull();
        denied.Error!.Code.Should().Be((int)McpErrorCode.PermissionDenied);
        denied.Error.Name.Should().Be("PERMISSION_DENIED");

        McpCommandResult<McpFindMeta, object> find = await commands.FindAsync(new McpFindRequest(null, null, null));
        find.IsSuccess.Should().BeTrue();
        find.Envelope!.Entries.Should().HaveCount(7);

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

    [Fact]
    public void Cli_send_start_maps_to_the_send_tool_request()
    {
        CliToolCall call = CliArguments.BuildToolCall("send",
            ["start", "--workflow", "patchouli://workflows/wf-1", "--param", "documentId=doc-1"], true);

        call.Tool.Should().Be(CliArguments.Send);
        call.Arguments["instruction"].Should().Be("start");
        call.Arguments["workflow"].Should().Be("patchouli://workflows/wf-1");
        call.Arguments["parameters"].Should().BeEquivalentTo((object)new[] { "documentId=doc-1" });
        call.Arguments["format"].Should().Be("json");
        call.Arguments.Should().NotContainKey("message_id").And.NotContainKey("session");
    }

    [Fact]
    public void Cli_send_message_cancel_resume_map_their_arguments()
    {
        CliToolCall message = CliArguments.BuildToolCall("send",
            ["message", "--session", "patchouli://runs/agent/s-1", "--message-id", "m-1", "--text", "go on"],
            false);
        message.Tool.Should().Be(CliArguments.Send);
        message.Arguments["instruction"].Should().Be("message");
        message.Arguments["session"].Should().Be("patchouli://runs/agent/s-1");
        message.Arguments["message_id"].Should().Be("m-1");
        message.Arguments["text"].Should().Be("go on");

        CliToolCall cancel = CliArguments.BuildToolCall("send",
            ["cancel", "--session", "patchouli://runs/agent/s-1"], false);
        cancel.Arguments["instruction"].Should().Be("cancel");

        CliToolCall resume = CliArguments.BuildToolCall("send",
            ["resume", "--session", "patchouli://runs/agent/s-1/status"], false);
        resume.Arguments["instruction"].Should().Be("resume");
    }

    [Fact]
    public void Cli_send_rejects_missing_or_unknown_verbs()
    {
        Action missing = () => CliArguments.BuildToolCall("send", [], false);
        missing.Should().Throw<CliUsageException>();

        Action unknown = () => CliArguments.BuildToolCall("send", ["restart"], false);
        unknown.Should().Throw<CliUsageException>();
    }

    private static McpCommandService CreateService(StubReadApi read, StubAgentRunsApi? agent = null,
        StubWorkflowRunsApi? workflows = null, StubOcrRunsApi? ocr = null, bool putEnabled = true,
        bool sendEnabled = true, StubWriteApi? write = null,
        Func<CancellationToken, Task<McpServerSettings>>? permissions = null)
    {
        return new McpCommandService(read, write ?? new StubWriteApi(), new StubBiblatexImportService(),
            new StubItemService(), new StubEvidenceReader(), putEnabled: putEnabled, sendEnabled: sendEnabled,
            agentRuns: agent, workflowRuns: workflows, ocrRuns: ocr, permissionSettings: permissions);
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
