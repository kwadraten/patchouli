using System.Text.Json;
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
            PutArguments(fixture.PageUri, fixture.Translate("第一版")), CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.Payload);
        outcome.ErrorCode.Should().BeNull();
        using (JsonDocument payload = JsonDocument.Parse(outcome.Payload))
        {
            JsonElement entry = payload.RootElement.GetProperty("entries")[0];
            entry.GetProperty("uri").GetString().Should().Be(fixture.PageUri);
            entry.GetProperty("resource_type").GetString().Should().Be("translation_page");
            entry.GetProperty("committed").GetBoolean().Should().BeTrue();
        }

        // The write reached the one channel both routes share: the translation service itself now
        // serves the page, and a second tool put replaces it as a whole resource.
        (await fixture.StoredTranslationAsync()).Should().Contain("第一版");
        AgentToolOutcome replaced = await gateway.CallToolAsync("put",
            PutArguments(fixture.PageUri, fixture.Translate("第二版")), CancellationToken.None);
        replaced.Succeeded.Should().BeTrue(replaced.Payload);
        (await fixture.StoredTranslationAsync()).Should().Contain("第二版").And.NotContain("第一版");
    }

    [Fact]
    public async Task Put_tool_returns_the_validation_failure_list_in_its_json_payload_and_writes_nothing()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        IAgentMcpGateway gateway = new McpCommandServiceAgentGateway(fixture.Commands);

        AgentToolOutcome outcome = await gateway.CallToolAsync("put",
            PutArguments(fixture.PageUri, fixture.Translate("译文") + "\n\n多余的段落。"), CancellationToken.None);

        outcome.ErrorCode.Should().Be("INVALID_CONTENT");
        outcome.Succeeded.Should().BeFalse();
        using (JsonDocument payload = JsonDocument.Parse(outcome.Payload))
        {
            JsonElement entry = payload.RootElement.GetProperty("entries")[0];
            entry.GetProperty("committed").GetBoolean().Should().BeFalse();
            JsonElement errors = entry.GetProperty("translation_errors");
            errors.GetArrayLength().Should().BeGreaterThan(0);
            JsonElement first = errors[0];
            first.GetProperty("expected").GetString().Should().NotBeNullOrWhiteSpace();
            first.GetProperty("actual").GetString().Should().NotBeNullOrWhiteSpace();
            first.GetProperty("block_index").GetInt32().Should().BeGreaterThanOrEqualTo(0);

            // The envelope also carries the sanitized terminal line of the refusal, which is the
            // fallback detail for a reply that has no mismatch list.
            payload.RootElement.GetProperty("message").GetProperty("error").GetString()
                .Should().Contain("INVALID_CONTENT");
        }

        (await fixture.StoredTranslationAsync()).Should().BeNull("a rejected put must not persist a translation");
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

    /// <summary>
    ///     Composes the real command service over one committed source page, so a tool call exercises
    ///     production permissions, validation and the real translation write channel.
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporarySqliteDatabase _database;

        private Fixture(TemporarySqliteDatabase database, McpCommandService commands,
            PageTranslationService translations, DocumentInstanceId documentId, PageId pageId, string pageUri,
            string source)
        {
            _database = database;
            Commands = commands;
            Translations = translations;
            DocumentId = documentId;
            PageId = pageId;
            PageUri = pageUri;
            Source = source;
        }

        public McpCommandService Commands { get; }

        public PageTranslationService Translations { get; }

        public DocumentInstanceId DocumentId { get; }

        public PageId PageId { get; }

        public string PageUri { get; }

        public string Source { get; }

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
            DocumentTreeRevision revision = await CommitSourcePageAsync(trees, document.DocumentInstanceId,
                page.PageId);
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
                new CslStyleStore(database.ConnectionFactory, clock), translations);
            BiblatexImportService biblatex = new(new BiblatexHelperClient(), items,
                new FileAssetService(database.ConnectionFactory, libraries, clock), documents);
            IVersionedEvidenceReader evidence = new VersionedEvidenceReader(database.ConnectionFactory, libraries,
                trees, markdownCompiler);
            McpCommandService commands = new(read, writes, biblatex, items, evidence);

            return new Fixture(database, commands, translations, document.DocumentInstanceId, page.PageId,
                McpResourceUris.TranslationPageUri(document.DocumentInstanceId, 1), source.Markdown);
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
            DocumentInstanceId documentId, PageId pageId)
        {
            Result<DocumentTreeRevision> working = await trees.BeginWorkingRevisionAsync(documentId, pageId,
                [
                    new DocumentBoxSeed(DocumentBoxId.New(), null, 0, DocumentBoxType.Text, null, null,
                        new NormalizedBBox(.1, .1, .8, .05), new TextBoxPayload("Original paragraph"))
                ],
                DocumentTreeRevisionSource.Import);
            Result<DocumentTreeRevision> committed =
                await trees.CommitWorkingRevisionAsync(working.Value.TreeRevisionId);
            return committed.Value;
        }
    }
}
