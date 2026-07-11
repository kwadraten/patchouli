using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Ids;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography;
using Patchouli.Infrastructure.Documents;
using Patchouli.Infrastructure.Layout;
using Patchouli.Infrastructure.LibraryIdentity;
using Patchouli.Infrastructure.Migrations;
using Patchouli.Infrastructure.Search;
using Patchouli.Search;

namespace Patchouli.Tests;

public sealed class SearchProfileRewriteTests
{
    [Fact]
    public async Task Migration_creates_search_profile_tables()
    {
        await using TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
        await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
        await using SqliteConnection c = db.ConnectionFactory.CreateConnection();
        await c.OpenAsync();
        (await c.ExecuteScalarAsync<int>(
                "select count(1) from sqlite_master where name in ('search_profiles','search_rewrite_rules','search_settings');"))
            .Should().Be(3);
    }

    [Fact]
    public async Task Profile_rules_and_plan_preserve_original_and_allow_disable()
    {
        await using Context ctx = await Context.CreateAsync();
        Result<SearchProfile> profile = await ctx.Service.CreateProfileAsync("Variants", null);
        (await ctx.Service.SetDefaultProfileAsync(profile.Value.ProfileId)).IsSuccess.Should().BeTrue();
        Result<SearchRewriteRule> rule = await ctx.Service.AddRewriteRuleAsync(profile.Value.ProfileId,
            SearchRuleType.Variant, "臺灣", "台湾", SearchRewriteDirection.Bidirectional, 10, null);
        Result<SearchRewritePlan> plan = await ctx.Service.BuildRewritePlanAsync("臺灣史",
            new SearchRewriteOptions(ctx.LibraryId, profile.Value.ProfileId));
        plan.Value.ExpandedQueries.Should().Contain("臺灣史").And.Contain("台湾");
        await ctx.Service.DisableRuleAsync(rule.Value.RuleId);
        (await ctx.Service.BuildRewritePlanAsync("臺灣史",
                new SearchRewriteOptions(ctx.LibraryId, profile.Value.ProfileId))).Value.ExpandedQueries.Should()
            .ContainSingle();
    }

