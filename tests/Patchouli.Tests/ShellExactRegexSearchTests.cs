using FluentAssertions;
using System.Text.Json;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Evidence;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Evidence;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Search;
using Patchouli.Infrastructure.Shell;
using Patchouli.Mcp;
using Patchouli.Search;

namespace Patchouli.Tests;

public sealed class ShellExactRegexSearchTests
{
    [Fact]
    public async Task Exact_search_supports_regex_alternation_and_scope()
    {
        await using Fixture fx = await Fixture.CreateAsync();

        Result<JsonElement> result = await fx.Domain.HandleAsync(
            "search.exact",
            JsonSerializer.SerializeToElement(new
            {
                query = "信濃蘭学|種痘|熊谷珪碩",
                scope = $"/texts/{fx.DocumentInstanceId}",
                limit = 100
            }));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        JsonElement[] matches = result.Value.GetProperty("matches").EnumerateArray().ToArray();
        matches.Should().NotBeEmpty();
        matches.Select(m => m.GetProperty("preview").GetString() ?? "")
            .Should().Contain(p => p.Contains("種痘", StringComparison.Ordinal));
        string documentInstanceId = fx.DocumentInstanceId.ToString();
        matches.Should().OnlyContain(m =>
            (m.GetProperty("uri").GetString() ?? "").Contains(documentInstanceId,
                StringComparison.OrdinalIgnoreCase));
        matches.Should().OnlyContain(m =>
            (m.GetProperty("uri").GetString() ?? "").Contains("evref=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Exact_search_reports_invalid_regex()
    {
        await using Fixture fx = await Fixture.CreateAsync();
        Result<JsonElement> result = await fx.Domain.HandleAsync(
            "search.exact",
            JsonSerializer.SerializeToElement(new { query = "(", limit = 10 }));
        result.IsFailure.Should().BeTrue();
        result.ErrorMessage.Should().Contain("invalid regex");
    }

    [Fact]
    public async Task Exact_search_emits_multiple_matches_in_one_unit()
    {
        await using Fixture fx = await Fixture.CreateAsync("foo bar foo baz");
        Result<JsonElement> result = await fx.Domain.HandleAsync(
            "search.exact",
            JsonSerializer.SerializeToElement(new
            {
                query = "foo",
                scope = $"/texts/{fx.DocumentInstanceId}",
                limit = 100
            }));
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.GetProperty("matches").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Exact_search_batches_distinct_matching_units_once()
    {
        await using Fixture fx = await Fixture.CreateAsync("foo bar foo baz");

        Result<JsonElement> result = await fx.Domain.HandleAsync(
            "search.exact",
            JsonSerializer.SerializeToElement(new
            {
                query = "foo",
                scope = $"/texts/{fx.DocumentInstanceId}",
                limit = 100
            }));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        fx.Evidence.BatchCallCount.Should().Be(1);
        fx.Evidence.LastBatch.Should().ContainSingle();
    }

    [Fact]
    public async Task Exact_search_stops_at_limit_and_reports_more_matches()
    {
        await using Fixture fx = await Fixture.CreateAsync("foo foo foo");
        Result<JsonElement> result = await fx.Domain.HandleAsync(
            "search.exact",
            JsonSerializer.SerializeToElement(new
            {
                query = "foo",
                scope = $"/texts/{fx.DocumentInstanceId}",
                limit = 2
            }));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.GetProperty("matches").GetArrayLength().Should().Be(2);
        result.Value.GetProperty("truncated").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Evidence_requires_one_evref_on_a_matching_text_page()
    {
        await using Fixture fx = await Fixture.CreateAsync("precise evidence text");
        string uri = await SearchUriAsync(fx, "evidence");

        Result<JsonElement> valid = await fx.Domain.HandleAsync(
            "evidence.resolve",
            JsonSerializer.SerializeToElement(new { uri }));
        valid.IsSuccess.Should().BeTrue(valid.ErrorMessage);
        valid.Value.GetProperty("text").GetString().Should().Be("precise evidence text");

        Result<JsonElement> duplicate = await fx.Domain.HandleAsync(
            "evidence.resolve",
            JsonSerializer.SerializeToElement(new { uri = $"{uri}&evref=duplicate" }));
        duplicate.IsFailure.Should().BeTrue();
        duplicate.ErrorCode.Should().Be(AppErrorCodes.InvalidEvref);

        Result<JsonElement> empty = await fx.Domain.HandleAsync(
            "evidence.resolve",
            JsonSerializer.SerializeToElement(new
            {
                uri = $"patchouli://texts/{fx.DocumentInstanceId}/page-0.md?evref="
            }));
        empty.IsFailure.Should().BeTrue();
        empty.ErrorCode.Should().Be(AppErrorCodes.InvalidEvref);
    }

    [Fact]
    public async Task Evidence_rejects_non_text_and_mismatched_resource_targets()
    {
        await using Fixture fx = await Fixture.CreateAsync("resource evidence text");
        string uri = await SearchUriAsync(fx, "resource");
        string encodedEvref = uri[(uri.IndexOf("?evref=", StringComparison.Ordinal) + 7)..];

        Result<JsonElement> unsupported = await fx.Domain.HandleAsync(
            "evidence.resolve",
            JsonSerializer.SerializeToElement(new
            {
                uri = $"patchouli://items/{Guid.NewGuid():D}.bib?evref={encodedEvref}"
            }));
        unsupported.IsFailure.Should().BeTrue();
        unsupported.ErrorCode.Should().Be(AppErrorCodes.UnsupportedEvrefTarget);

        Result<JsonElement> mismatch = await fx.Domain.HandleAsync(
            "evidence.resolve",
            JsonSerializer.SerializeToElement(new
            {
                uri = $"patchouli://texts/{Guid.NewGuid():D}/page-0.md?evref={encodedEvref}"
            }));
        mismatch.IsFailure.Should().BeTrue();
        mismatch.ErrorCode.Should().Be(AppErrorCodes.EvidenceResourceMismatch);
    }

    [Fact]
    public async Task Evidence_batch_preserves_uri_order_values_and_independent_errors()
    {
        await using Fixture fx = await Fixture.CreateAsync("batch evidence text");
        string validUri = await SearchUriAsync(fx, "batch");
        string invalidUri = $"patchouli://texts/{fx.DocumentInstanceId}/page-0.md?evref=invalid";
        string[] uris = [validUri, invalidUri, validUri];

        Result<JsonElement> batch = await fx.Domain.HandleAsync(
            "evidence.resolve_many",
            JsonSerializer.SerializeToElement(new { uris }));

        batch.IsSuccess.Should().BeTrue(batch.ErrorMessage);
        JsonElement[] results = batch.Value.GetProperty("results").EnumerateArray().ToArray();
        results.Select(result => result.GetProperty("uri").GetString()).Should().Equal(uris);
        results.Select(result => result.GetProperty("ok").GetBoolean()).Should().Equal(true, false, true);
        results[0].GetProperty("value").GetProperty("text").GetString().Should().Be("batch evidence text");
        results[1].GetProperty("error").GetProperty("code").GetString().Should().Be(AppErrorCodes.InvalidEvref);
        results[2].GetProperty("value").GetProperty("text").GetString().Should().Be("batch evidence text");

        Result<JsonElement> tooMany = await fx.Domain.HandleAsync(
            "evidence.resolve_many",
            JsonSerializer.SerializeToElement(new { uris = Enumerable.Repeat(validUri, 65).ToArray() }));
        tooMany.IsFailure.Should().BeTrue();
        tooMany.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    private static async Task<string> SearchUriAsync(Fixture fixture, string query)
    {
        Result<JsonElement> search = await fixture.Domain.HandleAsync(
            "search.exact",
            JsonSerializer.SerializeToElement(new
            {
                query,
                scope = $"/texts/{fixture.DocumentInstanceId}",
                limit = 10
            }));
        search.IsSuccess.Should().BeTrue(search.ErrorMessage);
        return search.Value.GetProperty("matches")[0].GetProperty("uri").GetString()!;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            TemporarySqliteDatabase database,
            ShellDomainService domain,
            DocumentInstanceId documentInstanceId,
            TrackingEvidenceReferenceService evidence)
        {
            Database = database;
            Domain = domain;
            DocumentInstanceId = documentInstanceId;
            Evidence = evidence;
        }

        public TemporarySqliteDatabase Database { get; }
        public ShellDomainService Domain { get; }
        public DocumentInstanceId DocumentInstanceId { get; }
        public TrackingEvidenceReferenceService Evidence { get; }

        public static Task<Fixture> CreateAsync(string? text = null)
        {
            return CreateCoreAsync(text ?? "信濃蘭学の記録。翌年種痘を行う。門人に熊谷珪碩あり。");
        }

        private static async Task<Fixture> CreateCoreAsync(string text)
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-07-28T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService library = new(database.ConnectionFactory, clock);
            (await library.CreateLibraryAsync("Shell regex")).IsSuccess.Should().BeTrue();
            ItemService items = new(database.ConnectionFactory, library, clock);
            Result<ItemMetadata> item = await items.CreateItemAsync("book", "Regex Doc");
            item.IsSuccess.Should().BeTrue();
            DocumentInstanceService documents = new(database.ConnectionFactory, clock);
            Result<DocumentInstance> doc =
                await documents.AttachDocumentInstanceAsync(item.Value.ItemId, null, DocumentInstanceType.PrimaryScan);
            doc.IsSuccess.Should().BeTrue();
            PageService pages = new(database.ConnectionFactory, clock);
            Result<Page> page = await pages.CreatePageAsync(
                doc.Value.DocumentInstanceId, 0, "1", null, null, 0, CoordinateBasis.NormalizedPage, null, null, "test",
                null);
            page.IsSuccess.Should().BeTrue();
            await BoxTreeTestData.CommitTextAsync(database.ConnectionFactory, clock, doc.Value.DocumentInstanceId,
                page.Value.PageId, text);
            SearchUnitBuilder units = new(database.ConnectionFactory, clock);
            SearchIndexRebuilder index = new(database.ConnectionFactory, clock);
            (await units.RebuildForDocumentInstanceAsync(doc.Value.DocumentInstanceId)).IsSuccess.Should().BeTrue();
            await index.RebuildFtsForDocumentInstanceAsync(doc.Value.DocumentInstanceId);

            SearchProfileService profiles = new(database.ConnectionFactory, library, clock);
            SqliteSearchService search = new(database.ConnectionFactory, profiles);
            TrackingEvidenceReferenceService evidence = new(new EvidenceReferenceService(database.ConnectionFactory,
                clock));
            McpReadApi api = new(database.ConnectionFactory, search, evidence);
            ShellDomainService domain = new(database.ConnectionFactory, api, search, evidence, library: library,
                items: items);
            return new Fixture(database, domain, doc.Value.DocumentInstanceId, evidence);
        }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }

    private sealed class TrackingEvidenceReferenceService(IEvidenceReferenceService inner)
        : IEvidenceReferenceService
    {
        public int BatchCallCount { get; private set; }
        public IReadOnlyList<SearchUnitId> LastBatch { get; private set; } = [];

        public Task<Result<EvidenceRefRecord>> CreateFromSearchUnitAsync(SearchUnitId unitId,
            CancellationToken cancellationToken = default)
        {
            return inner.CreateFromSearchUnitAsync(unitId, cancellationToken);
        }

        public Task<Result<IReadOnlyList<EvidenceReferenceCreateResult>>> CreateFromSearchUnitsAsync(
            IReadOnlyList<SearchUnitId> unitIds, CancellationToken cancellationToken = default)
        {
            BatchCallCount++;
            LastBatch = unitIds.ToArray();
            return inner.CreateFromSearchUnitsAsync(unitIds, cancellationToken);
        }

        public Task<Result<EvidenceResolutionResult>> ResolveAsync(string evidenceRefId,
            string mode = EvidenceResolutionMode.Pinned, CancellationToken cancellationToken = default)
        {
            return inner.ResolveAsync(evidenceRefId, mode, cancellationToken);
        }

        public Task<Result<EvidenceMarkdown>> CreateMarkdownAsync(string evidenceRefId,
            CancellationToken cancellationToken = default)
        {
            return inner.CreateMarkdownAsync(evidenceRefId, cancellationToken);
        }

        public Task<Result> MarkSupersededAsync(string evidenceRefId, string successorEvidenceRefId, string reason,
            CancellationToken cancellationToken = default)
        {
            return inner.MarkSupersededAsync(evidenceRefId, successorEvidenceRefId, reason, cancellationToken);
        }

        public Task<Result> TombstoneAsync(string evidenceRefId, string reason,
            CancellationToken cancellationToken = default)
        {
            return inner.TombstoneAsync(evidenceRefId, reason, cancellationToken);
        }

        public Task<Result> PurgeAsync(string evidenceRefId, string reason,
            CancellationToken cancellationToken = default)
        {
            return inner.PurgeAsync(evidenceRefId, reason, cancellationToken);
        }
    }
}
