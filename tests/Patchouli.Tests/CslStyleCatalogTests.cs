using System.Net;
using System.Net.Http;
using FluentAssertions;
using Patchouli.Core.Csl;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Tests;

public sealed class CslStyleCatalogTests
{
    [Fact]
    public async Task Default_source_refreshes_chinese_styles_from_github_repository_tree()
    {
        await using var db = TemporarySqliteDatabase.Create();
        using var httpClient = new HttpClient(new FakeHandler(new Dictionary<string, string>
        {
            ["https://api.github.com/repos/zotero-chinese/styles/git/trees/main?recursive=1"] =
                """
                {
                  "tree": [
                    { "path": "README.md", "type": "blob" },
                    { "path": "src/中国社会科学/中国社会科学.csl", "type": "blob" },
                    { "path": "src/中国社会科学/metadata.json", "type": "blob" },
                    { "path": "src/GB-T-7714—2015（顺序编码，双语）/GB-T-7714—2015（顺序编码，双语）.csl", "type": "blob" }
                  ]
                }
                """
        }));
        var catalog = new CslStyleCatalog(db.ConnectionFactory, httpClient);

        var refreshed = await catalog.RefreshAsync();
        var search = await catalog.SearchAsync("社会科学");

        catalog.CurrentSource.SourceId.Should().Be(CslCatalogSourceIds.ZoteroChineseGitHub);
        catalog.Sources.Select(source => source.SourceId).Should().Contain([
            CslCatalogSourceIds.ZoteroChineseGitHub,
            CslCatalogSourceIds.ZoteroChineseGitee,
            CslCatalogSourceIds.ZoteroOfficial
        ]);
        refreshed.IsSuccess.Should().BeTrue();
        refreshed.Value.Should().HaveCount(2);
        search.IsSuccess.Should().BeTrue();
        search.Value.Should().ContainSingle();
        search.Value.Single().StyleId.Should().Be("中国社会科学");
        search.Value.Single().DisplayName.Should().Be("中国社会科学");
        search.Value.Single().SourceKind.Should().Be(CslCatalogSourceIds.ZoteroChineseGitHub);
        search.Value.Single().SourceUrl.Should().Be("https://raw.githubusercontent.com/zotero-chinese/styles/main/src/%E4%B8%AD%E5%9B%BD%E7%A4%BE%E4%BC%9A%E7%A7%91%E5%AD%A6/%E4%B8%AD%E5%9B%BD%E7%A4%BE%E4%BC%9A%E7%A7%91%E5%AD%A6.csl");
    }

    [Fact]
    public async Task Can_switch_to_gitee_chinese_repository_source()
    {
        await using var db = TemporarySqliteDatabase.Create();
        using var httpClient = new HttpClient(new FakeHandler(new Dictionary<string, string>
        {
            ["https://gitee.com/api/v5/repos/zotero-chinese-x/styles/git/trees/main?recursive=1"] =
                """
                {
                  "tree": [
                    { "path": "src/马克思主义研究/马克思主义研究.csl", "type": "blob" }
                  ]
                }
                """
        }));
        var catalog = new CslStyleCatalog(db.ConnectionFactory, httpClient);

        var selected = catalog.SetSource(CslCatalogSourceIds.ZoteroChineseGitee);
        var refreshed = await catalog.RefreshAsync();

        selected.IsSuccess.Should().BeTrue();
        catalog.CurrentSource.DisplayName.Should().Contain("Gitee");
        refreshed.IsSuccess.Should().BeTrue();
        refreshed.Value.Should().ContainSingle();
        refreshed.Value.Single().SourceKind.Should().Be(CslCatalogSourceIds.ZoteroChineseGitee);
        refreshed.Value.Single().SourceUrl.Should().Be("https://gitee.com/zotero-chinese-x/styles/raw/main/src/%E9%A9%AC%E5%85%8B%E6%80%9D%E4%B8%BB%E4%B9%89%E7%A0%94%E7%A9%B6/%E9%A9%AC%E5%85%8B%E6%80%9D%E4%B8%BB%E4%B9%89%E7%A0%94%E7%A9%B6.csl");
    }

