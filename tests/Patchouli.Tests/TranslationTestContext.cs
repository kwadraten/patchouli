using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Database;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Documents.Translations;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

/// <summary>
/// Shared fixture for the box-derived translation tests. It owns one migrated temporary
/// database with a single library/item/document/page and composes the real translation
/// services over it, so tests exercise the production wiring instead of hand-built fakes.
/// </summary>
internal sealed class TranslationTestContext : IAsyncDisposable
{
    private readonly TemporarySqliteDatabase _database;

    private TranslationTestContext(
        TemporarySqliteDatabase database,
        FixedClock clock,
        DocumentInstanceId documentId,
        PageId pageId,
        DocumentTreeService trees,
        MarkdigMarkdownEngine markdown,
        DocumentMarkdownCompiler markdownCompiler,
        IPageTranslationCompiler translationCompiler,
        PageTranslationCache cache,
        PageTranslationService translations)
    {
        _database = database;
        Clock = clock;
        DocumentId = documentId;
        PageId = pageId;
        Trees = trees;
        Markdown = markdown;
        MarkdownCompiler = markdownCompiler;
        TranslationCompiler = translationCompiler;
        Cache = cache;
        Translations = translations;
    }

    public FixedClock Clock { get; }
    public DocumentInstanceId DocumentId { get; }
    public PageId PageId { get; }
    public DocumentTreeService Trees { get; }
    public MarkdigMarkdownEngine Markdown { get; }
    public DocumentMarkdownCompiler MarkdownCompiler { get; }
    public IPageTranslationCompiler TranslationCompiler { get; }
    public PageTranslationCache Cache { get; }
    public PageTranslationService Translations { get; }
    public SqliteConnectionFactory ConnectionFactory => _database.ConnectionFactory;
    public string DatabasePath => _database.Path;

    public static async Task<TranslationTestContext> CreateAsync(string? label = null)
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-13T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService libraries = new(database.ConnectionFactory, clock);
        await libraries.CreateLibraryAsync(label ?? "Translation tests");
        ItemMetadata item = (await new ItemService(database.ConnectionFactory, libraries, clock)
            .CreateItemAsync("document", label ?? "Translation source")).Value;
        DocumentInstance document = (await new DocumentInstanceService(database.ConnectionFactory, clock)
            .AttachDocumentInstanceAsync(item.ItemId, null, DocumentInstanceType.PrimaryScan)).Value;
        Page page = (await new PageService(database.ConnectionFactory, clock)
            .CreatePageAsync(document.DocumentInstanceId, 0, "1", null, null, 0,
                CoordinateBasis.NormalizedPage, null, null, "test", null)).Value;

        MarkdigMarkdownEngine markdown = new();
        DocumentTreeService trees = new(database.ConnectionFactory, clock, markdown);
        DocumentMarkdownCompiler markdownCompiler = new(trees, markdown);
        PageTranslationCache cache = new();
        CachedPageTranslationCompiler translationCompiler = new(
            new PageTranslationCompiler(database.ConnectionFactory, trees, markdown), cache);
        PageTranslationService translations = new(
            database.ConnectionFactory, trees, markdownCompiler, markdown, translationCompiler, cache, clock);
        return new TranslationTestContext(
            database, clock, document.DocumentInstanceId, page.PageId, trees, markdown, markdownCompiler,
            translationCompiler, cache, translations);
    }

    /// <summary>Commits a fresh revision for the single page and returns it.</summary>
    public async Task<DocumentTreeRevision> CommitAsync(params DocumentBoxSeed[] seeds)
    {
        Result<DocumentTreeRevision> working = await Trees.BeginWorkingRevisionAsync(
            DocumentId, PageId, seeds, DocumentTreeRevisionSource.Import);
        if (working.IsFailure)
        {
            throw new InvalidOperationException(working.ErrorMessage);
        }

        Result<DocumentTreeRevision> committed = await Trees.CommitWorkingRevisionAsync(working.Value.TreeRevisionId);
        if (committed.IsFailure)
        {
            throw new InvalidOperationException(committed.ErrorMessage);
        }

        return committed.Value;
    }

    public async Task<CompiledMarkdown> CompileSourceAsync(DocumentTreeRevisionId revisionId)
    {
        Result<CompiledMarkdown> compiled = await MarkdownCompiler.CompilePageMarkdownAsync(revisionId);
        if (compiled.IsFailure)
        {
            throw new InvalidOperationException(compiled.ErrorMessage);
        }

        return compiled.Value;
    }

    public async Task<IReadOnlyList<DocumentBox>> ListBoxesAsync(DocumentTreeRevisionId revisionId)
    {
        return (await Trees.ListBoxesAsync(revisionId)).Value;
    }

    public async Task<IReadOnlyList<TranslationBoxRowSnapshot>> ListTranslationRowsAsync()
    {
        await using SqliteConnection connection = ConnectionFactory.CreateReadConnection();
        await connection.OpenAsync();
        return (await connection.QueryAsync<TranslationBoxRowSnapshot>(
            """
            select box_id as BoxId, ordinal as Ordinal, translated_md as TranslatedMd, source_hash as SourceHash
            from translation_boxes where page_id = @PageId order by ordinal;
            """,
            new { PageId = PageId.ToString() })).ToArray();
    }

    public async Task<(int Version, string SourceTreeRevisionId)?> GetHeaderAsync()
    {
        await using SqliteConnection connection = ConnectionFactory.CreateReadConnection();
        await connection.OpenAsync();
        TranslationHeaderSnapshot? row = await connection.QuerySingleOrDefaultAsync<TranslationHeaderSnapshot>(
            "select version as Version, source_tree_revision_id as SourceTreeRevisionId " +
            "from page_translations where page_id = @PageId;",
            new { PageId = PageId.ToString() });
        return row is null ? null : (row.Version, row.SourceTreeRevisionId);
    }

    public async Task<int> GetVersionAsync()
    {
        (int Version, string SourceTreeRevisionId)? header = await GetHeaderAsync();
        return header.HasValue ? header.Value.Version : 0;
    }

    public ValueTask DisposeAsync()
    {
        return _database.DisposeAsync();
    }

    internal sealed class TranslationBoxRowSnapshot
    {
        public string BoxId { get; set; } = string.Empty;
        public int Ordinal { get; set; }
        public string TranslatedMd { get; set; } = string.Empty;
        public string SourceHash { get; set; } = string.Empty;
    }

    private sealed class TranslationHeaderSnapshot
    {
        public int Version { get; set; }
        public string SourceTreeRevisionId { get; set; } = string.Empty;
    }
}
