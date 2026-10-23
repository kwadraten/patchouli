using System.Collections.Concurrent;
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
/// Independent verification for the 2026-10-24 metadata provider parsing fixes: every case
/// replays a response captured live from the real provider for an identifier that is NOT used
/// by any fixture in BuiltInMetadataSourceTests, through the real compiled built-in package.
/// Capture date for all responses: 2026-10-24. URLs are noted per case.
/// </summary>
[Trait("Category", "compiled-extension")]
public sealed class MetadataProviderIndependentVerificationTests
{
    // Live: GET https://api.openalex.org/works/doi:10.1038/s41586-021-03819-2 (HTTP 200).
    // ids carries doi as a full URL, mag as a bare number and pmid as a PubMed URL.
    [Fact]
    public async Task Verify_openalex_fresh_work_keeps_full_doi_and_discovers_mag()
    {
        const string json = """
                            {"id": "https://openalex.org/W3177828909", "doi": "https://doi.org/10.1038/s41586-021-03819-2", "title": "Highly accurate protein structure prediction with AlphaFold", "display_name": "Highly accurate protein structure prediction with AlphaFold", "publication_year": 2021, "type": "article", "language": "en", "ids": {"openalex": "https://openalex.org/W3177828909", "doi": "https://doi.org/10.1038/s41586-021-03819-2", "mag": "3177828909", "pmid": "https://pubmed.ncbi.nlm.nih.gov/34265844"}, "biblio": {"volume": "596", "issue": "7873", "first_page": "583", "last_page": "589"}, "primary_location": {"source": {"display_name": "Nature"}}, "authorships": [{"author": {"display_name": "John Jumper"}}, {"author": {"display_name": "Richard Evans"}}, {"author": {"display_name": "Alexander Pritzel"}}]}
                            """;
        Harness harness = await Harness.LookupAsync("metadata.openalex", BuiltInIdentifierSchemes.DOI,
            "10.1038/s41586-021-03819-2", _ => JsonResponse(json));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("Highly accurate protein structure prediction with AlphaFold");
        harness.Result.Value.PublicationTitle.Should().Be("Nature");
        harness.Result.Value.Creators.Should().Contain(creator => creator.Literal == "John Jumper");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.DOI &&
            identifier.Value == "10.1038/s41586-021-03819-2");
        harness.Result.Value.Identifiers.Should().NotContain(identifier =>
            identifier.Value == "s41586-021-03819-2");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.Mag && identifier.Value == "3177828909");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.Pmid && identifier.Value == "34265844");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.OpenAlex && identifier.Value == "W3177828909");
    }

    // Live: GET https://ci.nii.ac.jp/ncid/BN00247778.json (HTTP 200), @graph Dublin Core shape.
    [Fact]
    public async Task Verify_cinii_fresh_ncid_parses_dc_graph()
    {
        const string json = """
                            {"@graph": [{"@id": "https://ci.nii.ac.jp/ncid/BN00247778#entity", "@type": "bibo:Book", "dc:title": [{"@value": "講座日本歴史"}, {"@value": "コウザ ニホン レキシ", "@language": "ja-hrkt"}], "dc:creator": "歴史学研究会, 日本史研究会編", "dc:publisher": [{"@value": "東京大学出版会"}], "dc:language": "jpn", "dc:date": "1984", "prism:publicationDate": ["1984-1985"], "cinii:ncid": "BN00247778", "foaf:maker": [{"@id": "https://ci.nii.ac.jp/author/DA00144066#entity", "@type": "foaf:Person", "foaf:name": [{"@value": "歴史学研究会"}, {"@value": "レキシガク ケンキュウカイ", "@language": "ja-hrkt"}]}, {"@id": "https://ci.nii.ac.jp/author/DA00319589#entity", "@type": "foaf:Person", "foaf:name": [{"@value": "日本史研究会"}, {"@value": "ニホンシ ケンキュウカイ", "@language": "ja-hrkt"}]}], "dc:subject": ["NDC8:210.08", "NDC8:210.1", "LCC:DS835"]}]}
                            """;
        Harness harness = await Harness.LookupAsync("metadata.cinii", BuiltInIdentifierSchemes.Ncid,
            "BN00247778", _ => JsonResponse(json));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("講座日本歴史");
        harness.Result.Value.Publisher.Should().Be("東京大学出版会");
        harness.Result.Value.Language.Should().Be("jpn");
        harness.Result.Value.Dates.Should().Contain(date => date.Literal == "1984-1985");
        harness.Result.Value.Creators.Should().Contain(creator => creator.Literal == "歴史学研究会");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.Ncid && identifier.Value == "BN00247778");
        harness.Requests[0].Uri.AbsoluteUri.Should().Contain("ci.nii.ac.jp/ncid/BN00247778.json");
    }

    // Live: GET https://www.loc.gov/item/2017380745/?fo=json (HTTP 200). All fields live inside
    // "item"; contributor_names carries a role suffix ("author, translator.") that the lowercase
    // contributors object keys do not have.
    [Fact]
    public async Task Verify_loc_fresh_lccn_reads_item_fields()
    {
        const string json = """
                            {"item": {"title": "Homo Deus : A Brief History of Tomorrow", "date": "2017", "contributor_names": ["Harari, Yuval N., author, translator."], "contributors": [{"harari, yuval n.": "https://www.loc.gov/search/?fa=contributor:harari,+yuval+n.&fo=json"}], "subject": ["effect of human beings on", "human beings", "21st century", "social science / future studies", "philosophical anthropology", "history, modern", "modern civilization", "science / life sciences / evolution", "history / civilization", "science and civilization", "technological forecasting", "technology and civilization", "nature", "history", "civilization, modern", "forecasting"], "language": ["english"], "created_published": ["New York, NY : Harper, an imprint of HarperCollins Publishers, [2017]", "©2017"]}}
                            """;
        Harness harness = await Harness.LookupAsync("metadata.library-of-congress",
            BuiltInIdentifierSchemes.Lccn, "2017380745", _ => JsonResponse(json));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("Homo Deus : A Brief History of Tomorrow");
        harness.Result.Value.Language.Should().Be("english");
        harness.Result.Value.Creators.Should().Contain(creator =>
            creator.Literal == "Harari, Yuval N., author, translator.");
        // created_published "...: Harper, an imprint of HarperCollins Publishers, [2017]": the
        // bracketed tail segment is the date, so the publisher is the full imprint string, not
        // just the first comma segment.
        harness.Result.Value.Publisher.Should().Be("Harper, an imprint of HarperCollins Publishers");
        harness.Result.Value.Place.Should().Be("New York, NY");
        harness.Result.Value.Dates.Should().Contain(date => date.Literal == "2017");
        harness.Result.Value.Tags.Should().Contain("civilization, modern");
    }

    // Live: GET https://ndlsearch.ndl.go.jp/api/opensearch?any=地域史&cnt=3 (HTTP 200). The
    // matching record (BibID 000007464904, a serial) is the second item; the first item carries
    // no identifier and must not be picked. Both the short and the portal (URL) form of the
    // BibID must match the same response.
    [Fact]
    public async Task Verify_ndl_fresh_bib_id_matches_in_short_and_url_forms()
    {
        const string xml = """
                           <rss xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:openSearch="http://a9.com/-/spec/opensearchrss/1.0/" xmlns:dcndl="http://ndl.go.jp/dcndl/terms/" xmlns:dcmitype="http://purl.org/dc/dcmitype/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:rdfs="http://www.w3.org/2000/01/rdf-schema#" xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" version="2.0">
                             <channel>
                               <title>地域史 - 国立国会図書館サーチ OpenSearch</title>
                               <link>https://ios-v2-prod-eks-alb.ndlsearch.ndl.go.jp/api/opensearch?any=%E5%9C%B0%E5%9F%9F%E5%8F%B2&amp;cnt=3</link>
                               <description>Search results for any=地域史 cnt=3</description>
                               <language>ja</language>
                               <openSearch:totalResults>11918</openSearch:totalResults>
                               <openSearch:startIndex>1</openSearch:startIndex>
                               <openSearch:itemsPerPage>3</openSearch:itemsPerPage>
                               <item>
                                 <title>アーカイブズ・カレッジテキスト : 史料管理学研修会講義要項</title>
                                 <link>https://ndlsearch.ndl.go.jp/books/R100000001-I44111109477660</link>
                                 <description><![CDATA[<p>平成１５年度,国文学研究資料館史料館,2003,<p><ul><li>タイトル：アーカイブズ・カレッジテキスト : 史料管理学研修会講義要項</li><li>タイトル（読み）：アーカイブズ　カレッジ　テキスト : シリョウ　カンリガク　ケンシュウカイ　コウギ　ヨウコウ</li><li>責任表示：国文学研究資料館史料館 〔編〕</li></ul>]]></description>
                                 <author>国文学研究資料館史料館,国文学研究資料館史料館 〔編〕</author>
                                 <category>図書</category>
                                 <category>紙</category>
                                 <guid isPermaLink="true">https://ndlsearch.ndl.go.jp/books/R100000001-I44111109477660</guid>
                                 <pubDate>Wed, 28 Nov 2012 17:24:03 +0900</pubDate>
                                 <dc:title>アーカイブズ・カレッジテキスト : 史料管理学研修会講義要項</dc:title>
                                 <dcndl:titleTranscription>アーカイブズ　カレッジ　テキスト : シリョウ　カンリガク　ケンシュウカイ　コウギ　ヨウコウ</dcndl:titleTranscription>
                                 <dc:creator>国文学研究資料館史料館</dc:creator>
                                 <dcndl:creatorTranscription>コクブンガクケンキュウシリョウカンシリョウカン</dcndl:creatorTranscription>
                                 <dcndl:volume>平成１５年度</dcndl:volume>
                                 <dc:publisher>国文学研究資料館史料館</dc:publisher>
                                 <dc:date xsi:type="dcterms:W3CDTF">2003</dc:date>
                                 <dcterms:issued>２００３．６</dcterms:issued>
                                 <dc:extent>１５４ｐ ; ３０ｃｍ</dc:extent>
                                 <dc:description>短期コース　大分：平成１５年１１月１０日〜１１月２１日</dc:description>
                                 <rdfs:seeAlso rdf:resource="https://library.pref.oita.jp/winj/opac/switch-detail-iccap.do?bibid=1109477660"/>
                                 <dc:description> 2003</dc:description>
                               </item>
                               <item>
                                 <title>アーカイブズ学研究</title>
                                 <link>https://ndlsearch.ndl.go.jp/books/R100000002-I000007464904-i4770936</link>
                                 <description><![CDATA[<p>21号 2014年12月,日本アーカイブズ学会,2014,<p><ul><li>タイトル：アーカイブズ学研究</li><li>タイトル（読み）：アーカイブズガク ケンキュウ</li><li>責任表示：日本アーカイブズ学会 編</li></ul>]]></description>
                                 <author>日本アーカイブズ学会,日本アーカイブズ学会 編</author>
                                 <category>雑誌</category>
                                 <category>紙</category>
                                 <guid isPermaLink="true">https://ndlsearch.ndl.go.jp/books/R100000002-I000007464904-i4770936</guid>
                                 <pubDate>Mon, 31 Mar 2025 22:49:23 +0900</pubDate>
                                 <dc:title>アーカイブズ学研究</dc:title>
                                 <dcndl:titleTranscription>アーカイブズガク ケンキュウ</dcndl:titleTranscription>
                                 <dc:creator>日本アーカイブズ学会</dc:creator>
                                 <dcndl:creatorTranscription>ニホン アーカイブズ ガッカイ</dcndl:creatorTranscription>
                                 <dcndl:volume>21号 2014年12月</dcndl:volume>
                                 <dc:publisher>日本アーカイブズ学会</dc:publisher>
                                 <dcndl:publicationPlace>JP</dcndl:publicationPlace>
                                 <dc:date xsi:type="dcterms:W3CDTF">2014</dc:date>
                                 <dcterms:issued>2014</dcterms:issued>
                                 <dc:identifier xsi:type="dcndl:NDLBibID">000007464904</dc:identifier>
                                 <dc:subject xsi:type="dcndl:NDLC">ZU8</dc:subject>
                                 <rdfs:seeAlso rdf:resource="https://ndlsearch.ndl.go.jp/books/R100000002-I000007464904-i4770936"/>
                                 <dc:description> 2014</dc:description>
                               </item>
                               <item>
                                 <title>アーカイブズ学研究</title>
                                 <link>https://ndlsearch.ndl.go.jp/books/R100000002-I000007464904-i4770778</link>
                                 <description><![CDATA[<p>6号-9号 2007年3月-2008年11月,日本アーカイブズ学会,2007-2008,<p><ul><li>タイトル：アーカイブズ学研究</li><li>タイトル（読み）：アーカイブズガク ケンキュウ</li><li>責任表示：日本アーカイブズ学会 編</li></ul>]]></description>
                                 <author>日本アーカイブズ学会,日本アーカイブズ学会 編</author>
                                 <category>雑誌</category>
                                 <category>紙</category>
                                 <guid isPermaLink="true">https://ndlsearch.ndl.go.jp/books/R100000002-I000007464904-i4770778</guid>
                                 <pubDate>Mon, 31 Mar 2025 22:49:23 +0900</pubDate>
                                 <dc:title>アーカイブズ学研究</dc:title>
                                 <dcndl:titleTranscription>アーカイブズガク ケンキュウ</dcndl:titleTranscription>
                                 <dc:creator>日本アーカイブズ学会</dc:creator>
                                 <dcndl:creatorTranscription>ニホン アーカイブズ ガッカイ</dcndl:creatorTranscription>
                                 <dcndl:volume>6号-9号 2007年3月-2008年11月</dcndl:volume>
                                 <dc:publisher>日本アーカイブズ学会</dc:publisher>
                                 <dcndl:publicationPlace>JP</dcndl:publicationPlace>
                                 <dc:date xsi:type="dcterms:W3CDTF">2007-2008</dc:date>
                                 <dcterms:issued>2007-2008</dcterms:issued>
                                 <dc:identifier xsi:type="dcndl:NDLBibID">000007464904</dc:identifier>
                                 <dc:subject xsi:type="dcndl:NDLC">ZU8</dc:subject>
                                 <rdfs:seeAlso rdf:resource="https://ndlsearch.ndl.go.jp/books/R100000002-I000007464904-i4770778"/>
                                 <dc:description> 2007-2008</dc:description>
                               </item>
                             </channel>
                           </rss>
                           """;

        Harness shortForm = await Harness.LookupAsync("metadata.ndl", BuiltInIdentifierSchemes.Ndlbibid,
            "000007464904", _ => XmlResponse(xml));
        shortForm.Result.IsSuccess.Should().BeTrue(shortForm.Result.ErrorMessage);
        shortForm.Result.Value.Title.Should().Be("アーカイブズ学研究");
        shortForm.Result.Value.Language.Should().BeNull();
        shortForm.Result.Value.Abstract.Should().NotContain("<");

        Harness urlForm = await Harness.LookupAsync("metadata.ndl", BuiltInIdentifierSchemes.Ndlbibid,
            "R100000002-I000007464904", _ => XmlResponse(xml));
        urlForm.Result.IsSuccess.Should().BeTrue(urlForm.Result.ErrorMessage);
        urlForm.Result.Value.Title.Should().Be("アーカイブズ学研究");
        urlForm.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.Ndlbibid &&
            identifier.Value == "000007464904");
    }

    // Live: GET https://services.dnb.de/sru/authorities?version=1.1&operation=searchRetrieve
    // &recordSchema=MARC21-xml&query=nid%3D118529579 (HTTP 200, 1 record, Einstein).
    [Fact]
    public async Task Verify_dnb_fresh_gnd_uses_authorities_heading()
    {
        const string xml = """
                           <?xml version="1.0" encoding="UTF-8"?>
                           <searchRetrieveResponse xmlns="http://www.loc.gov/zing/srw/"><version>1.1</version><numberOfRecords>1</numberOfRecords><records><record><recordSchema>MARC21-xml</recordSchema><recordPacking>xml</recordPacking><recordData><record xmlns="http://www.loc.gov/MARC21/slim" type="Authority">
                               <leader>00000nz  a2200000nc 4500</leader>
                               <controlfield tag="001">118529579</controlfield>
                               <controlfield tag="003">DE-101</controlfield>
                               
                               
                               <datafield tag="024" ind1="7" ind2=" ">
                                 <subfield code="a">118529579</subfield>
                                 <subfield code="0">http://d-nb.info/gnd/118529579</subfield>
                                 <subfield code="2">gnd</subfield>
                               </datafield>
                               <datafield tag="024" ind1="7" ind2=" ">
                                 <subfield code="a">5140127</subfield>
                                 <subfield code="2">geprishisp</subfield>
                               </datafield>
                               <datafield tag="024" ind1="7" ind2=" ">
                                 <subfield code="a">0000 0001 2281 955X</subfield>
                                 <subfield code="2">isni</subfield>
                               </datafield>
                               <datafield tag="024" ind1="7" ind2=" ">
                                 <subfield code="a">n79022889</subfield>
                                 <subfield code="2">lccn</subfield>
                               </datafield>
                               <datafield tag="024" ind1="7" ind2=" ">
                                 <subfield code="a">75121530</subfield>
                                 <subfield code="2">viaf</subfield>
                               </datafield>
                               <datafield tag="024" ind1="7" ind2=" ">
                                 <subfield code="a">n79022889</subfield>
                                 <subfield code="2">lccn</subfield>
                               </datafield>
                               <datafield tag="024" ind1="7" ind2=" ">
                                 <subfield code="a">Q937</subfield>
                                 <subfield code="9">v:Herkunft: musicb002</subfield>
                                 <subfield code="2">wikidata</subfield>
                               </datafield>
                               <datafield tag="024" ind1="7" ind2=" ">
                                 <subfield code="a">artist/c98c325e-7277-46e8-8b44-e3517f3e041a</subfield>
                                 <subfield code="9">v:Herkunft: musicb002</subfield>
                                 <subfield code="2">musicb</subfield>
                               </datafield>
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               <datafield tag="100" ind1="1" ind2=" ">
                                 <subfield code="a">Einstein, Albert</subfield>
                                 <subfield code="d">1879-1955</subfield>
                               </datafield>
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               <datafield tag="548" ind1=" " ind2=" ">
                                 <subfield code="a">1879-1955</subfield>
                                 <subfield code="4">datl</subfield>
                                 <subfield code="4">https://d-nb.info/standards/elementset/gnd#dateOfBirthAndDeath</subfield>
                                 <subfield code="w">r</subfield>
                                 <subfield code="i">Lebensdaten</subfield>
                               </datafield>
                               <datafield tag="548" ind1=" " ind2=" ">
                                 <subfield code="a">14.03.1879-18.04.1955</subfield>
                                 <subfield code="4">datx</subfield>
                                 <subfield code="4">https://d-nb.info/standards/elementset/gnd#dateOfBirthAndDeath</subfield>
                                 <subfield code="w">r</subfield>
                                 <subfield code="i">Exakte Lebensdaten</subfield>
                               </datafield>
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                               
                             </record></recordData><recordPosition>1</recordPosition></record></records><echoedSearchRetrieveRequest><version>1.1</version><query>nid=118529579</query><xQuery xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:nil="true"/><recordSchema>MARC21-xml</recordSchema></echoedSearchRetrieveRequest></searchRetrieveResponse>
                           """;
        Harness harness = await Harness.LookupAsync("metadata.dnb", BuiltInIdentifierSchemes.Gnd,
            "118529579", _ => XmlResponse(xml));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("Einstein, Albert");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.Gnd && identifier.Value == "118529579");
        harness.Requests[0].Uri.AbsoluteUri.Should()
            .Contain("services.dnb.de/sru/authorities").And.Contain("nid%3D118529579");
    }

    // Live: GET https://services.dnb.de/sru/authorities?version=1.1&operation=searchRetrieve
    // &recordSchema=MARC21-xml&query=nid%3D4005728-8 (HTTP 200, 1 record, retrieved 2026-10-24).
    // GND 4005728-8 is Berlin's place authority record: it has no 100/110/111/130 heading, the
    // authorized heading is the geographic name in 151 $a. The record below is trimmed to the
    // control fields, the GND 024 and the 151 field; those fields are verbatim from the live
    // response.
    [Fact]
    public async Task Verify_dnb_fresh_gnd_place_uses_151_heading()
    {
        const string xml = """
                           <?xml version="1.0" encoding="UTF-8"?>
                           <searchRetrieveResponse xmlns="http://www.loc.gov/zing/srw/"><version>1.1</version><numberOfRecords>1</numberOfRecords><records><record><recordSchema>MARC21-xml</recordSchema><recordPacking>xml</recordPacking><recordData><record xmlns="http://www.loc.gov/MARC21/slim" type="Authority">
                               <leader>00000nz  a2200000oc 4500</leader>
                               <controlfield tag="001">040057283</controlfield>
                               <controlfield tag="003">DE-101</controlfield>
                               <controlfield tag="005">20240928151500.0</controlfield>
                               <controlfield tag="008">880701n||azznnaabn           | ana    |c</controlfield>
                               <datafield tag="024" ind1="7" ind2=" ">
                                 <subfield code="a">4005728-8</subfield>
                                 <subfield code="0">http://d-nb.info/gnd/4005728-8</subfield>
                                 <subfield code="2">gnd</subfield>
                               </datafield>
                               <datafield tag="043" ind1=" " ind2=" ">
                                 <subfield code="c">XA-DE-BE</subfield>
                               </datafield>
                               <datafield tag="075" ind1=" " ind2=" ">
                                 <subfield code="b">g</subfield>
                                 <subfield code="2">gndgen</subfield>
                               </datafield>
                               <datafield tag="151" ind1=" " ind2=" ">
                                 <subfield code="a">Berlin</subfield>
                               </datafield>
                             </record></recordData><recordPosition>1</recordPosition></record></records><echoedSearchRetrieveRequest><version>1.1</version><query>nid=4005728-8</query><recordSchema>MARC21-xml</recordSchema></echoedSearchRetrieveRequest></searchRetrieveResponse>
                           """;
        Harness harness = await Harness.LookupAsync("metadata.dnb", BuiltInIdentifierSchemes.Gnd,
            "4005728-8", _ => XmlResponse(xml));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("Berlin");
        harness.Result.Value.Identifiers.Should().Contain(identifier =>
            identifier.Scheme == BuiltInIdentifierSchemes.Gnd && identifier.Value == "040057283");
        harness.Requests[0].Uri.AbsoluteUri.Should()
            .Contain("services.dnb.de/sru/authorities").And.Contain("nid%3D4005728-8");
    }

    // Live: GET https://services.dnb.de/sru/dnb?...&query=idn%3D125686871X (HTTP 200).
    // MARC21 264 carries place $a / publisher $b / date $c and must keep the MARC21 reading.
    [Fact]
    public async Task Verify_dnb_fresh_idn_keeps_marc21_reading()
    {
        const string xml = """
                           <?xml version="1.0" encoding="UTF-8"?>
                           <searchRetrieveResponse xmlns="http://www.loc.gov/zing/srw/"><version>1.1</version><numberOfRecords>1</numberOfRecords><records><record><recordSchema>MARC21-xml</recordSchema><recordPacking>xml</recordPacking><recordData><record xmlns="http://www.loc.gov/MARC21/slim" type="Bibliographic">
                               <leader>00000nam a22000008c 4500</leader>
                               <controlfield tag="001">125686871X</controlfield>
                               <controlfield tag="003">DE-101</controlfield>
                               <controlfield tag="005">20250219130150.0</controlfield>
                               <controlfield tag="007">tu</controlfield>
                               <controlfield tag="008">220505s2050    au ||||| |||| 00||||ger  </controlfield>
                               <datafield tag="015" ind1=" " ind2=" ">
                                 <subfield code="a">22,N19</subfield>
                                 <subfield code="2">dnb</subfield>
                               </datafield>
                               <datafield tag="016" ind1="7" ind2=" ">
                                 <subfield code="2">DE-101</subfield>
                                 <subfield code="a">125686871X</subfield>
                               </datafield>
                               <datafield tag="020" ind1=" " ind2=" ">
                                 <subfield code="a">9783755300274</subfield>
                                 <subfield code="c">: EUR 12.00 (DE), EUR 12.00 (AT), CHF 17.90 (freier Preis)</subfield>
                                 <subfield code="9">978-3-7553-0027-4</subfield>
                               </datafield>
                               <datafield tag="020" ind1=" " ind2=" ">
                                 <subfield code="a">3755300273</subfield>
                                 <subfield code="9">3-7553-0027-3</subfield>
                               </datafield>
                               <datafield tag="024" ind1="3" ind2=" ">
                                 <subfield code="a">9783755300274</subfield>
                               </datafield>
                               <datafield tag="028" ind1="5" ind2="2">
                                 <subfield code="a">Bestellnummer 790027</subfield>
                               </datafield>
                               <datafield tag="035" ind1=" " ind2=" ">
                                 <subfield code="a">(DE-599)DNB125686871X</subfield>
                               </datafield>
                               <datafield tag="035" ind1=" " ind2=" ">
                                 <subfield code="a">(OCoLC)1315749022</subfield>
                               </datafield>
                               <datafield tag="040" ind1=" " ind2=" ">
                                 <subfield code="a">1145</subfield>
                                 <subfield code="b">ger</subfield>
                                 <subfield code="c">DE-101</subfield>
                                 <subfield code="d">9999</subfield>
                               </datafield>
                               <datafield tag="041" ind1=" " ind2=" ">
                                 <subfield code="a">ger</subfield>
                               </datafield>
                               <datafield tag="044" ind1=" " ind2=" ">
                                 <subfield code="c">AT</subfield>
                               </datafield>
                               <datafield tag="082" ind1="7" ind2="4">
                                 <subfield code="8">2\p</subfield>
                                 <subfield code="a">830</subfield>
                                 <subfield code="a">B</subfield>
                                 <subfield code="q">DE-101</subfield>
                                 <subfield code="2">23sdnb</subfield>
                               </datafield>
                               <datafield tag="100" ind1="1" ind2=" ">
                                 <subfield code="8">1\p</subfield>
                                 <subfield code="a">Goethe, Johann Wolfgang von</subfield>
                                 <subfield code="e">Verfasser</subfield>
                                 <subfield code="4">aut</subfield>
                               </datafield>
                               <datafield tag="245" ind1="1" ind2="0">
                                 <subfield code="a">Faust I, II und Urfaust</subfield>
                                 <subfield code="b">Illustrierte Ausgabe</subfield>
                                 <subfield code="c">Johann Wolfgang von Goethe</subfield>
                               </datafield>
                               <datafield tag="263" ind1=" " ind2=" ">
                                 <subfield code="a">205012</subfield>
                               </datafield>
                               <datafield tag="264" ind1=" " ind2="1">
                                 <subfield code="a">Königswinter</subfield>
                                 <subfield code="b">Petersberg Verlag</subfield>
                                 <subfield code="c">2050</subfield>
                               </datafield>
                               <datafield tag="300" ind1=" " ind2=" ">
                                 <subfield code="a">576 Seiten</subfield>
                                 <subfield code="b">mit zahlr. Abbildungen, mit Lesebändchen</subfield>
                                 <subfield code="c">20 cm</subfield>
                               </datafield>
                               <datafield tag="336" ind1=" " ind2=" ">
                                 <subfield code="a">Text</subfield>
                                 <subfield code="b">txt</subfield>
                                 <subfield code="2">rdacontent</subfield>
                               </datafield>
                               <datafield tag="337" ind1=" " ind2=" ">
                                 <subfield code="a">ohne Hilfsmittel zu benutzen</subfield>
                                 <subfield code="b">n</subfield>
                                 <subfield code="2">rdamedia</subfield>
                               </datafield>
                               <datafield tag="338" ind1=" " ind2=" ">
                                 <subfield code="a">Band</subfield>
                                 <subfield code="b">nc</subfield>
                                 <subfield code="2">rdacarrier</subfield>
                               </datafield>
                               <datafield tag="653" ind1=" " ind2=" ">
                                 <subfield code="a">(Produktform)Hardback</subfield>
                               </datafield>
                               <datafield tag="653" ind1=" " ind2=" ">
                                 <subfield code="a">Goethe</subfield>
                               </datafield>
                               <datafield tag="653" ind1=" " ind2=" ">
                                 <subfield code="a">Weimarer Klassik</subfield>
                               </datafield>
                               <datafield tag="653" ind1=" " ind2=" ">
                                 <subfield code="a">Literaturklassiker</subfield>
                               </datafield>
                               <datafield tag="653" ind1=" " ind2=" ">
                                 <subfield code="a">Klassiker der Weltliteratur</subfield>
                               </datafield>
                               <datafield tag="653" ind1=" " ind2=" ">
                                 <subfield code="a">Sturm und Drang</subfield>
                               </datafield>
                               <datafield tag="653" ind1=" " ind2=" ">
                                 <subfield code="a">Urfaust</subfield>
                               </datafield>
                               <datafield tag="653" ind1=" " ind2=" ">
                                 <subfield code="a">Faust I</subfield>
                               </datafield>
                               <datafield tag="653" ind1=" " ind2=" ">
                                 <subfield code="a">Faust II</subfield>
                               </datafield>
                               <datafield tag="653" ind1=" " ind2=" ">
                                 <subfield code="a">Tragödie</subfield>
                               </datafield>
                               <datafield tag="653" ind1=" " ind2=" ">
                                 <subfield code="a">Heinrich Faust</subfield>
                               </datafield>
                               <datafield tag="653" ind1=" " ind2=" ">
                                 <subfield code="a">Margarete</subfield>
                               </datafield>
                               <datafield tag="653" ind1=" " ind2=" ">
                                 <subfield code="a">Gretchen</subfield>
                               </datafield>
                               <datafield tag="653" ind1=" " ind2=" ">
                                 <subfield code="a">Valentin</subfield>
                               </datafield>
                               <datafield tag="653" ind1=" " ind2=" ">
                                 <subfield code="a">Mephistopheles</subfield>
                               </datafield>
                               <datafield tag="653" ind1=" " ind2=" ">
                                 <subfield code="a">Mephisto</subfield>
                               </datafield>
                               <datafield tag="653" ind1=" " ind2=" ">
                                 <subfield code="a">(VLB-WN)1111: Hardcover, Softcover / Belletristik/Hauptwerk vor 1945</subfield>
                               </datafield>
                               <datafield tag="655" ind1=" " ind2="7">
                                 <subfield code="0">(DE-101)1010836293</subfield>
                                 <subfield code="a">Erzählende Literatur: Hauptwerk vor 1945</subfield>
                                 <subfield code="2">gatbeg</subfield>
                               </datafield>
                               <datafield tag="856" ind1="4" ind2="2">
                                 <subfield code="m">X:MVB</subfield>
                                 <subfield code="q">text/html</subfield>
                                 <subfield code="u">https://services.dnb.de/plus/idn/125686871X/blurb/</subfield>
                                 <subfield code="3">Inhaltstext</subfield>
                               </datafield>
                               <datafield tag="883" ind1="1" ind2=" ">
                                 <subfield code="8">1\p</subfield>
                                 <subfield code="a">vlb</subfield>
                                 <subfield code="d">20220505</subfield>
                                 <subfield code="q">DE-101</subfield>
                                 <subfield code="u">https://d-nb.info/provenance/plan#vlb</subfield>
                               </datafield>
                               <datafield tag="883" ind1="1" ind2=" ">
                                 <subfield code="8">2\p</subfield>
                                 <subfield code="a">vlb</subfield>
                                 <subfield code="d">20220505</subfield>
                                 <subfield code="q">DE-101</subfield>
                                 <subfield code="u">https://d-nb.info/provenance/plan#vlb</subfield>
                               </datafield>
                               <datafield tag="925" ind1="w" ind2=" ">
                                 <subfield code="a">c</subfield>
                               </datafield>
                               <datafield tag="926" ind1="1" ind2=" ">
                                 <subfield code="a">FBC</subfield>
                                 <subfield code="o">93</subfield>
                                 <subfield code="q">Publisher</subfield>
                                 <subfield code="v">1.6</subfield>
                                 <subfield code="x">Klassische Belletristik: allgemein und literarisch</subfield>
                               </datafield>
                             </record></recordData><recordPosition>1</recordPosition></record></records><echoedSearchRetrieveRequest><version>1.1</version><query>idn=125686871X</query><xQuery xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:nil="true"/><recordSchema>MARC21-xml</recordSchema></echoedSearchRetrieveRequest></searchRetrieveResponse>
                           """;
        Harness harness = await Harness.LookupAsync("metadata.dnb", BuiltInIdentifierSchemes.Dnb,
            "125686871X", _ => XmlResponse(xml));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("Faust I, II und Urfaust");
        harness.Result.Value.Publisher.Should().Be("Petersberg Verlag");
        // The live record stores the o-umlaut decomposed (o + U+0308); the parser returns
        // field text verbatim, so the expectation uses the same decomposed form.
        harness.Result.Value.Place.Should().Be("Königswinter");
        harness.Result.Value.Dates.Should().Contain(date => date.Literal == "2050");
    }

    // Live: GET https://catalogue.bnf.fr/api/SRU?...query=bib.ark all "ark:/12148/cb434806702"
    // &recordSchema=intermarcXchange (HTTP 200, 1 record). INTERMARC 260: $a place,
    // $c PUBLISHER, $d date; creators split name $a / given name $m.
    [Fact]
    public async Task Verify_bnf_fresh_ark_uses_intermarc_semantics()
    {
        const string xml = """
                           <?xml version="1.0" encoding="UTF-8"?><srw:searchRetrieveResponse xmlns:srw="http://www.loc.gov/zing/srw/" xmlns="http://catalogue.bnf.fr/namespaces/InterXMarc" xmlns:ixm="http://catalogue.bnf.fr/namespaces/InterXMarc" xmlns:mn="http://catalogue.bnf.fr/namespaces/motsnotices" xmlns:sd="http://www.loc.gov/zing/srw/diagnostic/">
                           <srw:version>1.2</srw:version>
                           <srw:echoedSearchRetrieveRequest>
                           <srw:version>1.2</srw:version>
                           <srw:query>bib.ark all "ark:/12148/cb434806702"</srw:query>
                           </srw:echoedSearchRetrieveRequest>
                           <srw:numberOfRecords>1</srw:numberOfRecords>
                           <srw:records>
                           <srw:record>
                           <srw:recordSchema>marcxchange</srw:recordSchema>
                           <srw:recordPacking>xml</srw:recordPacking>
                           <srw:recordData>
                           <mxc:record xmlns:mxc="info:lc/xmlns/marcxchange-v2" format="INTERMARC" id="ark:/12148/cb434806702" type="Bibliographic">   <mxc:leader>01060n5 m 2200027   45a </mxc:leader>   <mxc:controlfield tag="001">FRBNF434806700000007</mxc:controlfield>   <mxc:controlfield tag="003">http://catalogue.bnf.fr/ark:/12148/cb434806702</mxc:controlfield>   <mxc:controlfield tag="008">110811s 2011                 frfre     b  001 </mxc:controlfield>   <mxc:controlfield tag="009">a nd   z           </mxc:controlfield>   <mxc:datafield tag="020" ind1=" " ind2=" ">      <mxc:subfield code="a">978-2-917456-21-7</mxc:subfield>      <mxc:subfield code="b">Cart.</mxc:subfield>      <mxc:subfield code="d">15.00 EUR</mxc:subfield>   </mxc:datafield>   <mxc:datafield tag="038" ind1=" " ind2=" ">      <mxc:subfield code="a">9782917456217</mxc:subfield>   </mxc:datafield>   <mxc:datafield tag="044" ind1=" " ind2=" ">      <mxc:subfield code="c">d20111012</mxc:subfield>   </mxc:datafield>   <mxc:datafield tag="051" ind1=" " ind2=" ">      <mxc:subfield code="a">txt</mxc:subfield>      <mxc:subfield code="b">n</mxc:subfield>   </mxc:datafield>   <mxc:datafield tag="100" ind1=" " ind2=" ">      <mxc:subfield code="3">11919671</mxc:subfield>      <mxc:subfield code="1">ISNI0000000119991607</mxc:subfield>      <mxc:subfield code="w">.0..b.....</mxc:subfield>      <mxc:subfield code="a">Pichard</mxc:subfield>      <mxc:subfield code="m">Georges</mxc:subfield>      <mxc:subfield code="d">1920-2003</mxc:subfield>      <mxc:subfield code="4">0070</mxc:subfield>   </mxc:datafield>   <mxc:datafield tag="245" ind1="1" ind2=" ">      <mxc:subfield code="a">Germinal</mxc:subfield>      <mxc:subfield code="d">Texte imprimé</mxc:subfield>      <mxc:subfield code="h">2</mxc:subfield>      <mxc:subfield code="f">Georges Pichard</mxc:subfield>      <mxc:subfield code="g">d'après le roman d'Emile Zola</mxc:subfield>   </mxc:datafield>   <mxc:datafield tag="260" ind1=" " ind2=" ">      <mxc:subfield code="a">Ivry-sur-Seine (Val-de-Marne)</mxc:subfield>      <mxc:subfield code="c">R. Rils</mxc:subfield>      <mxc:subfield code="d">2011</mxc:subfield>   </mxc:datafield>   <mxc:datafield tag="280" ind1=" " ind2=" ">      <mxc:subfield code="a">1 vol. (42 p.)</mxc:subfield>      <mxc:subfield code="c">illustrations en noir et blanc</mxc:subfield>      <mxc:subfield code="d">30 x 21 cm</mxc:subfield>   </mxc:datafield>   <mxc:datafield tag="460" ind1=" " ind2=" ">      <mxc:subfield code="3">34344081</mxc:subfield>      <mxc:subfield code="t">Germinal</mxc:subfield>      <mxc:subfield code="v">2</mxc:subfield>   </mxc:datafield>   <mxc:datafield tag="689" ind1=" " ind2=" ">      <mxc:subfield code="q">Bandes dessinées - Bandes dessinées - Érotique</mxc:subfield>      <mxc:subfield code="2">Electre</mxc:subfield>   </mxc:datafield>   <mxc:datafield tag="700" ind1=" " ind2=" ">      <mxc:subfield code="3">11929720</mxc:subfield>      <mxc:subfield code="1">ISNI0000000120958660</mxc:subfield>      <mxc:subfield code="w">.0..b.....</mxc:subfield>      <mxc:subfield code="a">Zola</mxc:subfield>      <mxc:subfield code="m">Émile</mxc:subfield>      <mxc:subfield code="d">1840-1902</mxc:subfield>      <mxc:subfield code="4">0072</mxc:subfield>   </mxc:datafield>   <mxc:datafield tag="830" ind1=" " ind2=" ">      <mxc:subfield code="a">Etienne Lantier est embauché dans la mine de Voreux après avoir été renvoyé des chemins de fer pour son activité syndicale. Il loge chez les Maheu, une famille de mineurs. Le travail est rude, mal payé, et les conditions de sécurité ne sont pas respectées : la grève commence. Adaptation en bande dessinée du roman d'Emile Zola. BD érotique.</mxc:subfield>      <mxc:subfield code="2">Electre</mxc:subfield>   </mxc:datafield>   <mxc:datafield tag="917" ind1=" " ind2=" ">      <mxc:subfield code="o">ALS</mxc:subfield>      <mxc:subfield code="a">1760946</mxc:subfield>   </mxc:datafield>   <mxc:datafield tag="919" ind1=" " ind2=" ">      <mxc:subfield code="p">FR</mxc:subfield>      <mxc:subfield code="o">ELECTRE</mxc:subfield>      <mxc:subfield code="a">ELC1253901</mxc:subfield>   </mxc:datafield>   <mxc:datafield tag="950" ind1=" " ind2=" ">      <mxc:subfield code="a">167156</mxc:subfield>      <mxc:subfield code="b">C1</mxc:subfield>   </mxc:datafield>
                           </mxc:record>      </srw:recordData>
                           <srw:recordIdentifier>ark:/12148/cb434806702</srw:recordIdentifier>
                           <srw:recordPosition>1</srw:recordPosition>
                           <srw:extraRecordData>
                           <ixm:attr name="CreationDate">20140821</ixm:attr>
                           <ixm:attr name="LastModificationDate">20191015</ixm:attr>
                           <mn:score>7.378913</mn:score>
                           </srw:extraRecordData>
                           </srw:record>
                           </srw:records>
                           </srw:searchRetrieveResponse>
                           """;
        Harness harness = await Harness.LookupAsync("metadata.bnf", BuiltInIdentifierSchemes.Ark,
            "12148/cb434806702", _ => XmlResponse(xml));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("Germinal");
        harness.Result.Value.Publisher.Should().Be("R. Rils");
        harness.Result.Value.Place.Should().Be("Ivry-sur-Seine (Val-de-Marne)");
        harness.Result.Value.Dates.Should().Contain(date => date.Literal == "2011");
        harness.Result.Value.Creators.Should().Contain(creator => creator.Literal == "Pichard, Georges");
        harness.Result.Value.Creators.Should().Contain(creator => creator.Literal == "Zola, Émile");
        harness.Requests[0].Uri.AbsoluteUri.Should()
            .Contain("bib.ark").And.Contain("ark%3A%2F12148%2Fcb434806702");
    }

    // Live: GET http://opac.nlc.cn/F?func=find-b&find_code=ISB&request=9787536692930
    // &local_base=NLC01 (HTTP 200, 三体 detail page served directly). The record table id is
    // quoted while every data cell uses UNQUOTED class=td1 attributes (39 on the live page).
    [Fact]
    public async Task Verify_nlc_fresh_isbn_parses_unquoted_cells()
    {
        const string html = """
                            <html><body><pre>lt='Use SFX services'></a> 
                             
                             FMT:   
                             ISBN: 978-7-5366-9293-0 CNY23.00 </pre><table id="td" width="90%" style="margin:0 auto;" border="0" cellspacing="2"> 
                             
                            <script> 
                            var reg=new RegExp("&tag=(.*)","i"); 
                            if(reg.test(location.href)){ 
                                  var arr=reg.exec(location.href); 
                                  g('cclterm').value=decodeURI(arr[1]); 
                                  g('find_code').options[g('find_code').length-1].selected=true; 
                            }else{ 
                                  g('cclterm').value=parseccl('ISB = "9787536692930"'); 
                            } 
                            </script> 

                            <!-- filename: full-999-body --> 
                             <tr> 
                              <td style="background:#fff;" class=td1 
                                  id=bold 
                                  width=15% 
                                  valign=center 
                                  nowrap> 
                                  头标区 
                              </td> 
                              <td style="background:#fff;" class=td1 align=left > 
                                 -----nam0&nbsp;22-----&nbsp;450&nbsp; 
                              </td> 
                             </tr> 
                             

                            <!-- filename: full-999-body --> 
                             <tr> 
                              <td style="background:#fff;" class=td1 
                                  id=bold 
                                  width=15% 
                                  valign=center 
                                  nowrap> 
                                  ID 号 
                              </td> 
                              <td style="background:#fff;" class=td1 align=left > 
                                 004118608 
                              </td> 
                             </tr> 
                             

                            <!-- filename: full-999-body --> 
                             <tr> 
                              <td style="background:#fff;" class=td1 
                                  id=bold 
                                  width=15% 
                                  valign=center 
                                  nowrap> 
                                  通用数据 
                              </td> 
                              <td style="background:#fff;" class=td1 align=left > 
                                 20090414d2008&nbsp;em&nbsp;y0chiy50&nbsp;ea 
                              </td> 
                             </tr> 
                             

                            <!-- filename: full-999-body --> 
                             <tr> 
                              <td style="background:#fff;" class=td1 
                                  id=bold 
                                  width=15% 
                                  valign=center 
                                  nowrap> 
                                  题名与责任 
                              </td> 
                              <td style="background:#fff;" class=td1 align=left > 
                                 <img src=http://opac.nlc.cn:80/exlibris/aleph/u20_1/alephe/www_f_chi/icon/f-separator.gif alt='Link' border=0 alt=''><A HREF='javascript:open_window("http://opac.nlc.cn:80/F/UFIB6P58VFEGUYHXS1BHSI8S85R42QBF4P6PI17UIS21B17UUK-23961?func=service&doc_number=004118608&line_number=0011&service_type=TAG");'>三体&nbsp;[专著]&nbsp;/&nbsp;刘慈欣著</A> 
                              </td> 
                             </tr> 
                             

                            <!-- filename: full-999-body --> 
                             <tr> 
                              <td style="background:#fff;" class=td1 
                                  id=bold 
                                  width=15% 
                                  valign=center 
                                  nowrap> 
                                  出版项 
                              </td> 
                              <td style="background:#fff;" class=td1 align=left > 
                                 <img src=http://opac.nlc.cn:80/exlibris/aleph/u20_1/alephe/www_f_chi/icon/f-separator.gif alt='Link' border=0 alt=''><A HREF='javascript:open_window("http://opac.nlc.cn:80/F/UFIB6P58VFEGUYHXS1BHSI8S85R42QBF4P6PI17UIS21B17UUK-23962?func=service&doc_number=004118608&line_number=0012&service_type=TAG");'>重庆&nbsp;:&nbsp;重庆出版社,&nbsp;2008</A> 
                              </td> 
                             </tr> 
                             

                            <!-- filename: full-999-body --> 
                             <tr> 
                              <td style="background:#fff;" class=td1 
                                  id=bold 
                                  width=15% 
                                  valign=center 
                                  nowrap> 
                                  载体形态项 
                              </td> 
                              <td style="background:#fff;" class=td1 align=left > 
                                 302页&nbsp;;&nbsp;21cm 
                              </td> 
                             </tr> 
                             

                            <!-- filename: full-999-body --> 
                             <tr> 
                              <td style="background:#fff;" class=td1 
                                  id=bold 
                                  width=15% 
                                  valign=center 
                                  nowrap> 
                                  丛编项 
                              </td> 
                              <td style="background:#fff;" class=td1 align=left > 
                                 <img src=http://opac.nlc.cn:80/exlibris/aleph/u20_1/alephe/www_f_chi/icon/f-separator.gif alt='Link' border=0 alt=''><A HREF='javascript:open_window("http://opac.nlc.cn:80/F/UFIB6P58VFEGUYHXS1BHSI8S85R42QBF4P6PI17UIS21B17UUK-23963?func=service&doc_number=004118608&line_number=0014&service_type=TAG");'>中国科幻基石丛书</A> 
                              </td> 
                             </tr> 
                             

                            <!-- filename: full-999-body --> 
                             <tr> 
                              <td style="background:#fff;" class=td1 
                                  id=bold 
                                  width=15% 
                                  valign=center 
                                  nowrap> 
                                  语言 
                              </td> 
                              <td style="background:#fff;" class=td1 align=left > 
                                 chi 
                              </td> 
                             </tr> 
                             

                            <!-- filename: full-999-body --> 
                             <tr> 
                              <td style="background:#fff;" class=td1 
                                  id=bold 
                                  width=15% 
                                  valign=center 
                                  nowrap> 
                                  一般附注 
                              </td> 
                              <td style="background:#fff;" class=td1 align=left > 
                                 “地球往事”三部曲之一 
                              </td> 
                             </tr> 
                             

                            <!-- filename: full-999-body --> 
                             <tr> 
                              <td style="background:#fff;" class=td1 
                                  id=bold 
                                  width=15% 
                                  valign=center 
                                  nowrap> 
                                  题名 
                              </td> 
                              <td style="background:#fff;" class=td1 align=left > 
                                 <img src=http://opac.nlc.cn:80/exlibris/aleph/u20_1/alephe/www_f_chi/icon/f-separator.gif alt='Link' border=0 alt=''><A HREF='javascript:open_window("http://opac.nlc.cn:80/F/UFIB6P58VFEGUYHXS1BHSI8S85R42QBF4P6PI17UIS21B17UUK-23964?func=service&doc_number=004118608&line_number=0016&service_type=TAG");'>三体&nbsp;san ti</A> 
                              </td> 
                             </tr> 
                             

                            <!-- filename: full-999-body --> 
                             <tr> 
                              <td style="background:#fff;" class=td1 
                                  id=bold 
                                  width=15% 
                                  valign=center 
                                  nowrap> 
                                  主题 
                              </td> 
                              <td style="background:#fff;" class=td1 align=left > 
                                 <img src=http://opac.nlc.cn:80/exlibris/aleph/u20_1/alephe/www_f_chi/icon/f-separator.gif alt='Link' border=0 alt=''><A HREF='javascript:open_window("http://opac.nlc.cn:80/F/UFIB6P58VFEGUYHXS1BHSI8S85R42QBF4P6PI17UIS21B17UUK-23965?func=service&doc_number=004118608&line_number=0017&service_type=TAG");'>幻想小说&nbsp;--&nbsp;中国&nbsp;--&nbsp;现代</A> 
                              </td> 
                             </tr> 
                             

                            <!-- filename: full-999-body --> 
                             <tr> 
                              <td style="background:#fff;" class=td1 
                                  id=bold 
                                  width=15% 
                                  valign=center 
                                  nowrap> 
                                  中图分类号 
                              </td> 
                              <td style="background:#fff;" class=td1 align=left > 
                                 <img src=http://opac.nlc.cn:80/exlibris/aleph/u20_1/alephe/www_f_chi/icon/f-separator.gif alt='Link' border=0 alt=''><A HREF='javascript:open_window("http://opac.nlc.cn:80/F/UFIB6P58VFEGUYHXS1BHSI8S85R42QBF4P6PI17UIS21B17UUK-23966?func=service&doc_number=004118608&line_number=0018&service_type=TAG");'>I247.55</A> 
                              </td> 
                             </tr> 
                             

                            <!-- filename: full-999-body --> 
                             <tr> 
                              <td style="background:#fff;" class=td1 
                                  id=bold 
                                  width=15% 
                                  valign=center 
                                  nowrap> 
                                  著者 
                              </td> 
                              <td style="background:#fff;" class=td1 align=left > 
                                 <img src=http://opac.nlc.cn:80/exlibris/aleph/u20_1/alephe/www_f_chi/icon/f-separator.gif alt='Link' border=0 alt=''><A HREF='javascript:open_window("http://opac.nlc.cn:80/F/UFIB6P58VFEGUYHXS1BHSI8S85R42QBF4P6PI17UIS21B17UUK-23967?func=service&doc_number=004118608&line_number=0019&service_type=TAG");'>刘慈欣&nbsp;(1963~)&nbsp;著</A> 
                              </td> 
                             </tr> 
                             

                            <!-- filename: full-999-body --> 
                             <tr> 
                              <td style="background:#fff;" class=td1 
                                  id=bold 
                                  width=15% 
                                  valign=center 
                                  nowrap> 
                                   
                              </td> 
                              <td style="background:#fff;" class=td1 align=left > 
                                 <br> 
                              </td> 
                             </tr> 
                             

                            <!-- filename: full-999-body --> 
                             <tr> 
                              <td style="background:#fff;" class=td1 
                                  id=bold 
                                  width=15% 
                                  valign=center 
                                  nowrap> 
                                  所有单册 
                              </td> 
                              <td style="background:#fff;" class=td1 align=left > 
                                 <A HREF=http://opac.nlc.cn:80/F/UFIB6P58VFEGUYHXS1BHSI8S85R42QBF4P6PI17UIS21B17UUK-23968?func=item-global&doc_library=NLC01&doc_number=004118608&year=&volume=&sub_library= >查看所有馆藏单册信息</A> 
                              </td> 
                             </tr> 
                             

                            <!-- filename: full-999-body --> 
                             <tr> 
                              <td style="background:#fff;" class=td1 
                                  id=bold 
                                  width=15% 
                                  valign=center 
                                  nowrap> 
                                  馆藏 
                              </td> 
                              <td style="background:#fff;" class=td1 align=left > 
                                 <A HREF=http://opac.nlc.cn:80/F/UFIB6P58VFEGUYHXS1BHSI8S85R42QBF4P6PI17UIS21B17UUK-23969?func=item-global&doc_library=NLC01&doc_number=004118608&year=&volume=&sub_library=ZWJC> 中文基藏</A><A HREF='javascript:open_window("http://opac.nlc.cn:80/F/UFIB6P58VFEGUYHXS1BHSI8S85R42QBF4P6PI17UIS21B17UUK-23970?func=library&sub_library=ZWJC");'><img src=http://opac.nlc.cn:80/exlibris/aleph/u20_1/alephe/www_f_chi/icon/f-info.gif border=0 alt='Library Info'></A> 
                              </td> 
                             </tr> 
                             

                            <!-- filename: full-999-body --> 
                             <tr> 
                              <td style="background:#fff;" class=td1 
                                  id=bold 
                                  width=15% 
                                  valign=center 
                                  nowrap> 
                                  馆藏 
                              </td> 
                              <td style="background:#fff;" class=td1 align=left > 
                                 <A HREF=http://opac.nlc.cn:80/F/UFIB6P58VFEGUYHXS1BHSI8S85R42QBF4P6PI17UIS21B17UUK-23971?func=item-global&doc_library=NLC01&doc_number=004118608&year=&volume=&sub_library=SKBC> 书刊保存本库</A><A HREF='javascript:open_window("http://opac.nlc.cn:80/F/UFIB6P58VFEGUYHXS1BHSI8S85R42QBF4P6PI17UIS21B17UUK-23972?func=library&sub_library=SKBC");'><img src=http://opac.nlc.cn:80/exlibris/aleph/u20_1/alephe/www_f_chi/icon/f-info.gif border=0 alt='Library Info'></A> 
                              </td> 
                             </tr> 
                             

                            <!-- filename: full-999-body --> 
                             <tr> 
                              <td style="background:#fff;" class=td1 
                                  id=bold 
                                  width=15% 
                                  valign=center 
                                  nowrap> 
                                  馆藏 
                              </td> 
                              <td style="background:#fff;" class=td1 align=left > 
                                 <A HREF=http://opac.nlc.cn:80/F/UFIB6P58VFEGUYHXS1BHSI8S85R42QBF4P6PI17UIS21B17UUK-23973?func=item-global&doc_library=NLC01&doc_number=004118608&year=&volume=&sub_library=BWFG> 部委分馆</A><A HREF='javascript:open_window("http://opac.nlc.cn:80/F/UFIB6P58VFEGUYHXS1BHSI8S85R42QBF4P6PI17UIS21B17UUK-23974?func=library&sub_library=BWFG");'><img src=http://opac.nlc.cn:80/exlibris/aleph/u20_1/alephe/www_f_chi/icon/f-info.gif border=0 alt='Library Info'></A> 
                              </td> 
                             </tr> 
                             

                            <!-- filename: full-set-tail --> 
                            <!-- publish section 
                             FMT:   
                             ISBN: 978-7-5366-9293-0 CNY23.00 
                             TITLE: 三体 san ti 专著 刘慈欣著 
                             AUTHOR: 刘慈欣 liu ci xin (1963~) 著 
                             IMPRINT: 重庆出版社 
                             CALL-NO: I247.55 
                             
                             DOC-NUMBER: 004118608 
                             FIND-BASE: NLC01 
                             
                             BOR-ID: ALEPH 
                             BRO-NAME:  
                             GOOGLE: http://books.google.com/books?jscmd=viewapi&bibkeys=ISBN:978-7-5366-9293-0&callback=ProcessGBSBookInfo 
                            --> 
                            <!-- 
                            <tr> 
                            <td class=td1><td class=td1>&nbsp; &nbsp; &nbsp; 
                            --> 
                            <script>if(flag_tag!=0)w("<tr><td class=td1 id=bold width=15% valign=top nowrap>&nbsp;&nbsp; &nbsp;标签:&nbsp;<td class=td1><div id=alltags><\/div>");</script> 
                            </table></body></html>
                            """;
        Harness harness = await Harness.LookupAsync("metadata.nlc", BuiltInIdentifierSchemes.ISBN,
            "9787536692930", _ => HtmlResponse(html));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("三体");
        harness.Result.Value.Publisher.Should().Be("重庆出版社");
        harness.Result.Value.Dates.Should().ContainSingle(date =>
            date.DateParts!.SequenceEqual(new[] { 2008 }));
        harness.Result.Value.Creators.Should().Contain(creator =>
            creator.Literal!.Contains("刘慈欣"));
        harness.Result.Value.Tags.Should().Contain("I247.55");
        harness.Requests.Should().ContainSingle();
    }

    // Two-hop path: when the ISBN search returns a result LIST instead of the record itself,
    // the extension follows the first itemtitle link (Support.nlcFirstDetailUrl) to the detail
    // page. No real list page is archived in the metadata audit, so the list page below is a
    // SYNTHETIC sample built in the same unquoted-attribute style as the archived real detail
    // page above (<div class=itemtitle>, <a href=...> with no quotes anywhere); the detail page
    // is likewise a minimal synthetic page in that style.
    [Fact]
    public async Task Verify_nlc_list_page_with_unquoted_itemtitle_is_followed()
    {
        const string listHtml = """
                                <html><body>
                                <table width="90%" border="0" cellspacing="2">
                                <tr><td class=td2><div class=itemtitle><a href=http://opac.nlc.cn/F?func=full-set-set&set_number=012345&set_entry=000001&format=999>三体</a></div></td></tr>
                                </table>
                                </body></html>
                                """;
        const string detailHtml = """
                                  <html><body><table id="td" width="90%" style="margin:0 auto;" border="0" cellspacing="2">
                                  <tr>
                                    <td style="background:#fff;" class=td1>题名与责任</td>
                                    <td style="background:#fff;" class=td1>三体</td>
                                  </tr>
                                  <tr>
                                    <td style="background:#fff;" class=td1>著者</td>
                                    <td style="background:#fff;" class=td1>刘慈欣著</td>
                                  </tr>
                                  <tr>
                                    <td style="background:#fff;" class=td1>出版项</td>
                                    <td style="background:#fff;" class=td1>重庆 : 重庆出版社, 2008</td>
                                  </tr>
                                  <tr>
                                    <td style="background:#fff;" class=td1>ISBN</td>
                                    <td style="background:#fff;" class=td1>978-7-5366-9293-0</td>
                                  </tr>
                                  </table></body></html>
                                  """;
        Harness harness = await Harness.LookupAsync("metadata.nlc", BuiltInIdentifierSchemes.ISBN,
            "9787536692930", request => request.Uri.AbsoluteUri.Contains("func=full-set")
                ? HtmlResponse(detailHtml)
                : HtmlResponse(listHtml));

        harness.Result.IsSuccess.Should().BeTrue(harness.Result.ErrorMessage);
        harness.Result.Value.Title.Should().Be("三体");
        harness.Result.Value.Publisher.Should().Be("重庆出版社");
        harness.Result.Value.Dates.Should().ContainSingle(date =>
            date.DateParts!.SequenceEqual(new[] { 2008 }));
        harness.Requests.Should().HaveCount(2);
        harness.Requests[1].Uri.AbsoluteUri.Should().Contain("func=full-set-set");
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

    // Same replay harness shape as BuiltInMetadataSourceTests (which keeps its own private
    // copy): activates the real compiled built-in package and records wire requests.
    private sealed record Harness(Result<MetadataCandidate> Result, IReadOnlyList<RecordedHttpRequest> Requests)
    {
        public static async Task<Harness> LookupAsync(string extensionId, string scheme, string value,
            Func<RecordedHttpRequest, TestHttpResponse> respond,
            IReadOnlyDictionary<string, string>? config = null)
        {
            string directory = Path.Combine(Path.GetTempPath(), $"patchouli-metadata-verify-{Guid.NewGuid():N}");
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
