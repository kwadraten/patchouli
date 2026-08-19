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
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Coordinates;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
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
            new McpPageBlocksRequest(context.PageId, true))).Value;
        context.FullHashCount.Value.Should().Be(1, "the first coordinate-sensitive access may trigger one full hash");
        withBbox.Warnings.Should().Contain(BBoxWarning.BasisStale);

        McpPageBlocksResponse repeatedBbox = (await context.Api.GetPageBlocksAsync(
            new McpPageBlocksRequest(context.PageId, true))).Value;
        repeatedBbox.Warnings.Should().Contain(BBoxWarning.BasisStale);
        context.FullHashCount.Value.Should().Be(1, "an unchanged source file must reuse the cached full hash");
    }

    [Fact]
    public async Task Concurrent_bbox_reads_share_one_inflight_validation_and_versioned_evidence_does_not_hash()
    {
        await using Context context = await Context.CreateAsync();

        Task<Result<McpPageBlocksResponse>>[] requests = Enumerable.Range(0, 6)
            .Select(_ => context.Api.GetPageBlocksAsync(new McpPageBlocksRequest(context.PageId, true)))
            .ToArray();
        Result<McpPageBlocksResponse>[] results = await Task.WhenAll(requests);
        results.Should().OnlyContain(result => result.IsSuccess);
        context.FullHashCount.Value.Should()
            .Be(1, "concurrent coordinate-sensitive reads share one in-flight validation");

        McpPageBlock matched = (await context.Api.GetPageBlocksAsync(
            new McpPageBlocksRequest(context.PageId))).Value.Blocks.Single();

        int before = context.FullHashCount.Value;
        Result<EvidencePageText> versioned = await context.EvidenceReader.GetBoxTextAsync(
            context.DocumentId, 1, matched.TreeRevisionId, matched.BoxId);
        versioned.IsSuccess.Should().BeTrue();
        context.FullHashCount.Value.Should().Be(before,
            "versioned evidence resolution must not trigger a full hash");

        Result<EvidencePageText> head = await context.EvidenceReader.GetBoxTextAsync(
            context.DocumentId, 1, boxId: matched.BoxId);
        head.IsSuccess.Should().BeTrue();
        context.FullHashCount.Value.Should().Be(before,
            "HEAD evidence resolution on an unchanged source reuses the cached validation without a new full hash");
    }

    private sealed class Context : IAsyncDisposable
    {
        private Context(TemporarySqliteDatabase database, PageId pageId, DocumentInstanceId documentId,
            McpReadApi api, IVersionedEvidenceReader evidenceReader, Counter fullHashCount, string sourcePath)
        {
            Database = database;
            PageId = pageId;
            DocumentId = documentId;
            Api = api;
            EvidenceReader = evidenceReader;
            FullHashCount = fullHashCount;
            SourcePath = sourcePath;
        }

        public TemporarySqliteDatabase Database { get; }
        public PageId PageId { get; }
        public DocumentInstanceId DocumentId { get; }
        public McpReadApi Api { get; }
        public IVersionedEvidenceReader EvidenceReader { get; }
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

            DocumentTreeService trees = BoxTreeTestData.CreateService(database.ConnectionFactory, clock);
            Result<DocumentTreeRevision> working = await trees.BeginWorkingRevisionAsync(
                document.DocumentInstanceId, page.PageId,
                [
                    new DocumentBoxSeed(null, null, 0, DocumentBoxType.Text, null, null,
                        new NormalizedBBox(.1, .1, .8, .1), new TextBoxPayload("source text"))
                ],
                DocumentTreeRevisionSource.Import);
            if (working.IsFailure)
            {
                throw new InvalidOperationException(working.ErrorMessage);
            }

            Result<DocumentTreeRevision> committed =
                await trees.CommitWorkingRevisionAsync(working.Value.TreeRevisionId);
            if (committed.IsFailure)
            {
                throw new InvalidOperationException(committed.ErrorMessage);
            }

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
            IMarkdownEngine markdown = new MarkdigMarkdownEngine();
            IDocumentMarkdownCompiler markdownCompiler = new DocumentMarkdownCompiler(trees, markdown);
            IVersionedEvidenceReader evidenceReader = new VersionedEvidenceReader(
                database.ConnectionFactory, libraries, trees, markdownCompiler);
            await new SearchUnitBuilder(database.ConnectionFactory, clock, markdown)
                .RebuildForDocumentInstanceAsync(document.DocumentInstanceId);
            SqliteSearchService search = new(database.ConnectionFactory);
            McpReadApi api = new(database.ConnectionFactory, search, coordinates, markdown: markdown);
            return new Context(database, page.PageId, document.DocumentInstanceId, api, evidenceReader, fullHashCount,
                sourcePath);
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
