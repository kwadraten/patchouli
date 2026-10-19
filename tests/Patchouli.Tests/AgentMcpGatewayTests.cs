using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Host.Agent;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Bibliography.Biblatex;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Documents.Translations;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Search;
using Patchouli.Mcp;

namespace Patchouli.Tests;

/// <summary>
///     Verifies the agent-facing MCP tool surface of <see cref="McpCommandServiceAgentGateway" /> over
///     the real command service: the built-in translation script writes a page through the
///     <c>put</c> tool, so that tool must reach the same atomic write channel as the dedicated put
///     effect and answer with the JSON envelope that carries the commit flag and, on a structural
///     rejection, the host's complete <c>translation_errors</c> list (ADR 0034). A request-level
///     refusal must stay a JSON error envelope: a payload a caller cannot parse as a put answer would
///     make it fall back to a second write path around an explicit refusal.
/// </summary>
public sealed class AgentMcpGatewayTests
{
    [Fact]
    public async Task Generic_send_gateway_enforces_exact_action_grants_before_domain_execution()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        IAgentMcpGateway gateway = new McpCommandServiceAgentGateway(fixture.Commands);
        AgentToolOutcome outcome = await gateway.CallToolAsync("send", JsonSerializer.Serialize(new
        {
            target = $"patchouli://texts/{fixture.DocumentId}/", action = "ocr.start", args = new { }
        }), CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.ErrorCode.Should().Be("PERMISSION_DENIED");
        (await fixture.StoredTranslationAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Legacy_send_gateway_request_is_rejected_with_new_contract_hint()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        IAgentMcpGateway gateway = new McpCommandServiceAgentGateway(fixture.Commands);
        AgentToolOutcome outcome = await gateway.CallToolAsync("send",
            """{"instruction":"start","workflow":"patchouli://workflows/translate","parameters":[]}""",
            CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.ErrorCode.Should().Be("INVALID_ARGUMENT");
        outcome.Payload.ToLowerInvariant().Should().Contain("target").And.Contain("action");
        (await fixture.StoredTranslationAsync()).Should().BeNull();
    }

    [Theory]
    [InlineData("fetch", "{}", "$.arguments.uris")]
    [InlineData("fetch", "{\"uris\":[42]}", "expected string")]
    [InlineData("put", "{\"uri\":1,\"content\":\"x\"}", "expected string")]
    [InlineData("find", "{\"limit\":\"ten\"}", "expected integer")]
    [InlineData("cite", "{\"refs\":[],\"html\":0}", "expected boolean")]
    [InlineData("find", "{\"unexpected\":true}", "Unknown field")]
    [InlineData("find", "{\"query\":\"a\",\"query\":\"b\"}", "Duplicate field")]
    public async Task Invalid_tool_argument_types_are_rejected_with_precise_errors(string tool, string arguments,
        string error)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        IAgentMcpGateway gateway = new McpCommandServiceAgentGateway(fixture.Commands);
        AgentToolOutcome outcome = await gateway.CallToolAsync(tool, arguments, CancellationToken.None);
        outcome.Succeeded.Should().BeFalse();
        outcome.ErrorCode.Should().Be("INVALID_ARGUMENT");
        outcome.Payload.Should().Contain(error);
        (await fixture.StoredTranslationAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Put_tool_writes_through_the_same_atomic_channel_and_reports_committed()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        IAgentMcpGateway gateway = new McpCommandServiceAgentGateway(fixture.Commands);

        AgentToolOutcome outcome = await gateway.CallToolAsync("put",
            PutBoxesArguments(fixture, "第一版"), CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.Payload);
        outcome.ErrorCode.Should().BeNull();
        using (JsonDocument payload = JsonDocument.Parse(outcome.Payload))
        {
            JsonElement entry = payload.RootElement.GetProperty("entries")[0];
            entry.GetProperty("uri").GetString().Should().Be(fixture.PageUri);
            entry.GetProperty("resource_type").GetString().Should().Be("translation_page");
            entry.GetProperty("committed").GetBoolean().Should().BeTrue();
            entry.GetProperty("source_revision").GetString().Should().Be(fixture.SourceRevision);
            entry.GetProperty("translation_version").GetInt32().Should().Be(1);
            entry.GetProperty("modified_box_ids").EnumerateArray().Select(value => value.GetString())
                .Should().Equal(fixture.BoxId);
        }

        // The write reached the one channel both routes share: the translation service itself now
        // serves the page, and a second tool put replaces it as a whole resource.
        (await fixture.StoredTranslationAsync()).Should().Contain("第一版");
        AgentToolOutcome replaced = await gateway.CallToolAsync("put",
            PutBoxesArguments(fixture, "第二版", 1), CancellationToken.None);
        replaced.Succeeded.Should().BeTrue(replaced.Payload);
        (await fixture.StoredTranslationAsync()).Should().Contain("第二版").And.NotContain("第一版");
    }

    [Fact]
    public async Task Put_tool_rejects_a_box_that_does_not_belong_to_the_source_page()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        IAgentMcpGateway gateway = new McpCommandServiceAgentGateway(fixture.Commands);

        AgentToolOutcome outcome = await gateway.CallToolAsync("put",
            PutBoxesArguments(fixture, "译文", boxId: Guid.NewGuid().ToString()), CancellationToken.None);

        outcome.ErrorCode.Should().Be("INVALID_CONTENT");
        outcome.Succeeded.Should().BeFalse();
        using (JsonDocument payload = JsonDocument.Parse(outcome.Payload))
        {
            JsonElement entry = payload.RootElement.GetProperty("entries")[0];
            entry.GetProperty("committed").GetBoolean().Should().BeFalse();
            payload.RootElement.GetProperty("message").GetProperty("error").GetString()
                .Should().Contain("INVALID_CONTENT");
        }

        (await fixture.StoredTranslationAsync()).Should().BeNull("a rejected put must not persist a translation");
    }

    [Fact]
    public async Task Legacy_markdown_page_uri_returns_canonical_xml_uri_and_warning()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        IAgentMcpGateway gateway = new McpCommandServiceAgentGateway(fixture.Commands);
        string legacyUri = fixture.PageUri.Replace(".xml", ".md", StringComparison.Ordinal);

        AgentToolOutcome outcome = await gateway.CallToolAsync("put",
            PutArguments(legacyUri, fixture.Translate("旧版译文")), CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.Payload);
        using JsonDocument payload = JsonDocument.Parse(outcome.Payload);
        payload.RootElement.GetProperty("entries")[0].GetProperty("uri").GetString().Should().Be(fixture.PageUri);
        payload.RootElement.GetProperty("message").GetRawText().Should().Contain("[obsoleted]")
            .And.Contain("0.4.0").And.Contain(fixture.PageUri);
        string stored = await fixture.StoredTranslationAsync()
                        ?? throw new InvalidOperationException("The legacy translation was not persisted.");
        stored.Should().Contain("旧版译文").And.NotContain("[obsoleted]");
    }

