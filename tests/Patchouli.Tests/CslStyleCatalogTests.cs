using System.Net;
using System.Net.Http;
using FluentAssertions;
using Patchouli.Infrastructure.Csl;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Tests;

public sealed class CslStyleCatalogTests
{
    [Fact]
    public async Task Refresh_and_search_parse_zotero_chinese_style_index()
    {
        await using var db = TemporarySqliteDatabase.Create();
        using var httpClient = new HttpClient(new FakeHandler(
            """
            <html>
              <body>
                <a href="china-national-standard-gb-t-7714-2015-note.csl">GB/T 7714 (note)</a>
                <a href="/styles/apa.csl">APA</a>
              </body>
            </html>
            """));
        var catalog = new CslStyleCatalog(db.ConnectionFactory, httpClient);

        var refreshed = await catalog.RefreshAsync();
        var search = await catalog.SearchAsync("apa");

        refreshed.IsSuccess.Should().BeTrue();
        refreshed.Value.Should().HaveCount(2);
        refreshed.Value.Select(style => style.StyleId).Should().Contain(["china-national-standard-gb-t-7714-2015-note", "apa"]);
        search.IsSuccess.Should().BeTrue();
        search.Value.Should().ContainSingle();
        search.Value.Single().DisplayName.Should().Be("APA");
        search.Value.Single().SourceUrl.Should().Be("https://zotero-chinese.github.io/styles/apa.csl");
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly string _body;

        public FakeHandler(string body)
        {
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body)
            });
    }
}
