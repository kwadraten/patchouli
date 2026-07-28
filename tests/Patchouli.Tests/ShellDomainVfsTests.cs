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

        public static async Task<Fixture> CreateAsync(bool withOcrText)
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
            Result<Page> page = await pages.CreatePageAsync(
                doc.Value.DocumentInstanceId, 0, "1", null, null, 0, CoordinateBasis.NormalizedPage, null, null, "test",
                null);
            page.IsSuccess.Should().BeTrue();
            if (withOcrText)
            {
                await BoxTreeTestData.CommitTextAsync(database.ConnectionFactory, clock, doc.Value.DocumentInstanceId,
                    page.Value.PageId, "ocr text present");
            }

            SearchProfileService profiles = new(database.ConnectionFactory, library, clock);
            SqliteSearchService search = new(database.ConnectionFactory, profiles);
            EvidenceReferenceService evidence = new(database.ConnectionFactory, clock);
            McpReadApi api = new(database.ConnectionFactory, search, evidence);
            ShellDomainService domain = new(database.ConnectionFactory, api, search, evidence, library: library,
                items: items, biblatexHelper: new PassthroughBiblatexHelper());
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
