using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Core.Results;
using Patchouli.Extensions;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Extensions;
using Patchouli.Infrastructure.Bibliography.MetadataLookup;
using Patchouli.Infrastructure.Translation;

namespace Patchouli.Tests;

/// <summary>
/// Behavioural parity tests for built-in metadata FSX sources: cases activate the real compiled
/// built-in package and replay provider responses through the standard HttpClient resources.
/// </summary>
/// <remarks>
/// R9 classification: the verified behaviour is script-owned — per-source request URI/method/body,
/// provider response parsing (JSON/XML/UNIMARC/Aleph HTML), and error-code mapping (rate limit, quota,
/// not-found, invalid-response). None of it may move to a C# fake. The generic half each case also
/// exercises activation, lease acquisition, config resolution and resource plumbing. The HTTP handler
/// records wire requests but does not replace or emulate the extension implementation.
/// </remarks>
[Trait("Category", "compiled-extension")]
public sealed class BuiltInMetadataSourceTests
{
    [Fact]
    public async Task Crossref_parses_representative_json_response()
    {
        const string json = """
                            {"message":{"title":["A fetched article"],"subtitle":["Evidence"],"author":[{"family":"Lovelace","given":"Ada"}],"published-print":{"date-parts":[[1843,8]]},"container-title":["Scientific Memoirs"],"publisher":"Taylor","volume":"3","issue":"1","page":"10-20","subject":["History"],"DOI":"10.1000/example","type":"journal-article"}}
                            """;
        Harness harness = await Harness.LookupAsync("metadata.crossref", "doi", "10.1000/example",
            _ => JsonResponse(json));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("A fetched article");
        harness.Result.Value.Creators.Should()
            .ContainSingle(creator => creator.Family == "Lovelace" && creator.Given == "Ada");
        harness.Result.Value.Dates.Should().ContainSingle(date => date.DateParts!.SequenceEqual(new[] { 1843, 8 }));
        harness.Result.Value.PublicationTitle.Should().Be("Scientific Memoirs");
        harness.Result.Value.SuggestedItemType.Should().Be("article-journal");
        harness.Requests.Should().ContainSingle()
            .Which.Uri.AbsoluteUri.Should().Contain("10.1000%2Fexample");
    }

