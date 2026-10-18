using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.Biblatex;
using Patchouli.Core.Csl;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;

namespace Patchouli.Tests;

public sealed class CslLanguagePersistenceTests
{
    [Theory]
    [InlineData("English", "en")]
    [InlineData("english", "en")]
    [InlineData("eng", "en")]
    [InlineData("British", "en-GB")]
    [InlineData("EN_us", "en-US")]
    [InlineData("zh-Hant-TW", "zh-Hant-TW")]
    [InlineData("legacy-language-tag", "legacy-language-tag")]
    public async Task Item_service_normalizes_language_on_create_and_update(string input, string expected)
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        FixedClock clock = TestClock();
        LibraryIdentityService library = new(database.ConnectionFactory, clock);
        await library.CreateLibraryAsync("Item language normalization");
        ItemService items = new(database.ConnectionFactory, library, clock);

        Result<ItemMetadata> created = await items.CreateItemAsync(new CreateItemRequest(
            "book", "Language normalization", Language: input));
        created.IsSuccess.Should().BeTrue(created.ErrorMessage);
        created.Value.Language.Should().Be(expected);

        Result<ItemMetadata> updated = await items.UpdateItemAsync(created.Value.ItemId, new UpdateItemRequest(
            "book", "Language normalization updated", Language: input,
            ExpectedUpdatedAt: created.Value.UpdatedAt));
        updated.IsSuccess.Should().BeTrue(updated.ErrorMessage);
        updated.Value.Language.Should().Be(expected);
    }

    [Theory]
    [InlineData("language", "English", "en")]
    [InlineData("language", "EN_us", "en-US")]
    [InlineData("language", "zh-Hant-TW", "zh-Hant-TW")]
    [InlineData("langid", "eng", "en")]
    [InlineData("langid", "legacy-language-tag", "legacy-language-tag")]
    public void Biblatex_mapper_normalizes_language_and_langid_aliases(
        string fieldName,
        string fieldValue,
        string expected)
    {
        BiblatexEntryDto entry = CreateEntry(new Dictionary<string, string>
        {
            ["title"] = "Language field",
            [fieldName] = fieldValue
        });

        Result<BiblatexMappedItem> mapped = BiblatexFieldMapper.MapVisibleEntry(entry);

        mapped.IsSuccess.Should().BeTrue(mapped.ErrorMessage);
        mapped.Value.Language.Should().Be(expected);
    }

    [Fact]
    public void Biblatex_language_field_takes_precedence_over_langid()
    {
        BiblatexEntryDto entry = CreateEntry(new Dictionary<string, string>
        {
            ["title"] = "Language precedence",
            ["language"] = "British",
            ["langid"] = "jpn"
        });

        Result<BiblatexMappedItem> mapped = BiblatexFieldMapper.MapVisibleEntry(entry);

        mapped.IsSuccess.Should().BeTrue(mapped.ErrorMessage);
        mapped.Value.Language.Should().Be("en-GB");
    }

    [Fact]
    public async Task Migration_049_preserves_existing_csl_style_and_locale_and_adds_default_mappings()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        string stylesRoot = database.Path + ".styles";
        using TemporaryMigrationDirectory pre049Migrations = TemporaryMigrationDirectory.Create();
        try
        {
            foreach (string migration in Directory.EnumerateFiles(TestPaths.MigrationsDirectory, "*.sql",
                         SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(migration);
                if (StringComparer.Ordinal.Compare(fileName, "049_") < 0)
                {
                    File.Copy(migration, Path.Combine(pre049Migrations.Path, fileName));
                }
            }

            await new MigrationRunner(database.ConnectionFactory, pre049Migrations.Path).RunAsync();
            const string installedAt = "2026-01-02T03:04:05.0000000+00:00";
            await using (SqliteConnection connection = database.ConnectionFactory.CreateConnection())
            {
                await connection.OpenAsync();
                await connection.ExecuteAsync(
                    """
                    insert into csl_styles (
                        style_id, display_name, default_locale, source_kind, content_hash,
                        installed_at, updated_at, enabled, deleted)
                    values ('legacy-apa', 'Legacy APA', 'en-US', 'test', @Hash,
                            @InstalledAt, @InstalledAt, 1, 0);
                    insert into csl_settings (settings_id, default_style_id, locale, updated_at)
                    values ('default', 'legacy-apa', 'fr-FR', @InstalledAt);
                    """,
                    new { Hash = new string('a', 64), InstalledAt = installedAt });
            }

            IReadOnlyList<AppliedMigration> applied =
                await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            applied.Should().ContainSingle(migration => migration.Id == "049");

            FixedClock clock = TestClock();
            CslStyleStore store = new(database.ConnectionFactory, clock, stylesRoot);
            Result<CslSettings> loaded = await store.GetSettingsAsync();
            loaded.IsSuccess.Should().BeTrue(loaded.ErrorMessage);
            loaded.Value.DefaultStyleId.Should().Be("legacy-apa");
            loaded.Value.Locale.Should().Be("fr-FR");
            loaded.Value.LanguageLocaleMappings.Should().BeEquivalentTo(CslSettings.DefaultLanguageLocaleMappings);
        }
        finally
        {
            if (Directory.Exists(stylesRoot))
            {
                Directory.Delete(stylesRoot, true);
            }
        }
    }

    [Fact]
    public async Task Concurrent_legacy_style_save_and_language_mapping_save_preserve_both_settings()
    {
        await using TemporarySqliteDatabase database = TemporarySqliteDatabase.Create();
        string stylesRoot = database.Path + ".styles";
        try
        {
            await new MigrationRunner(database.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            FixedClock clock = TestClock();
            LibraryIdentityService library = new(database.ConnectionFactory, clock);
            await library.CreateLibraryAsync("CSL language settings concurrency");
            CslStyleStore store = new(database.ConnectionFactory, clock, stylesRoot);
            Result<CslStyle> installed = await store.InstallStyleAsync(
                new CslCatalogStyle("probe", "Probe", "https://example.test/probe.csl", "test"),
                StyleXml());
            installed.IsSuccess.Should().BeTrue(installed.ErrorMessage);
            Result<CslSettings> initialized = await store.SaveSettingsAsync("probe", "zh-CN");
            initialized.IsSuccess.Should().BeTrue(initialized.ErrorMessage);

            IReadOnlyDictionary<string, string> firstMappings = new Dictionary<string, string>
            {
                ["zh"] = "zh-Hant",
                ["ja"] = "en-US",
                ["ko"] = "zh-CN"
            };
            Task<Result<CslSettings>> saveStyle = store.SaveSettingsAsync("probe", "fr-FR");
            Task<Result<CslSettings>> saveMappings = store.SaveLanguageLocaleMappingsAsync(firstMappings);
            Result<CslSettings>[] concurrentResults = await Task.WhenAll(saveStyle, saveMappings);

            concurrentResults.Should().OnlyContain(result => result.IsSuccess, "both partial updates should commit");
            Result<CslSettings> afterConcurrentSave = await store.GetSettingsAsync();
            afterConcurrentSave.IsSuccess.Should().BeTrue(afterConcurrentSave.ErrorMessage);
            afterConcurrentSave.Value.DefaultStyleId.Should().Be("probe");
            afterConcurrentSave.Value.Locale.Should().Be("fr-FR");
            afterConcurrentSave.Value.LanguageLocaleMappings.Should().BeEquivalentTo(firstMappings);

            IReadOnlyDictionary<string, string> secondMappings = new Dictionary<string, string>
            {
                ["zh"] = "en-US",
                ["ja"] = "zh-CN",
                ["ko"] = "en-US"
            };
            Result<CslSettings> mappingOnly = await store.SaveLanguageLocaleMappingsAsync(secondMappings);
            mappingOnly.IsSuccess.Should().BeTrue(mappingOnly.ErrorMessage);
            Result<CslSettings> legacyStyleOnly = await store.SaveSettingsAsync("probe", "en-GB");

            legacyStyleOnly.IsSuccess.Should().BeTrue(legacyStyleOnly.ErrorMessage);
            legacyStyleOnly.Value.DefaultStyleId.Should().Be("probe");
            legacyStyleOnly.Value.Locale.Should().Be("en-GB");
            legacyStyleOnly.Value.LanguageLocaleMappings.Should().BeEquivalentTo(secondMappings);
        }
        finally
        {
            if (Directory.Exists(stylesRoot))
            {
                Directory.Delete(stylesRoot, true);
            }
        }
    }

    private static BiblatexEntryDto CreateEntry(IReadOnlyDictionary<string, string> fields)
    {
        return new BiblatexEntryDto(
            "language-entry",
            "book",
            false,
            fields,
            new Dictionary<string, IReadOnlyList<BiblatexPersonDto>>(),
            new Dictionary<string, BiblatexDateDto>(),
            [],
            null,
            true,
            new BiblatexVerifyDto([], [], []));
    }

    private static FixedClock TestClock()
    {
        return new FixedClock(DateTimeOffset.Parse("2026-07-08T00:00:00Z"));
    }

    private static string StyleXml()
    {
        return """
               <?xml version="1.0" encoding="utf-8"?>
               <style xmlns="http://purl.org/net/xbiblio/csl" class="in-text" version="1.0" default-locale="en-US">
                 <info>
                   <title>Probe</title>
                   <id>https://example.test/styles/probe</id>
                 </info>
                 <citation><layout><text variable="title"/></layout></citation>
                 <bibliography><layout><text variable="title"/></layout></bibliography>
               </style>
               """;
    }
}
