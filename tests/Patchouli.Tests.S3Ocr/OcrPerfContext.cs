using Dapper;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Core.Evidence;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Evidence;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Ocr;
using Patchouli.Infrastructure.Search;
using Patchouli.Ocr;
using Patchouli.Core.Search;

namespace Patchouli.Tests.S3Ocr;

internal sealed class OcrPerfContext : IAsyncDisposable
{
    private readonly TemporarySqliteDatabase _database;

    private OcrPerfContext(
        TemporarySqliteDatabase database,
        FixedClock clock,
        DocumentInstance document,
        IReadOnlyList<Page> pages,
        DocumentTreeService trees,
        SearchUnitBuilder units,
        OcrRunEngine engine,
        IOcrPresetService presets,
        IEvidenceReferenceService evidence,
        ISearchIndexRebuilder index,
        ISearchService search)
    {
        _database = database;
        Clock = clock;
        Document = document;
        Pages = pages;
        Trees = trees;
        Units = units;
        Engine = engine;
        Presets = presets;
        Evidence = evidence;
        Index = index;
        Search = search;
    }

    public FixedClock Clock { get; }
    public DocumentInstance Document { get; }
    public IReadOnlyList<Page> Pages { get; }
    public DocumentTreeService Trees { get; }
    public SearchUnitBuilder Units { get; }
    public OcrRunEngine Engine { get; }
    public IOcrPresetService Presets { get; }
    public IEvidenceReferenceService Evidence { get; }
    public ISearchIndexRebuilder Index { get; }
    public ISearchService Search { get; }

    public async Task<int> CountAsync(string sql, object? parameters = null)
    {
        await using Microsoft.Data.Sqlite.SqliteConnection connection =
            _database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<int>(sql, parameters);
    }

    public Microsoft.Data.Sqlite.SqliteConnection OpenConnection()
    {
        return _database.ConnectionFactory.CreateConnection();
    }

    public async Task<string?> ScalarAsync(string sql, object parameters)
    {
        await using Microsoft.Data.Sqlite.SqliteConnection connection =
            _database.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<string?>(sql, parameters);
    }

    public static async Task<OcrPerfContext> CreateAsync(int pageCount = 2)
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
        await libraries.CreateLibraryAsync("S3 OCR perf");
        ItemMetadata item = (await new ItemService(database.ConnectionFactory, libraries, clock)
            .CreateItemAsync("document", "S3 OCR perf")).Value;
        DocumentInstance document = (await new DocumentInstanceService(database.ConnectionFactory, clock)
            .AttachDocumentInstanceAsync(item.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
        Infrastructure.Layout.PageService pages = new(database.ConnectionFactory, clock);
        List<Page> pageList = [];
        for (int i = 0; i < pageCount; i++)
        {
            pageList.Add((await pages.CreatePageAsync(document.DocumentInstanceId, i, (i + 1).ToString(), null, null,
                0, CoordinateBasis.NormalizedPage, null, null, "test", null)).Value);
        }

        MarkdigMarkdownEngine markdown = new();
        DocumentTreeService trees = new(database.ConnectionFactory, clock, markdown);
        SearchUnitBuilder units = new(database.ConnectionFactory, clock, markdown);
        OcrRunEngine engine = new(
            database.ConnectionFactory,
            clock,
            new MockOcrEngine(),
            units,
            new OcrDocumentTreeImporter(trees));
        return new OcrPerfContext(
            database,
            clock,
            document,
            pageList,
            trees,
            units,
            engine,
            new OcrPresetService(database.ConnectionFactory, libraries, clock),
            new EvidenceReferenceService(database.ConnectionFactory, clock),
            new SearchIndexRebuilder(database.ConnectionFactory, clock),
            new SqliteSearchService(database.ConnectionFactory));
    }

    public ValueTask DisposeAsync()
    {
        return _database.DisposeAsync();
    }
}

internal static class Boxes
{
    /// <summary>
    /// Non-overlapping, non-nested leaf text boxes that fit inside the normalized page. Counts
    /// larger than the box write batch size force the chunked bulk insert path.
    /// </summary>
    public static IReadOnlyList<DocumentBoxSeed> LeafText(int count)
    {
        DocumentBoxSeed[] seeds = new DocumentBoxSeed[count];
        for (int i = 0; i < count; i++)
        {
            int col = i % 40;
            int row = i / 40;
            double x = 0.005 + col * 0.024;
            double y = 0.005 + row * 0.032;
            seeds[i] = new DocumentBoxSeed(
                null,
                null,
                i,
                DocumentBoxType.Text,
                null,
                null,
                new NormalizedBBox(x, y, 0.02, 0.02),
                new TextBoxPayload($"line{i} unique"));
        }

        return seeds;
    }
}
