using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Core.Evidence;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Coordinates;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Evidence;
using Patchouli.Infrastructure.Files;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Mcp;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Search;
using Patchouli.Mcp;
using Patchouli.Ocr;

namespace Patchouli.Tests;

public sealed class McpSourceValidationTests
{
    [Fact]
    public async Task Pure_text_reads_never_trigger_full_hash_and_bbox_reads_share_cached_validation()
    {
        await using Context context = await Context.CreateAsync();

        McpPageTextResponse text = (await context.Api.GetPageTextAsync(new McpPageTextRequest(context.PageId))).Value;
        text.Warnings.Should().BeEmpty();
        context.FullHashCount.Value.Should().Be(0, "pure-text page reads must not hash the whole source file");

        McpPageBlocksResponse withoutBbox = (await context.Api.GetPageBlocksAsync(
            new McpPageBlocksRequest(context.PageId))).Value;
        withoutBbox.Warnings.Should().BeEmpty();
        context.FullHashCount.Value.Should().Be(0, "page blocks without bbox must not hash the whole source file");

        McpPageBlocksResponse withBbox = (await context.Api.GetPageBlocksAsync(
            new McpPageBlocksRequest(context.PageId, IncludeBbox: true))).Value;
        context.FullHashCount.Value.Should().Be(1, "the first coordinate-sensitive access may trigger one full hash");
        withBbox.Warnings.Should().Contain(BBoxWarning.BasisStale);

        McpPageBlocksResponse repeatedBbox = (await context.Api.GetPageBlocksAsync(
            new McpPageBlocksRequest(context.PageId, IncludeBbox: true))).Value;
        repeatedBbox.Warnings.Should().Contain(BBoxWarning.BasisStale);
        context.FullHashCount.Value.Should().Be(1, "an unchanged source file must reuse the cached full hash");
    }

    [Fact]
    public async Task Concurrent_bbox_reads_share_one_inflight_validation_and_pinned_evidence_does_not_hash()
    {
        await using Context context = await Context.CreateAsync();

        Task<Result<McpPageBlocksResponse>>[] requests = Enumerable.Range(0, 6)
            .Select(_ => context.Api.GetPageBlocksAsync(new McpPageBlocksRequest(context.PageId, IncludeBbox: true)))
            .ToArray();
        Result<McpPageBlocksResponse>[] results = await Task.WhenAll(requests);
        results.Should().OnlyContain(result => result.IsSuccess);
        context.FullHashCount.Value.Should()
            .Be(1, "concurrent coordinate-sensitive reads share one in-flight validation");

        Result<EvidenceRefRecord> record = await context.Evidence.CreateFromSearchUnitAsync(context.UnitId);
        record.IsSuccess.Should().BeTrue();

        int before = context.FullHashCount.Value;
        Result<EvidenceResolutionResult> pinned = await context.Evidence.ResolveAsync(record.Value.EvidenceRefId,
            EvidenceResolutionMode.Pinned);
        pinned.IsSuccess.Should().BeTrue();
        context.FullHashCount.Value.Should().Be(before, "pinned evidence resolution must not trigger a full hash");

        Result<EvidenceResolutionResult> compare = await context.Evidence.ResolveAsync(record.Value.EvidenceRefId,
            EvidenceResolutionMode.Compare);
        compare.IsSuccess.Should().BeTrue();
        compare.Value.Warning.Should().Contain(BBoxWarning.BasisStale,
            "compare evidence requiring source drift must run through the shared validation");
        context.FullHashCount.Value.Should().Be(before,
            "repeated current/compare evidence on an unchanged source reuses the cached validation without a new full hash");
    }

    private sealed class Context : IAsyncDisposable
    {
        private Context(TemporarySqliteDatabase database, PageId pageId, SearchUnitId unitId, McpReadApi api,
            EvidenceReferenceService evidence, Counter fullHashCount, string sourcePath)
        {
            Database = database;
            PageId = pageId;
            UnitId = unitId;
            Api = api;
            Evidence = evidence;
            FullHashCount = fullHashCount;
            SourcePath = sourcePath;
        }

        public TemporarySqliteDatabase Database { get; }
        public PageId PageId { get; }
        public SearchUnitId UnitId { get; }
        public McpReadApi Api { get; }
        public EvidenceReferenceService Evidence { get; }
        public Counter FullHashCount { get; }
        public string SourcePath { get; }

