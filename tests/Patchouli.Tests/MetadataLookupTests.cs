using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Bibliography.MetadataLookup;

namespace Patchouli.Tests;

public sealed class MetadataLookupTests
{
    [Theory]
    [InlineData("doi", "https://doi.org/10.1000/ABC.", BuiltInIdentifierSchemes.DOI, "10.1000/abc")]
    [InlineData("isbn", "978-0-306-40615-7", BuiltInIdentifierSchemes.ISBN, "9780306406157")]
    [InlineData("pmcid", "PMC000123", BuiltInIdentifierSchemes.Pmcid, "PMC123")]
    [InlineData("arxiv", "arXiv:2101.01234v3", BuiltInIdentifierSchemes.ArXiv, "2101.01234")]
    [InlineData("NDLBibID", "123456", BuiltInIdentifierSchemes.Ndlbibid, "123456")]
    public void Identifier_normalization_returns_canonical_scheme_and_value(string scheme, string value,
        string expectedScheme, string expectedValue)
    {
        Result<NormalizedIdentifier> result = IdentifierNormalizer.Normalize(scheme, value);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.Should().Be(new NormalizedIdentifier(expectedScheme, expectedValue));
    }

    [Fact]
    public async Task Lookup_uses_configured_priority_and_falls_back_after_not_found()
    {
        FakeSource first = new("first",
            Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.NotFound, "missing"));
        FakeSource second = new("second", Result<MetadataCandidate>.Success(new MetadataCandidate("second", "Found")));
        FakeItemService items = new(CreateItem("Old"));
        MetadataLookupService service = new(items, new MetadataSourceRegistry([first, second]));

        Result<MetadataLookupOutcome> result = await service.LookupAndMergeAsync(
            items.Item.ItemId,
            BuiltInIdentifierSchemes.DOI,
            "10.1000/example",
            [new MetadataSourcePreference("second", true, 20), new MetadataSourcePreference("first", true, 10)]);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.Attempts.Select(attempt => attempt.SourceId).Should().ContainInOrder("first", "second");
        first.CallCount.Should().Be(1);
        second.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Lookup_reloads_and_merges_non_empty_fields_roles_tags_and_identifiers()
    {
        ItemMetadata existing = CreateItem(
            "User's latest title",
            "Keep subtitle",
            [
                Creator(ItemCreatorRoles.Author, "Old Author"),
                Creator(ItemCreatorRoles.Editor, literal: "Keep Editor")
            ],
            [
                Date(ItemDateRoles.Issued, "[[1999]]"),
                Date(ItemDateRoles.Accessed, "[[2026,7,10]]")
            ],
            "[\"History\"]",
            "[\"Do not replace\"]",
            "{\"user\":true}",
            [Identifier(BuiltInIdentifierSchemes.DOI, "10.1000/example")]);
        FakeItemService items = new(existing);
        FakeSource source = new("catalog", Result<MetadataCandidate>.Success(new MetadataCandidate(
            "catalog",
            "Source title",
            " ",
            Creators: [new MetadataCreator(ItemCreatorRoles.Author, "New Author")],
            Dates: [new MetadataDate(ItemDateRoles.Issued, [2025, 6])],
            Publisher: "Source Press",
            Tags: ["history", "Research"],
            Identifiers:
            [
                new MetadataIdentifier(BuiltInIdentifierSchemes.DOI, "https://doi.org/10.1000/EXAMPLE"),
                new MetadataIdentifier(BuiltInIdentifierSchemes.Pmid, "12345")
            ])));
        MetadataLookupService service = new(items, new MetadataSourceRegistry([source]));

        Result<MetadataLookupOutcome> result =
            await service.LookupAndMergeAsync(existing.ItemId, "doi", "10.1000/example");

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        items.GetCount.Should().BeGreaterThanOrEqualTo(2);
        items.LastUpdate.Should().NotBeNull();
        items.LastUpdate!.Title.Should().Be("Source title");
        items.LastUpdate.ItemType.Should().Be(existing.ItemType);
        items.LastUpdate.Subtitle.Should().Be("Keep subtitle");
        items.LastUpdate.Publisher.Should().Be("Source Press");
        items.LastUpdate.CollectionsJson.Should().Be(existing.CollectionsJson);
        items.LastUpdate.CustomFieldsJson.Should().Be(existing.CustomFieldsJson);
        items.LastUpdate.Creators.Should().ContainSingle(creator =>
            creator.Role == ItemCreatorRoles.Author && creator.Family == "New Author");
        items.LastUpdate.Creators.Should().ContainSingle(creator =>
            creator.Role == ItemCreatorRoles.Editor && creator.Literal == "Keep Editor");
        items.LastUpdate.Dates.Should()
            .ContainSingle(date => date.Role == ItemDateRoles.Issued && date.DatePartsJson == "[[2025,6]]");
        items.LastUpdate.Dates.Should().ContainSingle(date => date.Role == ItemDateRoles.Accessed);
        items.LastUpdate.TagsJson.Should().Contain("History").And.Contain("Research");
        items.AddedIdentifiers.Should().ContainSingle(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.Pmid && identifier.Value == "12345");
    }