    [Fact]
    public async Task Can_switch_to_official_zotero_style_repository_source()
    {
        await using var db = TemporarySqliteDatabase.Create();
        using var httpClient = new HttpClient(new FakeHandler(new Dictionary<string, string>
        {
            ["https://www.zotero.org/styles-files/styles.json"] =
                """
                [
                  {
                    "title": "American Psychological Association 7th edition",
                    "name": "apa",
                    "dependent": 0,
                    "categories": { "format": "author-date", "fields": ["psychology"] },
                    "updated": "2024-01-01 00:00:00",
                    "href": "https://www.zotero.org/styles/apa"
                  },
                  {
                    "title": "GB/T 7714-2015",
                    "name": "china-national-standard-gb-t-7714-2015",
                    "dependent": 0,
                    "categories": { "format": "numeric", "fields": [] },
                    "updated": "2024-01-01 00:00:00",
                    "href": "https://www.zotero.org/styles/china-national-standard-gb-t-7714-2015"
                  }
                ]
                """
        }));
        var catalog = new CslStyleCatalog(db.ConnectionFactory, httpClient);

        catalog.SetSource(CslCatalogSourceIds.ZoteroOfficial).IsSuccess.Should().BeTrue();
        var refreshed = await catalog.RefreshAsync();
        var search = await catalog.SearchAsync("apa");

        refreshed.IsSuccess.Should().BeTrue();
        refreshed.Value.Should().HaveCount(2);
        search.IsSuccess.Should().BeTrue();
        search.Value.Should().ContainSingle();
        search.Value.Single().StyleId.Should().Be("apa");
        search.Value.Single().DisplayName.Should().Be("American Psychological Association 7th edition");
        search.Value.Single().SourceKind.Should().Be(CslCatalogSourceIds.ZoteroOfficial);
        search.Value.Single().SourceUrl.Should().Be("https://www.zotero.org/styles/apa");
    }

    [Fact]
    public async Task Search_uses_cache_for_the_selected_source()
    {
        await using var db = TemporarySqliteDatabase.Create();
        var responses = new Dictionary<string, string>
        {
            ["https://api.github.com/repos/zotero-chinese/styles/git/trees/main?recursive=1"] =
                """
                {
                  "tree": [
                    { "path": "src/中国社会科学/中国社会科学.csl", "type": "blob" }
                  ]
                }
                """,
            ["https://www.zotero.org/styles-files/styles.json"] =
                """
                [
                  { "title": "APA", "name": "apa", "href": "https://www.zotero.org/styles/apa" }
                ]
                """
        };
        using var httpClient = new HttpClient(new FakeHandler(responses));
        var catalog = new CslStyleCatalog(db.ConnectionFactory, httpClient);
        await catalog.RefreshAsync();
        catalog.SetSource(CslCatalogSourceIds.ZoteroOfficial).IsSuccess.Should().BeTrue();
        await catalog.RefreshAsync();

        catalog.SetSource(CslCatalogSourceIds.ZoteroChineseGitHub).IsSuccess.Should().BeTrue();
        var chineseSearch = await catalog.SearchAsync();
        catalog.SetSource(CslCatalogSourceIds.ZoteroOfficial).IsSuccess.Should().BeTrue();
        var officialSearch = await catalog.SearchAsync();

        chineseSearch.Value.Should().ContainSingle(style => style.StyleId == "中国社会科学");
        officialSearch.Value.Should().ContainSingle(style => style.StyleId == "apa");
    }

    [Fact]
    public async Task Unknown_source_is_rejected()
    {
        await using var db = TemporarySqliteDatabase.Create();
        var catalog = new CslStyleCatalog(db.ConnectionFactory, new HttpClient(new FakeHandler(new Dictionary<string, string>())));

        var result = catalog.SetSource("missing");

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("validation_failed");
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, string> _responses;

        public FakeHandler(IReadOnlyDictionary<string, string> responses)
        {
            _responses = responses;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.ToString() ?? "";
            if (!_responses.TryGetValue(uri, out var body))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent($"No fake response for {uri}")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }
}
