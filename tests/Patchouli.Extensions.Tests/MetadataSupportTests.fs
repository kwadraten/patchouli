namespace Patchouli.Extensions.Tests

open System.Xml.Linq
open Patchouli.Core.Bibliography.MetadataLookup
open BuiltInMetadata.Support
open Xunit

[<Collection(TestEnvironment.CollectionName)>]
type MetadataSupportTests() =
    [<Fact>]
    member _.``production Dublin Core parser matches the requested identifier`` () =
        let document = XDocument.Parse("""<rss><channel><item><title>Catalog title</title><creator>A. Author</creator><identifier xsi:type="ISBN" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">9780306406157</identifier></item></channel></rss>""")
        let requested = NormalizedIdentifier("isbn", "9780306406157")
        let candidate = parseDublinCore document requested
        Assert.True(candidate.IsSome)
        Assert.Equal("Catalog title", (Option.get candidate).Title)

    [<Fact>]
    member _.``production PubMed parser extracts article and identifier`` () =
        let document = XDocument.Parse("""<PubmedArticle><MedlineCitation><Article><ArticleTitle>Research title</ArticleTitle><Journal><JournalIssue><PubDate><Year>2024</Year></PubDate></JournalIssue><Title>Journal</Title></Journal></Article></MedlineCitation><PubmedData><ArticleIdList><ArticleId IdType="pubmed">12345</ArticleId></ArticleIdList></PubmedData></PubmedArticle>""")
        let candidate = parsePubMed document
        Assert.True(candidate.IsSome)
        let value = Option.get candidate
        Assert.Equal("Research title", value.Title)
        let identifiers = value.Identifiers |> Option.ofObj |> Option.defaultWith (fun () -> failwith "Missing identifiers")
        Assert.True(identifiers |> Seq.exists (fun (identifier: MetadataIdentifier) -> identifier.Scheme = "pmid" && identifier.Value = "12345"))

    [<Fact>]
    member _.``production MARC parser maps title and ISBN`` () =
        let document = XDocument.Parse("""<collection><record><datafield tag="245"><subfield code="a">MARC title /</subfield></datafield><datafield tag="020"><subfield code="a">9784000000001</subfield></datafield></record></collection>""")
        let candidate = parseMarc document
        Assert.True(candidate.IsSome)
        let value = Option.get candidate
        Assert.Equal("MARC title", value.Title)
        Assert.Equal("book", value.SuggestedItemType)

    [<Fact>]
    member _.``production HTML parser requires identifier and extracts title`` () =
        let html = """<table id="td"><tr><td class="td1">ISBN</td><td class="td1">9784000000001</td></tr><tr><td class="td1">题名与责任</td><td class="td1">HTML title</td></tr></table>"""
        let candidate = parseNlcHtml html (NormalizedIdentifier("isbn", "9784000000001"))
        Assert.True(candidate.IsSome)
        Assert.Equal("HTML title", (Option.get candidate).Title)
        Assert.True((parseNlcHtml html (NormalizedIdentifier("isbn", "9784000000002"))).IsNone)
