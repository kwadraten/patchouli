using System.Net;
using System.Net.Http;
using FluentAssertions;
using Patchouli.Core.Csl;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Tests;

public sealed class CslStyleCatalogTests
{
    [Fact]
    public async Task Default_source_refreshes_chinese_styles_from_github_repository_tree()
    {
        await using TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
        using HttpClient httpClient = new(new FakeHandler(new Dictionary<string, string>
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
        CslStyleCatalog catalog = new(db.ConnectionFactory, httpClient);

        Result<IReadOnlyList<CslCatalogStyle>> refreshed = await catalog.RefreshAsync();
        Result<IReadOnlyList<CslCatalogStyle>> search = await catalog.SearchAsync("社会科学");

        catalog.CurrentSource.SourceId.Should().Be(CslCatalogSourceIds.ZoteroChineseGitHub);
        catalog.Sources.Select(source => source.SourceId).Should().Contain([
            CslCatalogSourceIds.ZoteroChineseGitHub,
            CslCatalogSourceIds.ZoteroOfficial
        ]);
        refreshed.IsSuccess.Should().BeTrue();
        refreshed.Value.Should().HaveCount(2);
        search.IsSuccess.Should().BeTrue();
        search.Value.Should().ContainSingle();
        search.Value.Single().StyleId.Should().Be("中国社会科学");
        search.Value.Single().DisplayName.Should().Be("中国社会科学");
        search.Value.Single().SourceKind.Should().Be(CslCatalogSourceIds.ZoteroChineseGitHub);
        search.Value.Single().SourceUrl.Should()
            .Be(
                "https://raw.githubusercontent.com/zotero-chinese/styles/main/src/%E4%B8%AD%E5%9B%BD%E7%A4%BE%E4%BC%9A%E7%A7%91%E5%AD%A6/%E4%B8%AD%E5%9B%BD%E7%A4%BE%E4%BC%9A%E7%A7%91%E5%AD%A6.csl");
    }

    [Fact]
    public async Task Can_switch_to_official_zotero_style_repository_source()
    {
        await using TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
        using HttpClient httpClient = new(new FakeHandler(new Dictionary<string, string>
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
        CslStyleCatalog catalog = new(db.ConnectionFactory, httpClient);

        catalog.SetSource(CslCatalogSourceIds.ZoteroOfficial).IsSuccess.Should().BeTrue();
        Result<IReadOnlyList<CslCatalogStyle>> refreshed = await catalog.RefreshAsync();
        Result<IReadOnlyList<CslCatalogStyle>> search = await catalog.SearchAsync("apa");

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
        await using TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
        Dictionary<string, string> responses = new()
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
        using HttpClient httpClient = new(new FakeHandler(responses));
        CslStyleCatalog catalog = new(db.ConnectionFactory, httpClient);
        await catalog.RefreshAsync();
        catalog.SetSource(CslCatalogSourceIds.ZoteroOfficial).IsSuccess.Should().BeTrue();
        await catalog.RefreshAsync();

        catalog.SetSource(CslCatalogSourceIds.ZoteroChineseGitHub).IsSuccess.Should().BeTrue();
        Result<IReadOnlyList<CslCatalogStyle>> chineseSearch = await catalog.SearchAsync();
        catalog.SetSource(CslCatalogSourceIds.ZoteroOfficial).IsSuccess.Should().BeTrue();
        Result<IReadOnlyList<CslCatalogStyle>> officialSearch = await catalog.SearchAsync();

        chineseSearch.Value.Should().ContainSingle(style => style.StyleId == "中国社会科学");
        officialSearch.Value.Should().ContainSingle(style => style.StyleId == "apa");
    }

    [Fact]
    public async Task Unknown_source_is_rejected()
    {
        await using TemporarySqliteDatabase db = TemporarySqliteDatabase.Create();
        CslStyleCatalog catalog = new(db.ConnectionFactory,
            new HttpClient(new FakeHandler(new Dictionary<string, string>())));

        Result result = catalog.SetSource("missing");

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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string uri = request.RequestUri?.ToString() ?? "";
            if (!_responses.TryGetValue(uri, out string? body))
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