    [Fact]
    public async Task Put_tool_passes_a_refusal_through_as_a_json_error_envelope()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        IAgentMcpGateway gateway = new McpCommandServiceAgentGateway(fixture.Commands);

        AgentToolOutcome outcome = await gateway.CallToolAsync("put",
            PutArguments(McpResourceUris.TranslationDocumentUri(fixture.DocumentId), "x"), CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.ErrorCode.Should().Be("PERMISSION_DENIED");
        using JsonDocument payload = JsonDocument.Parse(outcome.Payload);
        payload.RootElement.GetProperty("entries").GetArrayLength().Should().Be(0);
        payload.RootElement.GetProperty("message").GetProperty("error").GetString()
            .Should().Contain("PERMISSION_DENIED");
    }

    private static string PutArguments(string uri, string content)
    {
        return JsonSerializer.Serialize(new { uri, content, format = "json" });
    }

    private static string PutBoxesArguments(Fixture fixture, string content, int translationVersion = 0,
        string? boxId = null)
    {
        string replacementBox = new XElement("box",
                new XAttribute("id", boxId ?? fixture.BoxId),
                new XAttribute("type", "text"),
                new XAttribute("sub_type", string.Empty),
                new XAttribute("heading_level", string.Empty),
                new XAttribute("code_language", string.Empty),
                new XAttribute("suppressed", "false"),
                new XAttribute("readonly", "false"),
                new XAttribute("translated", "true"),
                new XElement("payload", new XAttribute("format", "markdown"), content))
            .ToString(SaveOptions.DisableFormatting);
        return JsonSerializer.Serialize(new
        {
            uri = fixture.PageUri,
            source_revision = fixture.SourceRevision,
            translation_version = translationVersion,
            boxes = new[] { new { box_id = boxId ?? fixture.BoxId, content = replacementBox } },
            format = "json"
        });
    }

    /// <summary>
    ///     Composes the real command service over one committed source page, so a tool call exercises
    ///     production permissions, validation and the real translation write channel.
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporarySqliteDatabase _database;

