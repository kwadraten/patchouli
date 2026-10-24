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

    // Fixture trimmed from the live response for CRID 1910302385685827200
    // (https://cir.nii.ac.jp/crid/1910302385685827200.json, retrieved 2026-10-24): CiNii Research
    // serves Dublin Core JSON-LD — dc:title language-tagged values, creator objects with
    // foaf:name, and productIdentifier entries — not the schema.org shape.
    [Fact]
    public async Task Cinii_crid_parses_dublin_core_json_ld()
    {
        const string json = """
                            {"@id":"https://cir.nii.ac.jp/crid/1910302385685827200.json","@type":"Dissertation","dc:title":[{"@value":"日本近世地域社会の研究"},{"@language":"ja-Kana","@value":"ニホン キンセイ チイキ シャカイ ノ ケンキュウ"}],"dc:language":"ja","description":[{"type":"Other","notation":[{"@value":"博士学位論文 (Thesis(doctor))"}]},{"notation":[{"@value":"博士論文"}]}],"creator":[{"@id":"https://cir.nii.ac.jp/crid/1070282677705676288","@type":"Researcher","foaf:name":[{"@value":"平川,  新"}]}],"ndl:dateGranted":"1995-01-12","productIdentifier":[{"identifier":{"@type":"DOI","@value":"10.11501/3080788"}},{"identifier":{"@type":"NAID","@value":"500001124362"}},{"identifier":{"@type":"NDL_BIB_ID","@value":"000000280987"}}]}
                            """;
        Harness harness = await Harness.LookupAsync("metadata.cinii", BuiltInIdentifierSchemes.Crid,
            "1910302385685827200", _ => JsonResponse(json));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("日本近世地域社会の研究");
        harness.Result.Value.Creators.Should().Contain(creator => creator.Literal!.Contains("平川"));
        harness.Result.Value.Language.Should().Be("ja");
        harness.Result.Value.Dates.Should().Contain(date => date.Literal == "1995-01-12");
        harness.Result.Value.Abstract.Should().BeNull("CiNii notation entries are classification notes, not abstracts");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.DOI && identifier.Value == "10.11501/3080788");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.Naid && identifier.Value == "500001124362");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.Ndlbibid && identifier.Value == "000000280987");
        harness.Requests[0].Uri.AbsoluteUri.Should()
            .Contain("cir.nii.ac.jp/crid/1910302385685827200.json");
    }

    // Fixture trimmed from the live response for NCID BN0368958X
    // (https://ci.nii.ac.jp/ncid/BN0368958X.json, retrieved 2026-10-24): the legacy endpoint wraps
    // the entity in @graph and uses the same Dublin Core vocabulary.
    [Fact]
    public async Task Cinii_ncid_parses_legacy_graph_json_ld()
    {
        const string json = """
                            {"@graph":[{"@id":"https://ci.nii.ac.jp/ncid/BN0368958X#entity","@type":"bibo:Book","dc:title":[{"@value":"世界各国の憲法集"},{"@value":"セカイ カッコク ノ ケンポウシュウ","@language":"ja-hrkt"}],"dc:creator":"大石憲法研究所編","dc:publisher":[{"@value":"嵯峨野書院"}],"dc:language":"jpn","dc:date":"1973","prism:publicationDate":["1973.9"],"cinii:ncid":"BN0368958X","foaf:maker":[{"@id":"https://ci.nii.ac.jp/author/DA0123705X#entity","@type":"foaf:Person","foaf:name":[{"@value":"大石憲法研究所"},{"@value":"オオイシ ケンポウ ケンキュウジョ","@language":"ja-hrkt"}]}]}]}
                            """;
        Harness harness = await Harness.LookupAsync("metadata.cinii", BuiltInIdentifierSchemes.Ncid,
            "BN0368958X", _ => JsonResponse(json));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("世界各国の憲法集");
        harness.Result.Value.Publisher.Should().Be("嵯峨野書院");
        harness.Result.Value.Language.Should().Be("jpn");
        harness.Result.Value.Dates.Should().Contain(date => date.Literal == "1973.9");
        harness.Result.Value.Creators.Should().Contain(creator => creator.Literal == "大石憲法研究所");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.Ncid && identifier.Value == "BN0368958X");
        harness.Requests[0].Uri.AbsoluteUri.Should().Contain("ci.nii.ac.jp/ncid/BN0368958X.json");
    }

    // Fixture trimmed from the live response for OpenAlex work W2159974629
    // (DOI 10.1038/nature12373, retrieved 2026-10-24): ids.doi is a full URL whose path is the
    // DOI itself, so it must survive identifier discovery intact.
    [Fact]
    public async Task OpenAlex_preserves_full_doi_and_discovers_mag()
    {
        const string json = """
                            {"id":"https://openalex.org/W2159974629","doi":"https://doi.org/10.1038/nature12373","title":"Nanometre-scale thermometry in a living cell","display_name":"Nanometre-scale thermometry in a living cell","publication_year":2013,"type":"article","language":"en","ids":{"openalex":"https://openalex.org/W2159974629","doi":"https://doi.org/10.1038/nature12373","mag":"2159974629","pmid":"https://pubmed.ncbi.nlm.nih.gov/23903748"},"biblio":{"volume":"500","issue":"7460","first_page":"54","last_page":"58"},"primary_location":{"source":{"display_name":"Nature"}},"authorships":[{"author":{"display_name":"Georg Kucsko"}},{"author":{"display_name":"Peter C. Maurer"}}]}
                            """;
        Harness harness = await Harness.LookupAsync("metadata.openalex", BuiltInIdentifierSchemes.DOI,
            "10.1038/nature12373", _ => JsonResponse(json));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("Nanometre-scale thermometry in a living cell");
        harness.Result.Value.PublicationTitle.Should().Be("Nature");
        harness.Result.Value.Pages.Should().Be("54-58");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.DOI && identifier.Value == "10.1038/nature12373");
        harness.Result.Value.Identifiers.Should().NotContain(identifier => identifier.Value == "nature12373");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.Mag && identifier.Value == "2159974629");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.Pmid && identifier.Value == "23903748");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.OpenAlex && identifier.Value == "W2159974629");
    }

    // Fixture trimmed from the live Semantic Scholar response for DOI 10.1038/nature12373
    // (retrieved 2026-10-24, archived in the metadata audit): publicationTypes carries the
    // unhyphenated "JournalArticle", which the shared mapType must map to article-journal;
    // the abstract is trimmed to its first sentence.
    [Fact]
    public async Task Semantic_scholar_maps_unhyphenated_journal_article_type()
    {
        const string json = """
                            {"paperId":"a5de30adc5c22bc86e8cfabe7fbd07c052d196a8","externalIds":{"MAG":"2159974629","ArXiv":"1304.1068","PubMedCentral":"4221854","DOI":"10.1038/nature12373","CorpusId":4314121,"PubMed":"23903748"},"title":"Nanometer scale thermometry in a living cell","venue":"Nature","year":2013,"publicationTypes":["JournalArticle"],"journal":{"name":"Nature","pages":"54 - 58","volume":"500"},"authors":[{"authorId":"4301543","name":"G. Kucsko"},{"authorId":"47762783","name":"P. Maurer"},{"authorId":"4138853","name":"N. Yao"},{"authorId":"2068210745","name":"Michael Kubo"},{"authorId":"2250179130","name":"H. Noh"},{"authorId":"47472726","name":"Peggy Lo"},{"authorId":"9236657","name":"Hongkun Park"},{"authorId":"145572474","name":"M. Lukin"}],"abstract":"Sensitive probing of temperature variations on nanometre scales is an outstanding challenge in many areas of modern science and technology."}
                            """;
        Harness harness = await Harness.LookupAsync("metadata.semantic-scholar", BuiltInIdentifierSchemes.DOI,
            "10.1038/nature12373", _ => JsonResponse(json));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("Nanometer scale thermometry in a living cell");
        harness.Result.Value.PublicationTitle.Should().Be("Nature");
        harness.Result.Value.Volume.Should().Be("500");
        harness.Result.Value.Pages.Should().Be("54 - 58");
        harness.Result.Value.Dates.Should().ContainSingle(date => date.DateParts!.SequenceEqual(new[] { 2013 }));
        harness.Result.Value.Creators.Should().HaveCount(8);
        harness.Result.Value.Creators.Should().Contain(creator => creator.Literal == "G. Kucsko");
        harness.Result.Value.Abstract.Should().StartWith("Sensitive probing of temperature variations");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.DOI && identifier.Value == "10.1038/nature12373");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.ArXiv && identifier.Value == "1304.1068");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.Pmid && identifier.Value == "23903748");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == "semantic_scholar" && identifier.Value == "4314121");
        harness.Result.Value.SuggestedItemType.Should().Be("article-journal");
        harness.Result.Value.TypeConfidence.Should().Be(0.9);
        harness.Requests.Should().ContainSingle();
        harness.Requests[0].Uri.Host.Should().Be("api.semanticscholar.org");
        harness.Requests[0].Uri.AbsoluteUri.Should().Contain("DOI%3A10.1038%2Fnature12373");
        harness.Requests[0].Uri.Query.Should().Contain("publicationTypes");
    }

    // Fixture trimmed from the live response for LCCN 2008024750
    // (https://www.loc.gov/item/2008024750/?fo=json, retrieved 2026-10-24): all bibliographic
    // fields live inside "item"; contributors are {name: url} objects, the name list is in
    // contributor_names, and publisher/place exist only in the created_published free text.
    [Fact]
    public async Task Library_of_congress_reads_item_fields_and_created_published()
    {
        const string json = """
                            {"item":{"title":"Clean code : a handbook of agile software craftsmanship","date":"2009","contributor_names":["Martin, Robert C."],"contributors":[{"martin, robert c.":"https://www.loc.gov/search/?fa=contributor:martin,+robert+c.&fo=json"}],"subject":["reliability","computer software","agile software development"],"language":["english"],"created_published":["Upper Saddle River, NJ : Prentice Hall, c2009."]}}
                            """;
        Harness harness = await Harness.LookupAsync("metadata.library-of-congress",
            BuiltInIdentifierSchemes.Lccn, "2008024750", _ => JsonResponse(json));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("Clean code : a handbook of agile software craftsmanship");
        harness.Result.Value.Creators.Should().Contain(creator => creator.Literal == "Martin, Robert C.");
        harness.Result.Value.Language.Should().Be("english");
        harness.Result.Value.Tags.Should().Contain("agile software development");
        harness.Result.Value.Publisher.Should().Be("Prentice Hall");
        harness.Result.Value.Place.Should().Be("Upper Saddle River, NJ");
        harness.Result.Value.Dates.Should().Contain(date => date.Literal == "2009");
    }

    // Fixture is the live OpenSearch response for any=R100000002-I000000280987 (retrieved
    // 2026-10-24, archived in the metadata audit): the response carries the BibID only in short
    // form, and the item's first description element is the RSS HTML blob while the real abstract
    // is the dc:description text.
    [Fact]
    public async Task Ndl_matches_url_form_bib_id_and_prefers_dc_description()
    {
        const string xml = """
                           <rss xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcndl="http://ndl.go.jp/dcndl/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" version="2.0"><channel><title>R100000002-I000000280987 - 国立国会図書館サーチ OpenSearch</title><item><title>日本近世地域社会の研究</title><link>https://ndlsearch.ndl.go.jp/books/R100000002-I000000280987</link><description><![CDATA[<p><p><ul><li>タイトル：日本近世地域社会の研究</li><li>責任表示：平川新 [著]</li></ul>]]></description><author>平川, 新, 1950-,平川新 [著]</author><dc:title>日本近世地域社会の研究</dc:title><dc:creator>平川, 新, 1950-</dc:creator><dc:identifier xsi:type="dcndl:NDLBibID">000000280987</dc:identifier><dc:subject xsi:type="dcndl:NDLC">UT51</dc:subject><dc:description>博士論文</dc:description><dc:description/></item></channel></rss>
                           """;
        Harness harness = await Harness.LookupAsync("metadata.ndl", BuiltInIdentifierSchemes.Ndlbibid,
            "R100000002-I000000280987", _ => XmlResponse(xml));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("日本近世地域社会の研究");
        harness.Result.Value.Abstract.Should().Be("博士論文");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.Ndlbibid && identifier.Value == "000000280987");
        harness.Requests[0].Uri.Query.Should().Contain("any=R100000002-I000000280987");
    }

    // Fixture rows are verbatim from the live Aleph page for ISBN 9787020002207 (retrieved
    // 2026-10-24, archived in the metadata audit): Aleph emits attribute values WITHOUT quotes
    // (<td class=td1>), which the cell extractor must accept. The script line reproduces how the
    // requested ISBN appears on the real page.
    [Fact]
    public async Task National_library_of_china_parses_unquoted_aleph_attributes()
    {
        const string html = """
                            <html><script>g('cclterm').value=parseccl('ISB = \"9787020002207\"');</script><table id="td" width="90%"><tr>
                              <td style="background:#fff;" class=td1 id=bold width=15% valign=center nowrap> 题名与责任 </td>
                              <td style="background:#fff;" class=td1 align=left > 红楼梦&nbsp;[专著]&nbsp;/&nbsp;曹雪芹著&nbsp;;&nbsp;无名氏续&nbsp;;&nbsp;程伟元，高鹗整理 </td>
                            </tr><tr>
                              <td style="background:#fff;" class=td1 id=bold width=15% valign=center nowrap> 出版项 </td>
                              <td style="background:#fff;" class=td1 align=left > 北京&nbsp;:&nbsp;人民文学出版社,&nbsp;2022,&nbsp;2025重印) </td>
                            </tr><tr>
                              <td style="background:#fff;" class=td1 id=bold width=15% valign=center nowrap> 著者 </td>
                              <td style="background:#fff;" class=td1 align=left > (清)曹雪芹 (1715/1724~1763/1764) 著 </td>
                            </tr><tr>
                              <td style="background:#fff;" class=td1 id=bold width=15% valign=center nowrap> 主题 </td>
                              <td style="background:#fff;" class=td1 align=left > 古典小说 -- 中国 -- 清代 </td>
                            </tr><tr>
                              <td style="background:#fff;" class=td1 id=bold width=15% valign=center nowrap> 中图分类号 </td>
                              <td style="background:#fff;" class=td1 align=left > I242.47 </td>
                            </tr></table></html>
                            """;
        Harness harness = await Harness.LookupAsync("metadata.nlc", BuiltInIdentifierSchemes.ISBN,
            "9787020002207", _ => HtmlResponse(html));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("红楼梦");
        harness.Result.Value.Publisher.Should().Be("人民文学出版社");
        harness.Result.Value.Dates.Should().ContainSingle(date => date.DateParts!.SequenceEqual(new[] { 2022 }));
        harness.Result.Value.Creators.Should().Contain(creator => creator.Literal!.Contains("曹雪芹"));
        harness.Result.Value.Tags.Should().Contain(["古典小说", "I242.47"]);
    }

    // Fixture trimmed from the live sru/authorities response for GND 118540238 (Goethe,
    // retrieved 2026-10-24, archived in the metadata audit): authority records have no 245
    // title field; the authorized heading is field 100.
    [Fact]
    public async Task Dnb_gnd_uses_authorities_endpoint_and_heading()
    {
        const string xml = """
                           <searchRetrieveResponse xmlns="http://www.loc.gov/zing/srw/"><version>1.1</version><numberOfRecords>1</numberOfRecords><records><record><recordData><record xmlns="http://www.loc.gov/MARC21/slim"><controlfield tag="001">118540238</controlfield><controlfield tag="003">DE-101</controlfield><datafield tag="024" ind1="7" ind2=" "><subfield code="a">118540238</subfield><subfield code="0">http://d-nb.info/gnd/118540238</subfield><subfield code="2">gnd</subfield></datafield><datafield tag="100" ind1="1" ind2=" "><subfield code="a">Goethe, Johann Wolfgang von</subfield><subfield code="d">1749-1832</subfield></datafield><datafield tag="548" ind1=" " ind2=" "><subfield code="a">1749-1832</subfield><subfield code="4">datl</subfield></datafield><datafield tag="550" ind1=" " ind2=" "><subfield code="a">Schriftsteller</subfield><subfield code="4">berc</subfield></datafield></record></recordData></record></records></searchRetrieveResponse>
                           """;
        Harness harness = await Harness.LookupAsync("metadata.dnb", BuiltInIdentifierSchemes.Gnd,
            "118540238", _ => XmlResponse(xml));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("Goethe, Johann Wolfgang von");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.Gnd && identifier.Value == "118540238");
        harness.Requests[0].Uri.AbsoluteUri.Should()
            .Contain("services.dnb.de/sru/authorities").And.Contain("nid%3D118540238");
    }

    [Fact]
    public async Task Dnb_urn_lookup_is_rejected_as_unsupported()
    {
        Harness harness = await Harness.LookupAsync("metadata.dnb", BuiltInIdentifierSchemes.UrnNbnDe,
            "urn:nbn:de:101-1", _ => XmlResponse(""));

        harness.Result.IsFailure.Should().BeTrue();
        harness.Result.ErrorCode.Should().Be(MetadataLookupErrorCodes.UnsupportedIdentifier);
        harness.Requests.Should().BeEmpty("the DNB SRU service has no URN index to query");
    }

    // Fixture record is verbatim from the live catalogue response for ark:/12148/cb397758340
    // (retrieved 2026-10-24, archived in the metadata audit): INTERMARC 260 carries the place in
    // $a, the PUBLISHER in $c and the date in $d — the MARC21 reading put the publisher name in
    // the date and left the publisher empty.
    [Fact]
    public async Task Bnf_ark_query_uses_full_form_and_intermarc_260()
    {
        const string xml = """
                           <srw:searchRetrieveResponse xmlns:srw="http://www.loc.gov/zing/srw/" xmlns:mxc="info:lc/xmlns/marcxchange-v2"><srw:version>1.2</srw:version><srw:numberOfRecords>1</srw:numberOfRecords><srw:records><srw:record><srw:recordData><mxc:record xmlns:mxc="info:lc/xmlns/marcxchange-v2" format="INTERMARC" id="ark:/12148/cb397758340" type="Bibliographic"><mxc:controlfield tag="001">FRBNF397758340000009</mxc:controlfield><mxc:controlfield tag="003">http://catalogue.bnf.fr/ark:/12148/cb397758340</mxc:controlfield><mxc:datafield tag="100" ind1=" " ind2=" "><mxc:subfield code="a">Christie</mxc:subfield><mxc:subfield code="m">John</mxc:subfield></mxc:datafield><mxc:datafield tag="245" ind1="1" ind2=" "><mxc:subfield code="a">Emile Zola</mxc:subfield><mxc:subfield code="d">Texte imprimé</mxc:subfield><mxc:subfield code="e">Germinal</mxc:subfield></mxc:datafield><mxc:datafield tag="260" ind1=" " ind2=" "><mxc:subfield code="a">Paris</mxc:subfield><mxc:subfield code="c">Didier</mxc:subfield><mxc:subfield code="d">1974</mxc:subfield></mxc:datafield><mxc:datafield tag="700" ind1=" " ind2=" "><mxc:subfield code="a">Tancock</mxc:subfield><mxc:subfield code="m">Léonard</mxc:subfield></mxc:datafield></mxc:record></srw:recordData></srw:record></srw:records></srw:searchRetrieveResponse>
                           """;
        Harness harness = await Harness.LookupAsync("metadata.bnf", BuiltInIdentifierSchemes.Ark,
            "12148/cb397758340", _ => XmlResponse(xml));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("Emile Zola");
        // INTERMARC 245 carries the subtitle in $e ("Germinal"); MARC21 has no 245 $e.
        harness.Result.Value.Subtitle.Should().Be("Germinal");
        harness.Result.Value.Publisher.Should().Be("Didier");
        harness.Result.Value.Place.Should().Be("Paris");
        harness.Result.Value.Dates.Should().Contain(date => date.Literal == "1974");
        harness.Result.Value.Creators.Should().Contain(creator => creator.Literal == "Christie, John");
        harness.Result.Value.Creators.Should().Contain(creator => creator.Literal == "Tancock, Léonard");
        harness.Requests[0].Uri.AbsoluteUri.Should()
            .Contain("bib.ark").And.Contain("ark%3A%2F12148%2Fcb397758340");
    }

    [Fact]
    public async Task Bnf_frbnf_uses_recordid_index()
    {
        const string xml = """
                           <srw:searchRetrieveResponse xmlns:srw="http://www.loc.gov/zing/srw/" xmlns:mxc="info:lc/xmlns/marcxchange-v2"><srw:version>1.2</srw:version><srw:numberOfRecords>1</srw:numberOfRecords><srw:records><srw:record><srw:recordData><mxc:record xmlns:mxc="info:lc/xmlns/marcxchange-v2" format="INTERMARC" id="ark:/12148/cb397758340" type="Bibliographic"><mxc:controlfield tag="001">FRBNF397758340000009</mxc:controlfield><mxc:datafield tag="245" ind1="1" ind2=" "><mxc:subfield code="a">Emile Zola</mxc:subfield></mxc:datafield><mxc:datafield tag="260" ind1=" " ind2=" "><mxc:subfield code="a">Paris</mxc:subfield><mxc:subfield code="c">Didier</mxc:subfield><mxc:subfield code="d">1974</mxc:subfield></mxc:datafield></mxc:record></srw:recordData></srw:record></srw:records></srw:searchRetrieveResponse>
                           """;
        Harness harness = await Harness.LookupAsync("metadata.bnf", BuiltInIdentifierSchemes.Frbnf,
            "FRBNF397758340000009", _ => XmlResponse(xml));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("Emile Zola");
        harness.Requests[0].Uri.AbsoluteUri.Should().Contain("bib.recordid");
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