    [Fact]
    public async Task Ndl_parses_dublin_core_for_ndlbibid()
    {
        const string xml = """
                           <rss xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcndl="http://ndl.go.jp/dcndl/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><channel><item><dc:title>別資料</dc:title><dc:identifier xsi:type="dcndl:NDLBibID">000000</dc:identifier></item><item><dc:title>在村蘭学の展開</dc:title><dc:creator>田崎, 哲郎, 1934-</dc:creator><dc:date>1992</dc:date><dc:publisher>思文閣出版</dc:publisher><dc:identifier xsi:type="dcndl:ISBN">4-7842-0701-5</dc:identifier><dc:identifier xsi:type="dcndl:NDLBibID">000002179229</dc:identifier><dc:identifier xsi:type="dcndl:JPNO">92042014</dc:identifier></item></channel></rss>
                           """;
        Harness harness = await Harness.LookupAsync("metadata.ndl", BuiltInIdentifierSchemes.Ndlbibid,
            "000002179229", _ => XmlResponse(xml));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("在村蘭学の展開");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.Jpno && identifier.Value == "92042014");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.ISBN && identifier.Value == "4-7842-0701-5");
        harness.Requests.Should().ContainSingle();
        harness.Requests[0].Uri.Host.Should().Be("ndlsearch.ndl.go.jp");
        harness.Requests[0].Uri.AbsolutePath.Should().Be("/api/opensearch");
        harness.Requests[0].Uri.Query.Should().Contain("any=000002179229");
    }

    [Fact]
    public async Task Ndl_isbn_lookup_uses_isbn_route_and_matches_hyphenated_response()
    {
        const string xml = """
                           <rss xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcndl="http://ndl.go.jp/dcndl/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><channel><item><dc:title>中国图书</dc:title><dc:identifier xsi:type="dcndl:ISBN13">978-7-209-10854-6</dc:identifier><dc:identifier xsi:type="dcndl:NDLBibID">123</dc:identifier></item></channel></rss>
                           """;
        Harness harness = await Harness.LookupAsync("metadata.ndl", BuiltInIdentifierSchemes.ISBN, "9787209108546",
            _ => XmlResponse(xml));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("中国图书");
        harness.Requests[0].Uri.Query.Should().Contain("isbn=9787209108546");
    }

    [Fact]
    public async Task PubMed_parses_representative_xml_response()
    {
        const string xml = """
                           <PubmedArticleSet><PubmedArticle><MedlineCitation><Article><ArticleTitle>XML article</ArticleTitle><Abstract><AbstractText>Summary.</AbstractText></Abstract><AuthorList><Author><LastName>Curie</LastName><ForeName>Marie</ForeName></Author></AuthorList><Journal><Title>Journal of Tests</Title><JournalIssue><Volume>7</Volume><Issue>2</Issue><PubDate><Year>1911</Year><Month>Dec</Month></PubDate></JournalIssue></Journal><Pagination><MedlinePgn>1-5</MedlinePgn></Pagination><Language>eng</Language></Article></MedlineCitation><PubmedData><ArticleIdList><ArticleId IdType="pubmed">123</ArticleId><ArticleId IdType="doi">10.1000/xml</ArticleId></ArticleIdList></PubmedData></PubmedArticle></PubmedArticleSet>
                           """;
        Harness harness = await Harness.LookupAsync("metadata.pubmed", BuiltInIdentifierSchemes.Pmid, "123",
            _ => XmlResponse(xml));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("XML article");
        harness.Result.Value.Creators.Should()
            .ContainSingle(creator => creator.Family == "Curie" && creator.Given == "Marie");
        harness.Result.Value.Dates.Should().ContainSingle(date => date.DateParts!.SequenceEqual(new[] { 1911, 12 }));
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.DOI && identifier.Value == "10.1000/xml");
    }

    [Fact]
    public async Task Google_books_zero_daily_quota_is_not_reported_as_retryable_rate_limit()
    {
        const string json = """
                            {"error":{"code":429,"message":"Quota exceeded for quota metric 'Queries' and limit 'Queries per day'.","details":[{"metadata":{"quota_limit_value":"0"}}]}}
                            """;
        Harness harness = await Harness.LookupAsync("metadata.google-books", BuiltInIdentifierSchemes.ISBN,
            "9787209108546", _ => JsonResponse(json, 429));

        harness.Result.IsFailure.Should().BeTrue();
        harness.Result.ErrorCode.Should().Be(MetadataLookupErrorCodes.QuotaUnavailable);
        harness.Result.ErrorMessage.Should().Contain("Google Books").And.Contain("retrying will not help");
    }

    [Fact]
    public async Task Calis_uses_isbn_search_then_maps_unimarc_detail()
    {
        const string marc = """
                            {"leader":"x","fields":[{"001":"CAL 012026046037"},{"010":{"subfields":[{"a":"978-7-5228-6527-0"}]}},{"101":{"subfields":[{"a":"chi"}]}},{"200":{"subfields":[{"a":"明代京师银库研究"},{"f":"李义琼著"}]}},{"210":{"subfields":[{"a":"北京"},{"c":"社会科学文献出版社"},{"d":"2026"}]}},{"215":{"subfields":[{"a":"405页"}]}},{"330":{"subfields":[{"a":"本书研究明代财政史。"}]}},{"606":{"subfields":[{"a":"财政史"}]}},{"690":{"subfields":[{"a":"F812.948"}]}},{"701":{"subfields":[{"a":"李义琼"},{"4":"著"}]}}]}
                            """;
        string search = """{"data":{"instances":[{"rid":"a5440f47f2da16b57a52ea525e41f84b"}]}}""";
        string detail = JsonSerializer.Serialize(new { data = new { MARC = marc } });
        Harness harness = await Harness.LookupAsync("metadata.calis", BuiltInIdentifierSchemes.ISBN, "9787522865270",
            request => request.Method == "POST" ? JsonResponse(search) : JsonResponse(detail));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("明代京师银库研究");
        harness.Result.Value.Publisher.Should().Be("社会科学文献出版社");
        harness.Result.Value.Creators.Should().ContainSingle(creator => creator.Literal == "李义琼");
        harness.Result.Value.Tags.Should().Contain(["财政史", "F812.948"]);
        harness.Requests.Should().HaveCount(2);
        harness.Requests[0].Body.Should().Contain("KGJhdGguaXNibj0iOTc4NzUyMjg2NTI3MCoiKQ==");
        harness.Requests[1].Uri.AbsoluteUri.Should().EndWith("/marc/a5440f47f2da16b57a52ea525e41f84b");
    }

    [Fact]
    public async Task Calis_skips_non_matching_marc_records()
    {
        const string marc = """
                            {"fields":[{"010":{"subfields":[{"a":"978-7-5228-6527-0"}]}},{"200":{"subfields":[{"a":"Different book"}]}}]}
                            """;
        string search = """{"data":{"instances":[{"rid":"different"}]}}""";
        string detail = JsonSerializer.Serialize(new { data = new { MARC = marc } });
        Harness harness = await Harness.LookupAsync("metadata.calis", BuiltInIdentifierSchemes.ISBN, "9787209108546",
            request => request.Method == "POST" ? JsonResponse(search) : JsonResponse(detail));

        harness.Result.IsFailure.Should().BeTrue();
        harness.Result.ErrorCode.Should().Be(MetadataLookupErrorCodes.NotFound);
    }

    [Fact]
    public async Task Calis_reports_empty_results_and_invalid_json()
    {
        Harness empty = await Harness.LookupAsync("metadata.calis", BuiltInIdentifierSchemes.ISBN, "9787209108546",
            _ => JsonResponse("""{"data":{"instances":[]}}"""));
        Harness invalid = await Harness.LookupAsync("metadata.calis", BuiltInIdentifierSchemes.ISBN, "9787209108546",
            _ => JsonResponse("not json"));

        empty.Result.ErrorCode.Should().Be(MetadataLookupErrorCodes.NotFound);
        invalid.Result.ErrorCode.Should().Be(MetadataLookupErrorCodes.InvalidResponse);
    }

    [Fact]
    public async Task Calis_and_nlc_preserve_rate_limit_errors()
    {
        Harness calis = await Harness.LookupAsync("metadata.calis", BuiltInIdentifierSchemes.ISBN, "9787209108546",
            _ => JsonResponse("", 429));
        Harness nlc = await Harness.LookupAsync("metadata.nlc", BuiltInIdentifierSchemes.ISBN, "9787209108546",
            _ => XmlResponse("", 429));

        calis.Result.ErrorCode.Should().Be(MetadataLookupErrorCodes.RateLimited);
        nlc.Result.ErrorCode.Should().Be(MetadataLookupErrorCodes.RateLimited);
    }

    [Fact]
    public async Task National_library_of_china_maps_aleph_html()
    {
        const string html = """
                            <html><table id="td">
                              <tr><td class="td1">ISBN</td><td class="td1">978-7-209-10854-6</td></tr>
                              <tr><td class="td1">题名与责任</td><td class="td1">山东文化世家研究书系 [专著]</td></tr>
                              <tr><td class="td1">著者</td><td class="td1">王明 著</td></tr>
                              <tr><td class="td1">出版项</td><td class="td1">济南 : 山东人民出版社, 2017</td></tr>
                              <tr><td class="td1">主题</td><td class="td1">齐鲁文化--家族史</td></tr>
                              <tr><td class="td1">中图分类号</td><td class="td1">K820.9</td></tr>
                              <tr><td class="td1">内容提要</td><td class="td1">中国家族文化研究。</td></tr>
                            </table></html>
                            """;
        Harness harness = await Harness.LookupAsync("metadata.nlc", BuiltInIdentifierSchemes.ISBN, "9787209108546",
            _ => HtmlResponse(html));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("山东文化世家研究书系");
        harness.Result.Value.Publisher.Should().Be("山东人民出版社");
        harness.Result.Value.Creators.Should().ContainSingle(creator => creator.Literal == "王明");
        harness.Result.Value.Tags.Should().Contain(["齐鲁文化", "家族史", "K820.9"]);
        harness.Requests[0].Uri.Query.Should().Contain("find_code=ISB").And.Contain("request=9787209108546");
    }

    [Fact]
    public async Task National_library_of_china_rejects_a_non_matching_isbn_record()
    {
        const string html = """
                            <html><table id="td">
                              <tr><td class="td1">ISBN</td><td class="td1">978-7-5228-6527-0</td></tr>
                              <tr><td class="td1">题名与责任</td><td class="td1">Different book [专著]</td></tr>
                            </table></html>
                            """;
        Harness harness = await Harness.LookupAsync("metadata.nlc", BuiltInIdentifierSchemes.ISBN, "9787209108546",
            _ => HtmlResponse(html));

        harness.Result.IsFailure.Should().BeTrue();
        harness.Result.ErrorCode.Should().Be(MetadataLookupErrorCodes.NotFound);
    }

    [Fact]
    public async Task Datacite_parses_dataset_metadata()
    {
        const string json = """
                            {"data":{"attributes":{"titles":[{"title":"Dataset title"}],"creators":[{"familyName":"Doe","givenName":"Jane"}],"publicationYear":2020,"types":{"resourceTypeGeneral":"Dataset"},"publisher":"Data Press","language":"en","subjects":[{"subject":"Geology"}],"doi":"10.1000/data"}}}
                            """;
        Harness harness = await Harness.LookupAsync("metadata.datacite", BuiltInIdentifierSchemes.DOI,
            "10.1000/data", _ => JsonResponse(json));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("Dataset title");
        harness.Result.Value.Publisher.Should().Be("Data Press");
        harness.Result.Value.Creators.Should()
            .ContainSingle(creator => creator.Family == "Doe" && creator.Given == "Jane");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.DOI && identifier.Value == "10.1000/data");
        harness.Result.Value.Tags.Should().Contain("Geology");
        harness.Result.Value.TypeConfidence.Should().Be(0.9);
    }

    [Fact]
    public async Task Arxiv_parses_atom_entry()
    {
        const string xml = """
                           <feed xmlns="http://www.w3.org/2005/Atom"><entry><id>http://arxiv.org/abs/2101.01234v2</id><published>2021-01-05T00:00:00Z</published><title>Preprint title</title><summary>Preprint abstract.</summary><author><name>Ada Lovelace</name></author><category term="cs.AI"/><doi>10.1000/preprint</doi></entry></feed>
                           """;
        Harness harness = await Harness.LookupAsync("metadata.arxiv", BuiltInIdentifierSchemes.ArXiv, "2101.01234",
            _ => XmlResponse(xml));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("Preprint title");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.ArXiv && identifier.Value == "2101.01234v2");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.DOI && identifier.Value == "10.1000/preprint");
        harness.Result.Value.Tags.Should().Contain("cs.AI");
        harness.Result.Value.SuggestedItemType.Should().Be("manuscript");
    }

    [Fact]
    public async Task Open_library_parses_bibkeys_response()
    {
        const string json = """
                            {"ISBN:9787209108546":{"title":"Book title","subtitle":"Book subtitle","authors":[{"name":"Author One"}],"publishers":[{"name":"Book Press"}],"publish_places":[{"name":"Jinan"}],"publish_date":"2017","number_of_pages":320,"subjects":[{"name":"History"}],"identifiers":{"isbn_13":["9787209108546"]},"key":"/books/OL123M"}}
                            """;
        Harness harness = await Harness.LookupAsync("metadata.open-library", BuiltInIdentifierSchemes.ISBN,
            "9787209108546", _ => JsonResponse(json));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("Book title");
        harness.Result.Value.Publisher.Should().Be("Book Press");
        harness.Result.Value.Place.Should().Be("Jinan");
        harness.Result.Value.Pages.Should().Be("320");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.OpenLibrary && identifier.Value == "OL123M");
        harness.Requests[0].Uri.Query.Should().Contain("bibkeys=ISBN:9787209108546");
    }

    [Fact]
    public async Task Library_of_congress_parses_item_json()
    {
        const string json = """
                            {"title":"LoC title","contributor":["Contributor One","Contributor Two"],"date":"1900","publisher":"LoC Press","location":"Washington","language":"eng","subject":["Subject One"]}
                            """;
        Harness harness = await Harness.LookupAsync("metadata.library-of-congress", BuiltInIdentifierSchemes.Lccn,
            "12345678", _ => JsonResponse(json));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("LoC title");
        harness.Result.Value.Place.Should().Be("Washington");
        harness.Result.Value.Creators.Should().HaveCount(2);
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.Lccn && identifier.Value == "12345678");
    }

    [Fact]
    public async Task Dnb_parses_marcxml_sru_response()
    {
        const string xml = """
                           <searchRetrieveResponse xmlns="http://www.loc.gov/zing/srw/"><records><record><recordData><record xmlns="http://www.loc.gov/MARC21/slim"><datafield tag="100"><subfield code="a">Author One</subfield></datafield><datafield tag="245"><subfield code="a">MARC title</subfield><subfield code="b">MARC subtitle</subfield></datafield><datafield tag="264"><subfield code="a">Berlin</subfield><subfield code="b">MARC Press</subfield><subfield code="c">2021</subfield></datafield><datafield tag="020"><subfield code="a">978-3-16-148410-0</subfield></datafield></record></recordData></record></records></searchRetrieveResponse>
                           """;
        Harness harness = await Harness.LookupAsync("metadata.dnb", BuiltInIdentifierSchemes.Dnb, "123456789",
            _ => XmlResponse(xml));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("MARC title");
        harness.Result.Value.Subtitle.Should().Be("MARC subtitle");
        harness.Result.Value.Publisher.Should().Be("MARC Press");
        harness.Result.Value.Place.Should().Be("Berlin");
        harness.Result.Value.Creators.Should().ContainSingle(creator => creator.Literal == "Author One");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.ISBN && identifier.Value == "978-3-16-148410-0");
    }

    private static TestHttpResponse JsonResponse(string body, int statusCode = 200)
    {
        return ProtocolTestHttp.Response(body, statusCode);
    }

    private static TestHttpResponse XmlResponse(string body, int statusCode = 200)
    {
        return ProtocolTestHttp.Response(body, statusCode);
    }

    private static TestHttpResponse HtmlResponse(string body)
    {
        return ProtocolTestHttp.Response(body);
    }

    private sealed record Harness(Result<MetadataCandidate> Result, IReadOnlyList<RecordedHttpRequest> Requests)
    {
        public static async Task<Harness> LookupAsync(string extensionId, string scheme, string value,
            Func<RecordedHttpRequest, TestHttpResponse> respond,
            IReadOnlyDictionary<string, string>? config = null)
        {
            string directory = Path.Combine(Path.GetTempPath(), $"patchouli-metadata-source-{Guid.NewGuid():N}");
            try
            {
                ConcurrentQueue<RecordedHttpRequest> requests = new();
                ProtocolTestHttpHandler handler = new((request, _) =>
                    Task.FromResult(respond(request)), requests.Enqueue);
                using TestExtensionResources resources = new(handler);
                await using IExtensionManager manager = new ExtensionManager(directory, resources,
                    new FileExtensionStore(directory), new CompiledExtensionGenerationLoader(directory));
                ExtensionPackageSnapshot package =
                    BuiltInMetadataExtensions.Packages.Single(item => item.Id == extensionId);
                await BuiltInExtensionArtifacts.InstallAsync(directory, package, CancellationToken.None);
                ExtensionCheckResult activated =
                    await manager.ActivateAsync(extensionId, package, CancellationToken.None);
                activated.Succeeded.Should().BeTrue(string.Join(Environment.NewLine,
                    activated.Diagnostics.Select(issue =>
                        $"{issue.Code} {issue.Location.FileName}:{issue.Location.Line}: {issue.Message}")));
                Dictionary<string, string> values = new(config ?? new Dictionary<string, string>(),
                    StringComparer.Ordinal);
                await manager.SaveConfigAsync(extensionId, new ExtensionConfigSnapshot(values, 1));
                await using IExtensionLease lease =
                    (await manager.AcquireAsync(extensionId, CancellationToken.None))!;
                ExtensionConfigSnapshot resolved = ExtensionConfiguration.Resolve(lease.ConfigFields, lease.Config);
                IExtensionResources runResources = resources.ForRun(resolved);
                Result<MetadataCandidate> result = await lease.Metadata!.LookupAsync(
                    new NormalizedIdentifier(scheme, value), resolved, runResources, CancellationToken.None);
                return new Harness(result, requests.ToArray());
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
        }
    }
}