    [Fact]
    public async Task Regex_bad_pattern_warns_and_global_rule_and_profile_priority_work()
    {
        await using Context ctx = await Context.CreateAsync();
        Result<SearchProfile> selected = await ctx.Service.CreateProfileAsync("Selected", null);
        Result<SearchProfile> explicitProfile = await ctx.Service.CreateProfileAsync("Explicit", null);
        await ctx.Service.SetLastUsedProfileAsync(selected.Value.ProfileId);
        await ctx.Service.AddRewriteRuleAsync(null, SearchRuleType.Literal, "foo", "bar", SearchRewriteDirection.Expand,
            1, null);
        await ctx.Service.AddRewriteRuleAsync(explicitProfile.Value.ProfileId, SearchRuleType.Regex, "[", "x",
            SearchRewriteDirection.Expand, 2, null);
        Result<SearchRewritePlan> plan = await ctx.Service.BuildRewritePlanAsync("foo",
            new SearchRewriteOptions(ctx.LibraryId, explicitProfile.Value.ProfileId, selected.Value.ProfileId));
        plan.Value.EffectiveProfileId.Should().Be(explicitProfile.Value.ProfileId);
        plan.Value.ExpandedQueries.Should().Contain("bar");
        plan.Value.Warnings.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Max_expansions_and_preview_only_are_respected()
    {
        await using Context ctx = await Context.CreateAsync();
        Result<SearchProfile> profile = await ctx.Service.GetDefaultProfileAsync();
        await ctx.Service.AddRewriteRuleAsync(profile.Value.ProfileId, SearchRuleType.Synonym, "a", "b",
            SearchRewriteDirection.Bidirectional, 1, null);
        await ctx.Service.AddRewriteRuleAsync(profile.Value.ProfileId, SearchRuleType.Synonym, "b", "c",
            SearchRewriteDirection.Bidirectional, 1, null);
        Result<SearchRewritePlan> plan = await ctx.Service.BuildRewritePlanAsync("a",
            new SearchRewriteOptions(ctx.LibraryId, profile.Value.ProfileId, PreviewOnly: true, MaxExpansions: 2));
        plan.Value.PreviewOnly.Should().BeTrue();
        plan.Value.ExpandedQueries.Count.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task Search_uses_rewrite_plan_without_changing_index_text()
    {
        await using SearchContext ctx = await SearchContext.CreateAsync();
        Result<SearchProfile> profile = await ctx.Profiles.CreateProfileAsync("Variants", null);
        await ctx.Profiles.AddRewriteRuleAsync(profile.Value.ProfileId, SearchRuleType.Variant, "臺灣", "台湾",
            SearchRewriteDirection.Bidirectional, 1, null);
        Result<SearchResultPage> result =
            await ctx.Search.SearchLibraryAsync(new SearchRequest("臺灣", ProfileId: profile.Value.ProfileId));
        result.Value.Results.Should().ContainSingle();
        result.Value.RewritePlan!.ExpandedQueries.Should().Contain("台湾");
        (await ctx.Connection.ExecuteScalarAsync<string>("select resolved_text from search_units limit 1;")).Should()
            .Be("台湾史");
    }

    private sealed class Context : IAsyncDisposable
    {
        private Context(TemporarySqliteDatabase db, SearchProfileService service, LibraryId libraryId)
        {
            Db = db;
            Service = service;
            LibraryId = libraryId;
        }

        public TemporarySqliteDatabase Db { get; }
        public SearchProfileService Service { get; }
        public LibraryId LibraryId { get; }

        public static async Task<Context> CreateAsync()
        {
            TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-06-20T00:00:00Z"));
            await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService library = new(db.ConnectionFactory, clock);
            Result<LibraryMetadata> created = await library.CreateLibraryAsync("Search profiles");
            return new Context(db, new SearchProfileService(db.ConnectionFactory, library, clock),
                created.Value.LibraryId);
        }

        public ValueTask DisposeAsync()
        {
            return Db.DisposeAsync();
        }
    }

    private sealed class SearchContext : IAsyncDisposable
    {
        private SearchContext(TemporarySqliteDatabase db, SqliteConnection connection,
            SearchProfileService profiles, SqliteSearchService search)
        {
            Db = db;
            Connection = connection;
            Profiles = profiles;
            Search = search;
        }

        public TemporarySqliteDatabase Db { get; }
        public SqliteConnection Connection { get; }
        public SearchProfileService Profiles { get; }
        public SqliteSearchService Search { get; }

        public static async Task<SearchContext> CreateAsync()
        {
            TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
            FixedClock clock = new(DateTimeOffset.Parse("2026-06-20T00:00:00Z"));
            await new MigrationRunner(db.ConnectionFactory, TestPaths.MigrationsDirectory).RunAsync();
            LibraryIdentityService library = new(db.ConnectionFactory, clock);
            await library.CreateLibraryAsync("Search integration");
            Result<ItemMetadata> item =
                await new ItemService(db.ConnectionFactory, library, clock).CreateItemAsync("book", "Taiwan");
            Result<DocumentInstance> doc =
                await new DocumentInstanceService(db.ConnectionFactory, clock).AttachDocumentInstanceAsync(
                    item.Value.ItemId, null, DocumentInstanceType.PrimaryScan);
            Result<Page> page = await new PageService(db.ConnectionFactory, clock).CreatePageAsync(
                doc.Value.DocumentInstanceId, 0, "1", null, null, 0, CoordinateBasis.NormalizedPage, null, null, "test",
                null);
            LayoutTreeService layout = new(db.ConnectionFactory, clock);
            Result<LayoutRevision> revision =
                await layout.CreateLayoutRevisionAsync(doc.Value.DocumentInstanceId, LayoutRevisionSource.Manual, true);
            await layout.AddNodeAsync(revision.Value.LayoutRevisionId, page.Value.PageId, null,
                LayoutNodeType.Paragraph, new NormalizedBBox(.1, .1, .5, .1), "台湾史", TextPolicy.Own, 1,
                LayoutNodeSource.Manual);
            SearchUnitBuilder builder = new(db.ConnectionFactory, clock);
            SearchIndexRebuilder index = new(db.ConnectionFactory, clock);
            await builder.RebuildForDocumentInstanceAsync(doc.Value.DocumentInstanceId);
            await index.RebuildFtsForLibraryAsync();
            SearchProfileService profiles = new(db.ConnectionFactory, library, clock);
            SqliteConnection connection = db.ConnectionFactory.CreateConnection();
            await connection.OpenAsync();
            return new SearchContext(db, connection, profiles, new SqliteSearchService(db.ConnectionFactory, profiles));
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            await Db.DisposeAsync();
        }
    }
}
