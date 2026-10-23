using System.Text;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Csl;
using Patchouli.Core.Results;
using Patchouli.Host.CitationInterop;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Bibliography.Biblatex;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests.CitationInterop;

/// <summary>
/// Shared fixture for the citation interop integration tests: a real migrated
/// SQLite library, the real query/item/CSL services, and the real
/// <see cref="BiblatexImportService"/> over a deterministic fake helper
/// client (the Rust helper binary is covered separately by the skip-if-absent
/// helper tests).
/// </summary>
internal sealed class InteropTestContext : IAsyncDisposable
{
    private InteropTestContext(
        TemporarySqliteDatabase database,
        ItemService items,
        LibraryItemQueryService query,
        CslStyleStore store,
        CslRenderer renderer,
        CitationInteropService interop)
    {
        Database = database;
        Items = items;
        Query = query;
        Store = store;
        Renderer = renderer;
        Interop = interop;
    }

    public TemporarySqliteDatabase Database { get; }

    public ItemService Items { get; }

    public LibraryItemQueryService Query { get; }

    public CslStyleStore Store { get; }

    public CslRenderer Renderer { get; }

    public CitationInteropService Interop { get; }

    public static async Task<InteropTestContext> CreateAsync()
    {
        TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        FixedClock clock = new(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        LibraryIdentityService library = new(database.ConnectionFactory, clock);
        await library.CreateLibraryAsync("Interop Tests");
        ItemService items = new(database.ConnectionFactory, library, clock);
        LibraryItemQueryService query = new(database.ConnectionFactory);
        CslStyleStore store = new(database.ConnectionFactory, clock);
        await store.InstallStyleAsync(
            new CslCatalogStyle("probe", "Probe", "https://example.test/probe.csl", "test"),
            ProbeStyleXml());
        await store.InstallStyleAsync(
            new CslCatalogStyle("citation-only", "Citation Only", "https://example.test/citation-only.csl", "test"),
            CitationOnlyStyleXml());
        await store.SaveSettingsAsync("probe", "en-US");
        CslItemMapper mapper = new();
        CslRenderer renderer = new(items, store, mapper);
        FakeBiblatexHelperClient helper = new();
        BiblatexImportService biblatex = new(helper, items, null!, null!);
        CitationInteropService interop = new(query, items, mapper, renderer, biblatex);
        return new InteropTestContext(database, items, query, store, renderer, interop);
    }

    public async Task<ItemMetadata> CreateItemAsync(CreateItemRequest request)
    {
        Result<ItemMetadata> created = await Items.CreateItemAsync(request);
        if (created.IsFailure)
        {
            throw new InvalidOperationException($"CreateItem failed: {created.ErrorCode} {created.ErrorMessage}");
        }

        return created.Value;
    }

    public ValueTask DisposeAsync()
    {
        return Database.DisposeAsync();
    }

    /// <summary>A style whose citation layout renders author, year and the
    /// locator label+value, so cluster modifiers are observable in output.</summary>
    public static string ProbeStyleXml()
    {
        return """
               <?xml version="1.0" encoding="utf-8"?>
               <style xmlns="http://purl.org/net/xbiblio/csl" class="in-text" version="1.0" default-locale="en-US">
                 <info>
                   <title>Probe</title>
                   <id>https://example.test/styles/probe</id>
                 </info>
                 <citation>
                   <layout prefix="(" suffix=")" delimiter="; ">
                     <group delimiter=", ">
                       <names variable="author">
                         <name and="text" delimiter=", " initialize-with=". "/>
                       </names>
                       <date variable="issued">
                         <date-part name="year"/>
                       </date>
                       <group delimiter=" ">
                         <label variable="locator" form="short"/>
                         <text variable="locator"/>
                       </group>
                     </group>
                   </layout>
                 </citation>
                 <bibliography>
                   <layout suffix=".">
                     <group delimiter=" ">
                       <names variable="author">
                         <name and="text" delimiter=", " sort-separator=", " initialize-with=". "/>
                       </names>
                       <date variable="issued" prefix=" (" suffix=")">
                         <date-part name="year"/>
                       </date>
                       <text variable="title" font-style="italic"/>
                     </group>
                   </layout>
                 </bibliography>
               </style>
               """;
    }

    /// <summary>A citation-only style: no bibliography section at all.</summary>
    public static string CitationOnlyStyleXml()
    {
        return """
               <?xml version="1.0" encoding="utf-8"?>
               <style xmlns="http://purl.org/net/xbiblio/csl" class="note" version="1.0" default-locale="en-US">
                 <info>
                   <title>Citation Only</title>
                   <id>https://example.test/styles/citation-only</id>
                 </info>
                 <citation>
                   <layout prefix="(" suffix=")">
                     <names variable="author">
                       <name initialize-with=". "/>
                     </names>
                   </layout>
                 </citation>
               </style>
               """;
    }
}

/// <summary>Deterministic in-memory stand-in for the Rust biblatex helper:
/// serializes entries in classic BibTeX syntax so export tests can assert on
/// entry types and fields without the native binary.</summary>
internal sealed class FakeBiblatexHelperClient : IBiblatexHelperClient
{
    public Task<Result<IReadOnlyList<BiblatexEntryDto>>> ParseAsync(
        string biblatexText, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Result<IReadOnlyList<BiblatexEntryDto>>.Failure(
            AppErrorCodes.BiblatexParseFailed, "The fake helper does not parse."));
    }

    public Task<Result<string>> WriteAsync(
        IReadOnlyList<BiblatexWriteEntryDto> entries, CancellationToken cancellationToken = default)
    {
        StringBuilder builder = new();
        foreach (BiblatexWriteEntryDto entry in entries)
        {
            builder.Append('@').Append(entry.EntryType).Append('{').Append(entry.Key).AppendLine(",");
            foreach (KeyValuePair<string, IReadOnlyList<BiblatexPersonDto>> persons in entry.Persons)
            {
                string names = string.Join(" and ", persons.Value.Select(FormatPerson));
                builder.Append("  ").Append(persons.Key).Append(" = {").Append(names).AppendLine("},");
            }

            foreach (KeyValuePair<string, string> field in entry.Fields)
            {
                builder.Append("  ").Append(field.Key).Append(" = {").Append(field.Value).AppendLine("},");
            }

            if (entry.Keywords.Count > 0)
            {
                builder.Append("  keywords = {").Append(string.Join(", ", entry.Keywords)).AppendLine("},");
            }

            builder.AppendLine("}");
        }

        return Task.FromResult(Result<string>.Success(builder.ToString()));
    }

    private static string FormatPerson(BiblatexPersonDto person)
    {
        if (!string.IsNullOrWhiteSpace(person.Literal))
        {
            return person.Literal!;
        }

        return string.IsNullOrWhiteSpace(person.Given) ? person.Family ?? "" : $"{person.Family}, {person.Given}";
    }
}
