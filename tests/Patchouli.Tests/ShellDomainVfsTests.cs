using FluentAssertions;
using System.Text.Json;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
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

namespace Patchouli.Tests;

public sealed class ShellDomainVfsTests
{
    [Fact]
    public async Task Vfs_resolve_root_and_agents_and_list_root_entries()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        SystemClock clock = new();
        LibraryIdentityService library = new(database.ConnectionFactory, clock);
        (await library.CreateLibraryAsync("Shell VFS")).IsSuccess.Should().BeTrue();
        SearchProfileService profiles = new(database.ConnectionFactory, library, clock);
        SqliteSearchService search = new(database.ConnectionFactory, profiles);
        EvidenceReferenceService evidence = new(database.ConnectionFactory, clock);
        McpReadApi api = new(database.ConnectionFactory, search, evidence);
        ShellDomainService domain = new(database.ConnectionFactory, api, search, evidence, library: library);

        Result<JsonElement> root = await domain.HandleAsync("vfs.resolve",
            JsonSerializer.SerializeToElement(new { path = "/" }));
        root.IsSuccess.Should().BeTrue();
        root.Value.GetProperty("exists").GetBoolean().Should().BeTrue();
        root.Value.GetProperty("kind").GetString().Should().Be("directory");

        Result<JsonElement> agents = await domain.HandleAsync("vfs.resolve",
            JsonSerializer.SerializeToElement(new { path = "/AGENTS.md" }));
        agents.IsSuccess.Should().BeTrue();
        agents.Value.GetProperty("exists").GetBoolean().Should().BeTrue();

        Result<JsonElement> readAgents = await domain.HandleAsync("vfs.read",
            JsonSerializer.SerializeToElement(new { path = "/AGENTS.md" }));
        readAgents.IsSuccess.Should().BeTrue();
        readAgents.Value.GetProperty("content").GetString().Should()
            .Contain("Patchouli Virtual Library Shell")
            .And.Contain("[Formatted bibliography](patchouli://texts/")
            .And.Contain("link text is the formatted bibliography")
            .And.Contain("target is the complete evidence URI");