        private Fixture(TemporarySqliteDatabase database, McpCommandService commands,
            PageTranslationService translations, DocumentInstanceId documentId, PageId pageId, string pageUri,
            string source, string sourceRevision, string boxId)
        {
            _database = database;
            Commands = commands;
            Translations = translations;
            DocumentId = documentId;
            PageId = pageId;
            PageUri = pageUri;
            Source = source;
            SourceRevision = sourceRevision;
            BoxId = boxId;
        }

        public McpCommandService Commands { get; }

        public PageTranslationService Translations { get; }

        public DocumentInstanceId DocumentId { get; }

        public PageId PageId { get; }

        public string PageUri { get; }

        public string Source { get; }

        public string SourceRevision { get; }

        public string BoxId { get; }

        public static async Task<Fixture> CreateAsync()
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
            await libraries.CreateLibraryAsync("Agent MCP gateway");
            ItemService items = new(database.ConnectionFactory, libraries, clock);
            ItemMetadata item = (await items.CreateItemAsync("document", "Gateway source")).Value;
            DocumentInstanceService documents = new(database.ConnectionFactory, clock);
            DocumentInstance document = (await documents.AttachDocumentInstanceAsync(item.ItemId, null,
                DocumentInstanceType.PrimaryScan, "Gateway source", true)).Value;
            Page page = (await new PageService(database.ConnectionFactory, clock).CreatePageAsync(
                document.DocumentInstanceId, 0, "1", null, null, 0, CoordinateBasis.NormalizedPage, null, null,
                "test", null)).Value;

            MarkdigMarkdownEngine markdown = new();
            DocumentTreeService trees = new(database.ConnectionFactory, clock, markdown);
            DocumentBoxId boxId = DocumentBoxId.New();
            DocumentTreeRevision revision = await CommitSourcePageAsync(trees, document.DocumentInstanceId,
                page.PageId, boxId);
            DocumentMarkdownCompiler markdownCompiler = new(trees, markdown);
            CompiledMarkdown source = (await markdownCompiler.CompilePageMarkdownAsync(revision.TreeRevisionId))
                .Value;

            PageTranslationCache cache = new();
            PageTranslationService translations = new(database.ConnectionFactory, trees, markdownCompiler, markdown,
                new CachedPageTranslationCompiler(
                    new PageTranslationCompiler(database.ConnectionFactory, trees, markdown), cache), cache, clock);

            McpReadApi read = new(database.ConnectionFactory, new SqliteSearchService(database.ConnectionFactory),
                markdown: markdown, markdownCompiler: markdownCompiler, pageTranslations: translations);
            McpWriteApi writes = new(items, new BiblatexHelperClient(),
                new CslStyleStore(database.ConnectionFactory, clock), translations, trees);
            BiblatexImportService biblatex = new(new BiblatexHelperClient(), items,
                new FileAssetService(database.ConnectionFactory, libraries, clock), documents);
            IVersionedEvidenceReader evidence = new VersionedEvidenceReader(database.ConnectionFactory, libraries,
                trees, markdownCompiler);
            McpCommandService commands = new(read, writes, biblatex, items, evidence);

            return new Fixture(database, commands, translations, document.DocumentInstanceId, page.PageId,
                McpResourceUris.TranslationPageUri(document.DocumentInstanceId, 1), source.Markdown,
                revision.TreeRevisionId.ToString(), boxId.ToString());
        }

        /// <summary>The translated Markdown the write channel really stored, or null when it stored none.</summary>
        public async Task<string?> StoredTranslationAsync()
        {
            TranslatedPageMarkdown? stored = await Translations.GetPageTranslationAsync(DocumentId, PageId);
            return stored?.Markdown;
        }

        /// <summary>Translates the committed source page while keeping its block structure.</summary>
        public string Translate(string marker)
        {
            return Source.Replace("Original paragraph", marker);
        }

        public ValueTask DisposeAsync()
        {
            return _database.DisposeAsync();
        }

        private static async Task<DocumentTreeRevision> CommitSourcePageAsync(DocumentTreeService trees,
            DocumentInstanceId documentId, PageId pageId, DocumentBoxId boxId)
        {
            Result<DocumentTreeRevision> working = await trees.BeginWorkingRevisionAsync(documentId, pageId,
                [
                    new DocumentBoxSeed(boxId, null, 0, DocumentBoxType.Text, null, new NormalizedBBox(.1, .1, .8, .05),
                        new TextBoxPayload("Original paragraph"))
                ],
                DocumentTreeRevisionSource.Import);
            Result<DocumentTreeRevision> committed =
                await trees.CommitWorkingRevisionAsync(working.Value.TreeRevisionId);
            return committed.Value;
        }
    }
}