        public static async Task<Context> CreateAsync()
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-07-30T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
            LibraryMetadata library = (await libraries.CreateLibraryAsync("MCP source validation")).Value;
            ItemMetadata item = (await new ItemService(database.ConnectionFactory, libraries, clock)
                .CreateItemAsync("document", "Validated source")).Value;
            DocumentInstance document = (await new DocumentInstanceService(database.ConnectionFactory, clock)
                .AttachDocumentInstanceAsync(item.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
            Page page = (await new PageService(database.ConnectionFactory, clock)
                .CreatePageAsync(document.DocumentInstanceId, 0, "1", null, null, 0,
                    CoordinateBasis.NormalizedPage, null, null, "test", null)).Value;
            DocumentTreeRevision revision = await BoxTreeTestData.CommitTextAsync(database.ConnectionFactory, clock,
                document.DocumentInstanceId, page.PageId, "source text");

            Counter fullHashCount = new();
            string sourcePath = Path.Combine(Path.GetTempPath(), $"patchouli-mcp-source-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(sourcePath, "unchanged source payload");
            DateTimeOffset now = clock.UtcNow.ToUniversalTime();
            await LinkSourceFileAsync(database.ConnectionFactory, library.LibraryId, document.DocumentInstanceId,
                page.PageId, sourcePath, now);

            SourceFingerprintValidationService validation = new(fullHashComputer: async (path, ct) =>
            {
                fullHashCount.Increment();
                return await Infrastructure.Hashing.Blake3Hash.ComputeFileAsync(path, ct);
            });
            PageCoordinateService coordinates = new(database.ConnectionFactory, validation);
            await new SearchUnitBuilder(database.ConnectionFactory, clock, new MarkdigMarkdownEngine())
                .RebuildForDocumentInstanceAsync(document.DocumentInstanceId);
            SearchUnitId unitId = await CurrentUnitIdAsync(database.ConnectionFactory, document.DocumentInstanceId);
            EvidenceReferenceService evidence = new(database.ConnectionFactory, clock, coordinates);
            SqliteSearchService search = new(database.ConnectionFactory);
            McpReadApi api = new(database.ConnectionFactory, search, evidence, coordinates,
                markdown: new MarkdigMarkdownEngine());
            return new Context(database, page.PageId, unitId, api, evidence, fullHashCount, sourcePath);
        }

        private static async Task<SearchUnitId> CurrentUnitIdAsync(SqliteConnectionFactory factory,
            DocumentInstanceId documentId)
        {
            await using SqliteConnection connection = factory.CreateConnection();
            await connection.OpenAsync();
            string? unitId = await connection.ExecuteScalarAsync<string?>(
                """
                select unit_id from search_units
                where document_instance_id = @Id and status = 'current'
                order by ordinal
                limit 1;
                """,
                new { Id = documentId.ToString() });
            if (string.IsNullOrWhiteSpace(unitId))
            {
                throw new InvalidOperationException("Expected a current search unit after rebuild.");
            }

            return SearchUnitId.Parse(unitId);
        }

        private static async Task LinkSourceFileAsync(SqliteConnectionFactory factory, LibraryId libraryId,
            DocumentInstanceId documentId, PageId pageId, string sourcePath, DateTimeOffset now)
        {
            FileInfo info = new(sourcePath);
            string fileAssetId = FileAssetId.New().ToString();
            string created = now.ToString("O");
            await using SqliteConnection connection = factory.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                """
                insert into file_assets (file_asset_id, library_id, original_path, file_name, size_bytes,
                    mtime_utc, quick_hash, full_blake3, page_count, pdf_trailer_id, status, created_at, updated_at)
                values (@FileAssetId, @LibraryId, @OriginalPath, @FileName, @SizeBytes,
                    @MtimeUtc, @QuickHash, @FullBlake3, null, null, 'available', @CreatedAt, @UpdatedAt);
                """,
                new
                {
                    FileAssetId = fileAssetId, LibraryId = libraryId.ToString(), OriginalPath = sourcePath,
                    FileName = info.Name, SizeBytes = info.Length, MtimeUtc = info.LastWriteTimeUtc.ToString("O"),
                    QuickHash = "quick", FullBlake3 = "stored-full-hash", CreatedAt = created, UpdatedAt = created
                });
            await connection.ExecuteAsync(
                "update document_instances set file_asset_id = @FileAssetId where document_instance_id = @DocumentId;",
                new { FileAssetId = fileAssetId, DocumentId = documentId.ToString() });
            await connection.ExecuteAsync(
                "update pages set source_file_hash = @Hash where page_id = @PageId;",
                new { Hash = "stored-hash-different-from-current", PageId = pageId.ToString() });
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            if (!string.IsNullOrWhiteSpace(SourcePath))
            {
                try
                {
                    File.Delete(SourcePath);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private sealed class Counter
    {
        private int _value;

        public int Value => Volatile.Read(ref _value);

        public void Increment()
        {
            Interlocked.Increment(ref _value);
        }
    }
}