    [Fact]
    public void High_confidence_source_type_overwrites_item_type()
    {
        ItemMetadata item = CreateItem("Old");
        UpdateItemRequest update = MetadataLookupService.CreateUpdate(item, new MetadataCandidate(
            "source",
            "New",
            SuggestedItemType: "article-journal",
            TypeConfidence: 0.95));

        update.ItemType.Should().Be("article-journal");
    }

    [Fact]
    public void Malformed_existing_tags_are_preserved_instead_of_replaced()
    {
        ItemMetadata item = CreateItem("Old", tagsJson: "legacy-not-json");

        UpdateItemRequest update =
            MetadataLookupService.CreateUpdate(item, new MetadataCandidate("source", "New", Tags: ["remote"]));

        update.TagsJson.Should().Be("legacy-not-json");
    }

    [Theory]
    [InlineData("pmc")]
    [InlineData("PMCID")]
    public void CanLookup_accepts_supported_scheme_aliases(string scheme)
    {
        SchemeSource source = new(BuiltInIdentifierSchemes.Pmcid);
        MetadataLookupService service = new(new FakeItemService(CreateItem("Old")),
            new MetadataSourceRegistry([source]));

        service.CanLookup(scheme).Should().BeTrue();
    }

    [Fact]
    public async Task Ndl_parses_dublin_core_for_ndlbibid()
    {
        const string xml = """
                           <rss xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcndl="http://ndl.go.jp/dcndl/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><channel><item><dc:title>別資料</dc:title><dc:identifier xsi:type="dcndl:NDLBibID">000000</dc:identifier></item><item><dc:title>在村蘭学の展開</dc:title><dc:creator>田崎, 哲郎, 1934-</dc:creator><dc:date>1992</dc:date><dc:publisher>思文閣出版</dc:publisher><dc:identifier xsi:type="dcndl:ISBN">4-7842-0701-5</dc:identifier><dc:identifier xsi:type="dcndl:NDLBibID">000002179229</dc:identifier><dc:identifier xsi:type="dcndl:JPNO">92042014</dc:identifier></item></channel></rss>
                           """;
        FakeHttpHandler handler = new(xml, "application/xml");
        NdlMetadataSource source = new(new HttpClient(handler));

        Result<MetadataCandidate> result =
            await source.LookupAsync(new NormalizedIdentifier(BuiltInIdentifierSchemes.Ndlbibid, "000002179229"));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.Title.Should().Be("在村蘭学の展開");
        result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.Jpno && identifier.Value == "92042014");
        result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.ISBN && identifier.Value == "4-7842-0701-5");
        handler.LastRequest!.RequestUri!.Host.Should().Be("ndlsearch.ndl.go.jp");
        handler.LastRequest.RequestUri.AbsolutePath.Should().Be("/api/opensearch");
        handler.LastRequest.RequestUri.Query.Should().Contain("any=000002179229");
    }

    [Fact]
    public async Task Crossref_parses_representative_json_response()
    {
        const string json = """
                            {"message":{"title":["A fetched article"],"subtitle":["Evidence"],"author":[{"family":"Lovelace","given":"Ada"}],"published-print":{"date-parts":[[1843,8]]},"container-title":["Scientific Memoirs"],"publisher":"Taylor","volume":"3","issue":"1","page":"10-20","subject":["History"],"DOI":"10.1000/example","type":"journal-article"}}
                            """;
        FakeHttpHandler handler = new(json, "application/json");
        CrossrefMetadataSource source = new(new HttpClient(handler));

        Result<MetadataCandidate> result =
            await source.LookupAsync(new NormalizedIdentifier(BuiltInIdentifierSchemes.DOI, "10.1000/example"));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.Title.Should().Be("A fetched article");
        result.Value.Creators.Should().ContainSingle(creator => creator.Family == "Lovelace" && creator.Given == "Ada");
        result.Value.Dates.Should().ContainSingle(date => date.DateParts!.SequenceEqual(new[] { 1843, 8 }));
        result.Value.PublicationTitle.Should().Be("Scientific Memoirs");
        result.Value.SuggestedItemType.Should().Be("article-journal");
        handler.LastRequest!.RequestUri!.AbsoluteUri.Should().Contain("10.1000%2Fexample");
    }

    [Fact]
    public async Task PubMed_parses_representative_xml_response()
    {
        const string xml = """
                           <PubmedArticleSet><PubmedArticle><MedlineCitation><Article><ArticleTitle>XML article</ArticleTitle><Abstract><AbstractText>Summary.</AbstractText></Abstract><AuthorList><Author><LastName>Curie</LastName><ForeName>Marie</ForeName></Author></AuthorList><Journal><Title>Journal of Tests</Title><JournalIssue><Volume>7</Volume><Issue>2</Issue><PubDate><Year>1911</Year><Month>Dec</Month></PubDate></JournalIssue></Journal><Pagination><MedlinePgn>1-5</MedlinePgn></Pagination><Language>eng</Language></Article></MedlineCitation><PubmedData><ArticleIdList><ArticleId IdType="pubmed">123</ArticleId><ArticleId IdType="doi">10.1000/xml</ArticleId></ArticleIdList></PubmedData></PubmedArticle></PubmedArticleSet>
                           """;
        PubMedMetadataSource source = new(new HttpClient(new FakeHttpHandler(xml, "application/xml")));

        Result<MetadataCandidate> result =
            await source.LookupAsync(new NormalizedIdentifier(BuiltInIdentifierSchemes.Pmid, "123"));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.Title.Should().Be("XML article");
        result.Value.Creators.Should().ContainSingle(creator => creator.Family == "Curie" && creator.Given == "Marie");
        result.Value.Dates.Should().ContainSingle(date => date.DateParts!.SequenceEqual(new[] { 1911, 12 }));
        result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.DOI && identifier.Value == "10.1000/xml");
    }

    [Fact]
    public async Task Google_books_zero_daily_quota_is_not_reported_as_retryable_rate_limit()
    {
        const string json = """
                            {"error":{"code":429,"message":"Quota exceeded for quota metric 'Queries' and limit 'Queries per day'.","details":[{"metadata":{"quota_limit_value":"0"}}]}}
                            """;
        GoogleBooksMetadataSource source =
            new(new HttpClient(new FakeHttpHandler(json, "application/json", HttpStatusCode.TooManyRequests)));

        Result<MetadataCandidate> result =
            await source.LookupAsync(new NormalizedIdentifier(BuiltInIdentifierSchemes.ISBN, "9787209108546"));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(MetadataLookupErrorCodes.QuotaUnavailable);
        result.ErrorMessage.Should().Contain("Google Books").And.Contain("retrying will not help");
    }

    [Fact]
    public async Task Ndl_isbn_lookup_uses_isbn_route_and_matches_hyphenated_response()
    {
        const string xml = """
                           <rss xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcndl="http://ndl.go.jp/dcndl/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><channel><item><dc:title>中国图书</dc:title><dc:identifier xsi:type="dcndl:ISBN13">978-7-209-10854-6</dc:identifier><dc:identifier xsi:type="dcndl:NDLBibID">123</dc:identifier></item></channel></rss>
                           """;
        FakeHttpHandler handler = new(xml, "application/xml");
        NdlMetadataSource source = new(new HttpClient(handler));

        Result<MetadataCandidate> result =
            await source.LookupAsync(new NormalizedIdentifier(BuiltInIdentifierSchemes.ISBN, "9787209108546"));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.Title.Should().Be("中国图书");
        handler.LastRequest!.RequestUri!.Query.Should().Contain("isbn=9787209108546");
    }

    [Fact]
    public async Task Calis_uses_isbn_search_then_maps_unimarc_detail()
    {
        const string marc = """
                            {"leader":"x","fields":[{"001":"CAL 012026046037"},{"010":{"subfields":[{"a":"978-7-5228-6527-0"}]}},{"101":{"subfields":[{"a":"chi"}]}},{"200":{"subfields":[{"a":"明代京师银库研究"},{"f":"李义琼著"}]}},{"210":{"subfields":[{"a":"北京"},{"c":"社会科学文献出版社"},{"d":"2026"}]}},{"215":{"subfields":[{"a":"405页"}]}},{"330":{"subfields":[{"a":"本书研究明代财政史。"}]}},{"606":{"subfields":[{"a":"财政史"}]}},{"690":{"subfields":[{"a":"F812.948"}]}},{"701":{"subfields":[{"a":"李义琼"},{"4":"著"}]}}]}
                            """;
        string search = """{"data":{"instances":[{"rid":"a5440f47f2da16b57a52ea525e41f84b"}]}}""";
        string detail = JsonSerializer.Serialize(new { data = new { MARC = marc } });
        RoutingHttpHandler handler = new(request => request.Method == HttpMethod.Post
            ? JsonResponse(search)
            : JsonResponse(detail));
        CalisMetadataSource source = new(new HttpClient(handler));

        Result<MetadataCandidate> result =
            await source.LookupAsync(new NormalizedIdentifier(BuiltInIdentifierSchemes.ISBN, "9787522865270"));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.Title.Should().Be("明代京师银库研究");
        result.Value.Publisher.Should().Be("社会科学文献出版社");
        result.Value.Creators.Should().ContainSingle(creator => creator.Literal == "李义琼");
        result.Value.Tags.Should().Contain(["财政史", "F812.948"]);
        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Body.Should().Contain("KGJhdGguaXNibj0iOTc4NzUyMjg2NTI3MCoiKQ==");
        handler.Requests[1].Uri.Should().EndWith("/marc/a5440f47f2da16b57a52ea525e41f84b");
    }

    [Fact]
    public async Task Calis_skips_non_matching_marc_records()
    {
        const string marc = """
                            {"fields":[{"010":{"subfields":[{"a":"978-7-5228-6527-0"}]}},{"200":{"subfields":[{"a":"Different book"}]}}]}
                            """;
        string search = """{"data":{"instances":[{"rid":"different"}]}}""";
        string detail = JsonSerializer.Serialize(new { data = new { MARC = marc } });
        RoutingHttpHandler handler = new(request => request.Method == HttpMethod.Post
            ? JsonResponse(search)
            : JsonResponse(detail));
        CalisMetadataSource source = new(new HttpClient(handler));

        Result<MetadataCandidate> result =
            await source.LookupAsync(new NormalizedIdentifier(BuiltInIdentifierSchemes.ISBN, "9787209108546"));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(MetadataLookupErrorCodes.NotFound);
    }

    [Fact]
    public async Task Calis_reports_empty_results_and_invalid_json()
    {
        CalisMetadataSource empty =
            new(new HttpClient(new FakeHttpHandler("""{"data":{"instances":[]}}""", "application/json")));
        CalisMetadataSource invalid = new(new HttpClient(new FakeHttpHandler("not json", "application/json")));

        Result<MetadataCandidate> emptyResult =
            await empty.LookupAsync(new NormalizedIdentifier(BuiltInIdentifierSchemes.ISBN, "9787209108546"));
        Result<MetadataCandidate> invalidResult =
            await invalid.LookupAsync(new NormalizedIdentifier(BuiltInIdentifierSchemes.ISBN, "9787209108546"));

        emptyResult.ErrorCode.Should().Be(MetadataLookupErrorCodes.NotFound);
        invalidResult.ErrorCode.Should().Be(MetadataLookupErrorCodes.InvalidResponse);
    }

    [Fact]
    public async Task Calis_and_nlc_preserve_rate_limit_errors()
    {
        CalisMetadataSource calis =
            new(new HttpClient(new FakeHttpHandler("", "application/json", HttpStatusCode.TooManyRequests)));
        NationalLibraryOfChinaMetadataSource nlc =
            new(new HttpClient(new FakeHttpHandler("", "text/html", HttpStatusCode.TooManyRequests)));

        Result<MetadataCandidate> calisResult =
            await calis.LookupAsync(new NormalizedIdentifier(BuiltInIdentifierSchemes.ISBN, "9787209108546"));
        Result<MetadataCandidate> nlcResult =
            await nlc.LookupAsync(new NormalizedIdentifier(BuiltInIdentifierSchemes.ISBN, "9787209108546"));

        calisResult.ErrorCode.Should().Be(MetadataLookupErrorCodes.RateLimited);
        nlcResult.ErrorCode.Should().Be(MetadataLookupErrorCodes.RateLimited);
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
        FakeHttpHandler handler = new(html, "text/html");
        NationalLibraryOfChinaMetadataSource source = new(new HttpClient(handler));

        Result<MetadataCandidate> result =
            await source.LookupAsync(new NormalizedIdentifier(BuiltInIdentifierSchemes.ISBN, "9787209108546"));

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.Title.Should().Be("山东文化世家研究书系");
        result.Value.Publisher.Should().Be("山东人民出版社");
        result.Value.Creators.Should().ContainSingle(creator => creator.Literal == "王明");
        result.Value.Tags.Should().Contain(["齐鲁文化", "家族史", "K820.9"]);
        handler.LastRequest!.RequestUri!.Query.Should().Contain("find_code=ISB").And.Contain("request=9787209108546");
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
        NationalLibraryOfChinaMetadataSource source = new(new HttpClient(new FakeHttpHandler(html, "text/html")));

        Result<MetadataCandidate> result =
            await source.LookupAsync(new NormalizedIdentifier(BuiltInIdentifierSchemes.ISBN, "9787209108546"));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(MetadataLookupErrorCodes.NotFound);
    }

    private static ItemMetadata CreateItem(
        string title,
        string? subtitle = null,
        IReadOnlyList<ItemCreator>? creators = null,
        IReadOnlyList<ItemDate>? dates = null,
        string tagsJson = "[]",
        string collectionsJson = "[]",
        string customFieldsJson = "{}",
        IReadOnlyList<ItemIdentifier>? identifiers = null)
    {
        return new ItemMetadata(ItemId.New(), LibraryId.New(), "book", "preserved-key", title, subtitle, null, "[]",
            creators ?? [],
            null, dates ?? [], identifiers ?? [], null, null, null, null, null, null, null, null, null, null, null,
            null, null, null, null, null, null, tagsJson, collectionsJson, customFieldsJson, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
    }

    private static ItemCreator Creator(string role, string? family = null, string? literal = null)
    {
        return new ItemCreator(Guid.NewGuid().ToString(), default, role, family, null, literal, null, null, 0,
            DateTimeOffset.UnixEpoch);
    }

    private static ItemDate Date(string role, string parts)
    {
        return new ItemDate(Guid.NewGuid().ToString(), default, role, parts, false, null, null,
            DateTimeOffset.UnixEpoch);
    }

    private static ItemIdentifier Identifier(string scheme, string value)
    {
        return new ItemIdentifier(IdentifierId.New(), default, scheme, value, null, DateTimeOffset.UnixEpoch);
    }

    private sealed class FakeSource(string id, Result<MetadataCandidate> result) : IMetadataSource
    {
        public MetadataSourceDefinition Definition { get; } = new(id, id,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { BuiltInIdentifierSchemes.DOI }, 10);

        public int CallCount { get; private set; }

        public Task<Result<MetadataCandidate>> LookupAsync(NormalizedIdentifier identifier,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class SchemeSource(string scheme) : IMetadataSource
    {
        public MetadataSourceDefinition Definition { get; } = new("scheme", "scheme",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { scheme }, 1);

        public Task<Result<MetadataCandidate>> LookupAsync(NormalizedIdentifier identifier,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.NotFound, "missing"));
        }
    }

    private sealed class FakeHttpHandler(
        string response,
        string contentType,
        HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(response, Encoding.UTF8, contentType)
            });
        }
    }

    private static HttpResponseMessage JsonResponse(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed class RoutingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<(string Uri, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.AbsoluteUri, body));
            return response(request);
        }
    }

    private sealed class FakeItemService : IItemService
    {
        public FakeItemService(ItemMetadata item)
        {
            Item = item;
        }

        public ItemMetadata Item { get; private set; }
        public int GetCount { get; private set; }
        public UpdateItemRequest? LastUpdate { get; private set; }
        public List<ItemIdentifier> AddedIdentifiers { get; } = [];

        public Task<Result<ItemMetadata>> GetItemAsync(ItemId itemId, CancellationToken cancellationToken = default)
        {
            GetCount++;
            return Task.FromResult(Result<ItemMetadata>.Success(Item));
        }

        public Task<Result<ItemMetadata>> UpdateItemAsync(ItemId itemId, UpdateItemRequest request,
            CancellationToken cancellationToken = default)
        {
            LastUpdate = request;
            Item = Item with
            {
                Title = request.Title, Subtitle = request.Subtitle, Publisher = request.Publisher,
                TagsJson = request.TagsJson ?? "[]", CollectionsJson = request.CollectionsJson ?? "[]",
                CustomFieldsJson = request.CustomFieldsJson ?? "{}"
            };
            return Task.FromResult(Result<ItemMetadata>.Success(Item));
        }

        public Task<Result<ItemMetadata>> ReplaceItemAsync(ItemId itemId, UpdateItemRequest request,
            IReadOnlyList<ItemIdentifierInput> identifiers, CancellationToken cancellationToken = default)
        {
            return UpdateItemAsync(itemId, request, cancellationToken);
        }

        public Task<Result<ItemIdentifier>> AddIdentifierAsync(ItemId itemId, string scheme, string value, string? note,
            CancellationToken cancellationToken = default)
        {
            ItemIdentifier identifier = new(IdentifierId.New(), itemId, scheme, value, note, DateTimeOffset.UtcNow);
            AddedIdentifiers.Add(identifier);
            Item = Item with { Identifiers = Item.Identifiers.Concat([identifier]).ToArray() };
            return Task.FromResult(Result<ItemIdentifier>.Success(identifier));
        }

        public Task<Result<IReadOnlyList<ItemIdentifier>>> ListIdentifiersAsync(ItemId itemId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<IReadOnlyList<ItemIdentifier>>.Success(Item.Identifiers));
        }

        public Task<Result> RemoveIdentifierAsync(ItemId itemId, IdentifierId identifierId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<ItemMetadata>> CreateItemAsync(CreateItemRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<ItemMetadata>> CreateItemAsync(string itemType, string title, string? subtitle = null,
            string? titleShort = null, string? creatorsJson = null, string? date = null,
            string? publicationTitle = null, string? containerTitleShort = null, string? collectionTitle = null,
            string? publisher = null, string? place = null, string? edition = null, string? genre = null,
            string? number = null, string? chapterNumber = null, string? volume = null, string? version = null,
            string? issue = null, string? pages = null, string? language = null, string? status = null,
            string? note = null, string? abstractText = null, string? tagsJson = null, string? collectionsJson = null,
            string? customFieldsJson = null, IReadOnlyList<ItemCreatorInput>? creators = null,
            IReadOnlyList<ItemDateInput>? dates = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result> DeleteItemAsync(ItemId itemId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Result<ItemListPage>> ListItemsAsync(ListItemsRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