        Result<JsonElement> list = await domain.HandleAsync("vfs.list",
            JsonSerializer.SerializeToElement(new { path = "/", limit = 20 }));
        list.IsSuccess.Should().BeTrue();
        string[] names = list.Value.GetProperty("entries").EnumerateArray()
            .Select(entry => entry.GetProperty("name").GetString()!)
            .ToArray();
        names.Should().Contain(["AGENTS.md", "library.yml", "items", "texts", "csl-styles"]);
    }

    [Fact]
    public async Task Texts_and_bib_file_require_ocr_text()
    {
        await using Fixture fx = await Fixture.CreateAsync(false);

        Result<JsonElement> list = await fx.Domain.HandleAsync("vfs.list",
            JsonSerializer.SerializeToElement(new { path = "/texts", limit = 20 }));
        list.IsSuccess.Should().BeTrue(list.ErrorMessage);
        list.Value.GetProperty("entries").GetArrayLength().Should().Be(0);

        Result<JsonElement> resolveDir = await fx.Domain.HandleAsync("vfs.resolve",
            JsonSerializer.SerializeToElement(new { path = $"/texts/{fx.DocumentInstanceId}" }));
        resolveDir.IsSuccess.Should().BeTrue(resolveDir.ErrorMessage);
        resolveDir.Value.GetProperty("exists").GetBoolean().Should().BeFalse();

        Result<JsonElement> bib = await fx.Domain.HandleAsync("vfs.read",
            JsonSerializer.SerializeToElement(new { path = $"/items/{fx.ItemId}.bib" }));
        bib.IsSuccess.Should().BeTrue(bib.ErrorMessage);
        string content = bib.Value.GetProperty("content").GetString()!;
        content.Should().NotContain("file =");
        content.Should().NotContain("patchouli://texts/");
    }

    [Fact]
    public async Task Texts_and_bib_file_appear_after_ocr_text()
    {
        await using Fixture fx = await Fixture.CreateAsync(true);

        Result<JsonElement> list = await fx.Domain.HandleAsync("vfs.list",
            JsonSerializer.SerializeToElement(new { path = "/texts", limit = 20 }));
        list.IsSuccess.Should().BeTrue(list.ErrorMessage);
        list.Value.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString())
            .Should().Contain(fx.DocumentInstanceId.ToString());

        Result<JsonElement> resolveDir = await fx.Domain.HandleAsync("vfs.resolve",
            JsonSerializer.SerializeToElement(new { path = $"/texts/{fx.DocumentInstanceId}" }));
        resolveDir.IsSuccess.Should().BeTrue(resolveDir.ErrorMessage);
        resolveDir.Value.GetProperty("exists").GetBoolean().Should().BeTrue();

        Result<JsonElement> bib = await fx.Domain.HandleAsync("vfs.read",
            JsonSerializer.SerializeToElement(new { path = $"/items/{fx.ItemId}.bib" }));
        bib.IsSuccess.Should().BeTrue(bib.ErrorMessage);
        string content = bib.Value.GetProperty("content").GetString()!;
        content.Should().Contain($"file = {{patchouli://texts/{fx.DocumentInstanceId}/}}");
    }

    [Fact]
    public async Task Stat_can_skip_size_and_batch_methods_preserve_path_order_and_errors()
    {
        await using Fixture fx = await Fixture.CreateAsync(true, configureBiblatexHelper: false);
        string itemPath = $"/items/{fx.ItemId}.bib";

        Result<JsonElement> withoutSize = await fx.Domain.HandleAsync("vfs.stat",
            JsonSerializer.SerializeToElement(new { path = itemPath, include_size = false }));
        withoutSize.IsSuccess.Should().BeTrue(withoutSize.ErrorMessage);
        withoutSize.Value.TryGetProperty("size", out _).Should().BeFalse();

        Result<JsonElement> withDefaultSize = await fx.Domain.HandleAsync("vfs.stat",
            JsonSerializer.SerializeToElement(new { path = "/AGENTS.md" }));
        withDefaultSize.IsSuccess.Should().BeTrue(withDefaultSize.ErrorMessage);
        withDefaultSize.Value.GetProperty("size").GetInt64().Should().BePositive();

        string[] paths = ["/AGENTS.md", "/missing", "/library.yml"];
        Result<JsonElement> stats = await fx.Domain.HandleAsync("vfs.stat_many",
            JsonSerializer.SerializeToElement(new { paths, include_size = false }));
        stats.IsSuccess.Should().BeTrue(stats.ErrorMessage);
        JsonElement[] statResults = stats.Value.GetProperty("results").EnumerateArray().ToArray();
        statResults.Select(result => result.GetProperty("path").GetString()).Should().Equal(paths);
        statResults.Select(result => result.GetProperty("ok").GetBoolean()).Should().Equal(true, false, true);
        statResults[0].GetProperty("value").TryGetProperty("size", out _).Should().BeFalse();

        Result<JsonElement> reads = await fx.Domain.HandleAsync("vfs.read_batch",
            JsonSerializer.SerializeToElement(new { paths }));
        reads.IsSuccess.Should().BeTrue(reads.ErrorMessage);
        JsonElement[] readResults = reads.Value.GetProperty("results").EnumerateArray().ToArray();
        readResults.Select(result => result.GetProperty("path").GetString()).Should().Equal(paths);
        readResults.Select(result => result.GetProperty("ok").GetBoolean()).Should().Equal(true, false, true);
        readResults[0].GetProperty("value").GetProperty("content").GetString().Should()
            .Contain("Patchouli Virtual Library Shell");

        Result<JsonElement> tooMany = await fx.Domain.HandleAsync("vfs.read_batch",
            JsonSerializer.SerializeToElement(new { paths = Enumerable.Repeat("/AGENTS.md", 65).ToArray() }));
        tooMany.IsFailure.Should().BeTrue();
        tooMany.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Read_lines_returns_bounded_head_and_tail_with_truncation_status()
    {
        await using Fixture fx = await Fixture.CreateAsync(true);

        Result<JsonElement> head = await fx.Domain.HandleAsync("vfs.read_lines",
            JsonSerializer.SerializeToElement(new { path = "/AGENTS.md", mode = "head", count = 2 }));
        head.IsSuccess.Should().BeTrue(head.ErrorMessage);
        head.Value.GetProperty("content").GetString().Should().Be("# Patchouli Virtual Library Shell\n\n");
        head.Value.GetProperty("truncated").GetBoolean().Should().BeTrue();

        Result<JsonElement> tail = await fx.Domain.HandleAsync("vfs.read_lines",
            JsonSerializer.SerializeToElement(new { path = "/AGENTS.md", mode = "tail", count = 1 }));
        tail.IsSuccess.Should().BeTrue(tail.ErrorMessage);
        tail.Value.GetProperty("content").GetString().Should().NotBeEmpty().And.NotContain("# Patchouli");
        tail.Value.GetProperty("truncated").GetBoolean().Should().BeTrue();

        Result<JsonElement> invalidMode = await fx.Domain.HandleAsync("vfs.read_lines",
            JsonSerializer.SerializeToElement(new { path = "/AGENTS.md", mode = "middle", count = 10 }));
        invalidMode.IsFailure.Should().BeTrue();
        invalidMode.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);

        Result<JsonElement> excessiveCount = await fx.Domain.HandleAsync("vfs.read_lines",
            JsonSerializer.SerializeToElement(new { path = "/AGENTS.md", mode = "head", count = 1001 }));
        excessiveCount.IsFailure.Should().BeTrue();
        excessiveCount.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Document_page_listing_uses_ordinal_filename_cursor_semantics()
    {
        await using Fixture fx = await Fixture.CreateAsync(true, 12);
        string path = $"/texts/{fx.DocumentInstanceId}";

        Result<JsonElement> first = await fx.Domain.HandleAsync("vfs.list",
            JsonSerializer.SerializeToElement(new { path, limit = 3 }));
        first.IsSuccess.Should().BeTrue(first.ErrorMessage);
        first.Value.GetProperty("entries").EnumerateArray()
            .Select(entry => entry.GetProperty("name").GetString())
            .Should().Equal("page-0.md", "page-1.md", "page-10.md");
        string nextAfter = first.Value.GetProperty("next_after").GetString()!;
        nextAfter.Should().Be($"patchouli://texts/{fx.DocumentInstanceId}/page-10.md");
        first.Value.GetProperty("continuation_command").GetString().Should().NotBeNull();

        Result<JsonElement> second = await fx.Domain.HandleAsync("vfs.list",
            JsonSerializer.SerializeToElement(new
            {
                path,
                limit = 20,
                after = nextAfter
            }));
        second.IsSuccess.Should().BeTrue(second.ErrorMessage);
        second.Value.GetProperty("entries").EnumerateArray()
            .Select(entry => entry.GetProperty("name").GetString())
            .Should().Equal("page-11.md", "page-2.md", "page-3.md", "page-4.md", "page-5.md", "page-6.md",
                "page-7.md", "page-8.md", "page-9.md");
        second.Value.GetProperty("next_after").ValueKind.Should().Be(JsonValueKind.Null);
        second.Value.TryGetProperty("continuation_command", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Walk_returns_bounded_ordered_entries_without_reading_file_content()
    {
        await using Fixture fx = await Fixture.CreateAsync(true, 12, false);

        Result<JsonElement> files = await fx.Domain.HandleAsync("vfs.walk",
            JsonSerializer.SerializeToElement(new
            {
                path = "/texts",
                max_depth = 2,
                limit = 3,
                type = "file"
            }));
        files.IsSuccess.Should().BeTrue(files.ErrorMessage);
        files.Value.GetProperty("entries").EnumerateArray()
            .Select(entry => entry.GetProperty("name").GetString())
            .Should().Equal("page-0.md", "page-1.md", "page-10.md");
        files.Value.GetProperty("entries").EnumerateArray()
            .Select(entry => entry.GetProperty("depth").GetInt32())
            .Should().OnlyContain(depth => depth == 2);
        files.Value.GetProperty("truncated").GetBoolean().Should().BeTrue();

        Result<JsonElement> shallowFiles = await fx.Domain.HandleAsync("vfs.walk",
            JsonSerializer.SerializeToElement(new
            {
                path = "/texts",
                max_depth = 1,
                limit = 100,
                type = "file"
            }));
        shallowFiles.IsSuccess.Should().BeTrue(shallowFiles.ErrorMessage);
        shallowFiles.Value.GetProperty("entries").GetArrayLength().Should().Be(0);
        shallowFiles.Value.GetProperty("truncated").GetBoolean().Should().BeFalse();

        Result<JsonElement> document = await fx.Domain.HandleAsync("vfs.walk",
            JsonSerializer.SerializeToElement(new
            {
                path = $"/texts/{fx.DocumentInstanceId}",
                max_depth = 0,
                limit = 1
            }));
        document.IsSuccess.Should().BeTrue(document.ErrorMessage);
        JsonElement onlyEntry = document.Value.GetProperty("entries")[0];
        onlyEntry.GetProperty("path").GetString().Should().Be($"/texts/{fx.DocumentInstanceId}");
        onlyEntry.GetProperty("kind").GetString().Should().Be("directory");
        onlyEntry.GetProperty("depth").GetInt32().Should().Be(0);
        document.Value.GetProperty("truncated").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData("max_depth", -1, null)]
    [InlineData("max_depth", 21, null)]
    [InlineData("limit", 0, null)]
    [InlineData("limit", 10001, null)]
    [InlineData("type", 0, "link")]
    public async Task Walk_rejects_out_of_range_bounds_and_unsupported_types(string field, int value, string? type)
    {
        await using Fixture fx = await Fixture.CreateAsync(true);
        object request = field == "max_depth"
            ? new { path = "/texts", max_depth = value, limit = 10, type }
            : field == "limit"
                ? new { path = "/texts", max_depth = 2, limit = value, type }
                : new { path = "/texts", max_depth = 2, limit = 10, type };

        Result<JsonElement> result = await fx.Domain.HandleAsync("vfs.walk",
            JsonSerializer.SerializeToElement(request));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.ValidationFailed);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            TemporarySqliteDatabase database,
            ShellDomainService domain,
            ItemId itemId,
            DocumentInstanceId documentInstanceId)
        {
            Database = database;
            Domain = domain;
            ItemId = itemId;
            DocumentInstanceId = documentInstanceId;
        }

        public TemporarySqliteDatabase Database { get; }
        public ShellDomainService Domain { get; }
        public ItemId ItemId { get; }
        public DocumentInstanceId DocumentInstanceId { get; }

        public static async Task<Fixture> CreateAsync(bool withOcrText, int pageCount = 1,
            bool configureBiblatexHelper = true)
        {
            TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-07-28T00:00:00Z"));
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService library = new(database.ConnectionFactory, clock);
            (await library.CreateLibraryAsync("Shell OCR gate")).IsSuccess.Should().BeTrue();
            ItemService items = new(database.ConnectionFactory, library, clock);
            Result<ItemMetadata> item = await items.CreateItemAsync("book", "OCR Gate Book");
            item.IsSuccess.Should().BeTrue();
            DocumentInstanceService documents = new(database.ConnectionFactory, clock);
            Result<DocumentInstance> doc =
                await documents.AttachDocumentInstanceAsync(item.Value.ItemId, null, DocumentInstanceType.PrimaryScan);
            doc.IsSuccess.Should().BeTrue();
            PageService pages = new(database.ConnectionFactory, clock);
            List<Page> createdPages = [];
            for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
            {
                Result<Page> page = await pages.CreatePageAsync(
                    doc.Value.DocumentInstanceId, pageIndex, (pageIndex + 1).ToString(), null, null, 0,
                    CoordinateBasis.NormalizedPage, null, null, "test", null);
                page.IsSuccess.Should().BeTrue();
                createdPages.Add(page.Value);
            }

            if (withOcrText)
            {
                foreach (Page createdPage in createdPages)
                {
                    await BoxTreeTestData.CommitTextAsync(database.ConnectionFactory, clock,
                        doc.Value.DocumentInstanceId, createdPage.PageId, "ocr text present");
                }
            }

            SearchProfileService profiles = new(database.ConnectionFactory, library, clock);
            SqliteSearchService search = new(database.ConnectionFactory, profiles);
            EvidenceReferenceService evidence = new(database.ConnectionFactory, clock);
            McpReadApi api = new(database.ConnectionFactory, search, evidence);
            ShellDomainService domain = new(database.ConnectionFactory, api, search, evidence, library: library,
                items: items, biblatexHelper: configureBiblatexHelper ? new PassthroughBiblatexHelper() : null);
            return new Fixture(database, domain, item.Value.ItemId, doc.Value.DocumentInstanceId);
        }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }

    /// <summary>Serializes bib fields without invoking the external helper binary.</summary>
    private sealed class PassthroughBiblatexHelper : IBiblatexHelperClient
    {
        public Task<Result<IReadOnlyList<BiblatexEntryDto>>> ParseAsync(string biblatexText,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<BiblatexEntryDto>>.Failure(
                AppErrorCodes.UnsupportedOperation, "Not used."));
        }

        public Task<Result<string>> WriteAsync(IReadOnlyList<BiblatexWriteEntryDto> entries,
            CancellationToken cancellationToken = default)
        {
            BiblatexWriteEntryDto entry = entries[0];
            System.Text.StringBuilder sb = new();
            sb.Append('@').Append(entry.EntryType).Append('{').Append(entry.Key).AppendLine(",");
            foreach ((string key, string value) in entry.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                sb.Append("  ").Append(key).Append(" = {").Append(value).AppendLine("},");
            }

            sb.AppendLine("}");
            return Task.FromResult(Result<string>.Success(sb.ToString()));
        }
    }
}
