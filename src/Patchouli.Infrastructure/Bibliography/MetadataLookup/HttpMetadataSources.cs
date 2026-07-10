using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Bibliography.MetadataLookup;

public static class PublicMetadataSources
{
    public static IReadOnlyList<IMetadataSource> Create(HttpClient httpClient, TimeSpan? requestTimeout = null)
        =>
        [
            new CrossrefMetadataSource(httpClient, requestTimeout),
            new DataCiteMetadataSource(httpClient, requestTimeout),
            new OpenAlexMetadataSource(httpClient, requestTimeout),
            new PubMedMetadataSource(httpClient, requestTimeout),
            new PmcIdConverterMetadataSource(httpClient, requestTimeout),
            new SemanticScholarMetadataSource(httpClient, requestTimeout),
            new ArXivMetadataSource(httpClient, requestTimeout),
            new OpenLibraryMetadataSource(httpClient, requestTimeout),
            new GoogleBooksMetadataSource(httpClient, requestTimeout),
            new CalisMetadataSource(httpClient, requestTimeout),
            new NationalLibraryOfChinaMetadataSource(httpClient, requestTimeout),
            new NdlMetadataSource(httpClient, requestTimeout),
            new CiniiMetadataSource(httpClient, requestTimeout),
            new LibraryOfCongressMetadataSource(httpClient, requestTimeout),
            new DnbMetadataSource(httpClient, requestTimeout),
            new BnfMetadataSource(httpClient, requestTimeout)
        ];
}

public abstract class HttpMetadataSource : IMetadataSource
{
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _requestTimeout;

    protected HttpMetadataSource(HttpClient httpClient, MetadataSourceDefinition definition, TimeSpan? requestTimeout)
    {
        _httpClient = httpClient;
        Definition = definition;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(15);
    }

    public MetadataSourceDefinition Definition { get; }

    public async Task<Result<MetadataCandidate>> LookupAsync(
        NormalizedIdentifier identifier,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        try
        {
            using var request = CreateRequest(identifier);
            request.Headers.Accept.ParseAdd(Accept);
            request.Headers.UserAgent.ParseAdd("Patchouli/1.0 (bibliographic metadata lookup)");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
                return Failure(MetadataLookupErrorCodes.NotFound, "The source did not find a matching record.");
            if ((int)response.StatusCode == 429)
            {
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                if (body.Contains("quota_limit_value\": \"0", StringComparison.OrdinalIgnoreCase)
                    || body.Contains("Queries per day", StringComparison.OrdinalIgnoreCase))
                {
                    return Failure(
                        MetadataLookupErrorCodes.QuotaUnavailable,
                        $"{Definition.DisplayName} anonymous API quota is unavailable for this client; retrying will not help until the provider quota changes.");
                }

                var retryAfter = response.Headers.RetryAfter?.Delta is { } delay
                    ? $" Retry after {Math.Ceiling(delay.TotalSeconds)} seconds."
                    : string.Empty;
                return Failure(MetadataLookupErrorCodes.RateLimited, $"{Definition.DisplayName} rate limit was reached.{retryAfter}");
            }
            if (!response.IsSuccessStatusCode)
                return Failure(MetadataLookupErrorCodes.ProviderUnavailable, $"The metadata source returned HTTP {(int)response.StatusCode}.");

            await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024, timeout.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var parsed = await ParseAsync(stream, identifier, timeout.Token);
            if (parsed is null)
                return Failure(MetadataLookupErrorCodes.NotFound, "The source did not find a matching record.");
            return string.IsNullOrWhiteSpace(parsed.Title) && (parsed.Identifiers?.Count ?? 0) == 0
                ? Failure(MetadataLookupErrorCodes.InvalidResponse, "The metadata source response did not contain metadata or identifiers.")
                : Result<MetadataCandidate>.Success(parsed with { SourceId = Definition.Id });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(MetadataLookupErrorCodes.Timeout, "The metadata source request timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception) when ((int?)exception.StatusCode == 429)
        {
            return Failure(MetadataLookupErrorCodes.RateLimited, $"{Definition.DisplayName} rate limit was reached.");
        }
        catch (HttpRequestException exception)
        {
            return Failure(MetadataLookupErrorCodes.ProviderUnavailable, $"The metadata source request failed: {exception.Message}");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or System.Xml.XmlException)
        {
            return Failure(MetadataLookupErrorCodes.InvalidResponse, $"The metadata source response was invalid: {exception.Message}");
        }
    }

    protected virtual string Accept => "application/json";
    protected abstract HttpRequestMessage CreateRequest(NormalizedIdentifier identifier);
    protected abstract Task<MetadataCandidate?> ParseAsync(Stream stream, NormalizedIdentifier identifier, CancellationToken cancellationToken);

    protected static HttpRequestMessage Get(string uri) => new(HttpMethod.Get, new Uri(uri, UriKind.Absolute));
    protected static string E(string value) => Uri.EscapeDataString(value);
    private static Result<MetadataCandidate> Failure(string code, string message) => Result<MetadataCandidate>.Failure(code, message);
}

public sealed class CrossrefMetadataSource : HttpMetadataSource
{
    public CrossrefMetadataSource(HttpClient client, TimeSpan? timeout = null)
        : base(client, SourceDefinitions.Def("crossref", "Crossref", 20, BuiltInIdentifierSchemes.DOI), timeout) { }

    protected override HttpRequestMessage CreateRequest(NormalizedIdentifier id) => Get($"https://api.crossref.org/works/{E(id.Value)}");
    protected override async Task<MetadataCandidate?> ParseAsync(Stream stream, NormalizedIdentifier id, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement.GetProperty("message");
        return JsonMetadata.ParseCrossref(root);
    }
}

public sealed class DataCiteMetadataSource : HttpMetadataSource
{
    public DataCiteMetadataSource(HttpClient client, TimeSpan? timeout = null)
        : base(client, SourceDefinitions.Def("datacite", "DataCite", 30, BuiltInIdentifierSchemes.DOI), timeout) { }

    protected override HttpRequestMessage CreateRequest(NormalizedIdentifier id) => Get($"https://api.datacite.org/dois/{E(id.Value)}");
    protected override async Task<MetadataCandidate?> ParseAsync(Stream stream, NormalizedIdentifier id, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return JsonMetadata.ParseDataCite(doc.RootElement.GetProperty("data").GetProperty("attributes"));
    }
}

public sealed class OpenAlexMetadataSource : HttpMetadataSource
{
    private static readonly IReadOnlySet<string> Schemes = SourceDefinitions.Set(BuiltInIdentifierSchemes.DOI, BuiltInIdentifierSchemes.Pmid, BuiltInIdentifierSchemes.Pmcid, BuiltInIdentifierSchemes.Mag, BuiltInIdentifierSchemes.OpenAlex);
    public OpenAlexMetadataSource(HttpClient client, TimeSpan? timeout = null)
        : base(client, new MetadataSourceDefinition("openalex", "OpenAlex", Schemes, 40), timeout) { }

    protected override HttpRequestMessage CreateRequest(NormalizedIdentifier id)
    {
        var value = id.Scheme switch
        {
            BuiltInIdentifierSchemes.DOI => "https://doi.org/" + id.Value,
            BuiltInIdentifierSchemes.Pmid => "pmid:" + id.Value,
            BuiltInIdentifierSchemes.Pmcid => "pmcid:" + id.Value,
            BuiltInIdentifierSchemes.Mag => "mag:" + id.Value,
            _ => id.Value
        };
        return Get($"https://api.openalex.org/works/{E(value)}");
    }

    protected override async Task<MetadataCandidate?> ParseAsync(Stream stream, NormalizedIdentifier id, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return JsonMetadata.ParseOpenAlex(doc.RootElement);
    }
}

public sealed class PubMedMetadataSource : HttpMetadataSource
{
    public PubMedMetadataSource(HttpClient client, TimeSpan? timeout = null)
        : base(client, SourceDefinitions.Def("pubmed", "PubMed", 10, BuiltInIdentifierSchemes.Pmid), timeout) { }

    protected override string Accept => "application/xml";
    protected override HttpRequestMessage CreateRequest(NormalizedIdentifier id) => Get($"https://eutils.ncbi.nlm.nih.gov/entrez/eutils/efetch.fcgi?db=pubmed&retmode=xml&id={E(id.Value)}");
    protected override async Task<MetadataCandidate?> ParseAsync(Stream stream, NormalizedIdentifier id, CancellationToken ct)
        => XmlMetadata.ParsePubMed(await XDocument.LoadAsync(stream, LoadOptions.None, ct));
}

public sealed class PmcIdConverterMetadataSource : HttpMetadataSource
{
    private static readonly IReadOnlySet<string> Schemes = SourceDefinitions.Set(BuiltInIdentifierSchemes.DOI, BuiltInIdentifierSchemes.Pmid, BuiltInIdentifierSchemes.Pmcid, BuiltInIdentifierSchemes.Mid);
    public PmcIdConverterMetadataSource(HttpClient client, TimeSpan? timeout = null)
        : base(client, new MetadataSourceDefinition("pmc-id-converter", "PMC ID Converter", Schemes, 15), timeout) { }

    protected override HttpRequestMessage CreateRequest(NormalizedIdentifier id) => Get($"https://pmc.ncbi.nlm.nih.gov/tools/idconv/api/v1/articles/?format=json&ids={E(id.Value)}");
    protected override async Task<MetadataCandidate?> ParseAsync(Stream stream, NormalizedIdentifier id, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var record = doc.RootElement.GetProperty("records").EnumerateArray().FirstOrDefault();
        if (record.ValueKind != JsonValueKind.Object) return null;
        var identifiers = JsonMetadata.IdentifierFields(record, ("doi", BuiltInIdentifierSchemes.DOI), ("pmid", BuiltInIdentifierSchemes.Pmid), ("pmcid", BuiltInIdentifierSchemes.Pmcid), ("mid", BuiltInIdentifierSchemes.Mid));
        // The converter is an identifier bridge, not a descriptive catalogue; use its optional title when supplied.
        return new MetadataCandidate("", JsonMetadata.String(record, "title"), Identifiers: identifiers);
    }
}

public sealed class SemanticScholarMetadataSource : HttpMetadataSource
{
    private static readonly IReadOnlySet<string> Schemes = SourceDefinitions.Set(BuiltInIdentifierSchemes.SemanticScholar, BuiltInIdentifierSchemes.DOI, BuiltInIdentifierSchemes.ArXiv, BuiltInIdentifierSchemes.Pmid);
    public SemanticScholarMetadataSource(HttpClient client, TimeSpan? timeout = null)
        : base(client, new MetadataSourceDefinition("semantic-scholar", "Semantic Scholar", Schemes, 50), timeout) { }

    protected override HttpRequestMessage CreateRequest(NormalizedIdentifier id)
    {
        var prefix = id.Scheme switch
        {
            BuiltInIdentifierSchemes.DOI => "DOI:",
            BuiltInIdentifierSchemes.ArXiv => "ARXIV:",
            BuiltInIdentifierSchemes.Pmid => "PMID:",
            _ => string.Empty
        };
        return Get($"https://api.semanticscholar.org/graph/v1/paper/{E(prefix + id.Value)}?fields=title,abstract,authors,year,venue,journal,publicationTypes,externalIds");
    }

    protected override async Task<MetadataCandidate?> ParseAsync(Stream stream, NormalizedIdentifier id, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return JsonMetadata.ParseSemanticScholar(doc.RootElement);
    }
}

public sealed class ArXivMetadataSource : HttpMetadataSource
{
    public ArXivMetadataSource(HttpClient client, TimeSpan? timeout = null)
        : base(client, SourceDefinitions.Def("arxiv", "arXiv", 10, BuiltInIdentifierSchemes.ArXiv), timeout) { }

    protected override string Accept => "application/atom+xml";
    protected override HttpRequestMessage CreateRequest(NormalizedIdentifier id) => Get($"https://export.arxiv.org/api/query?id_list={E(id.Value)}");
    protected override async Task<MetadataCandidate?> ParseAsync(Stream stream, NormalizedIdentifier id, CancellationToken ct)
        => XmlMetadata.ParseArXiv(await XDocument.LoadAsync(stream, LoadOptions.None, ct));
}

public sealed class OpenLibraryMetadataSource : HttpMetadataSource
{
    private static readonly IReadOnlySet<string> Schemes = SourceDefinitions.Set(BuiltInIdentifierSchemes.ISBN, BuiltInIdentifierSchemes.Oclc, BuiltInIdentifierSchemes.Lccn, BuiltInIdentifierSchemes.OpenLibrary);
    public OpenLibraryMetadataSource(HttpClient client, TimeSpan? timeout = null)
        : base(client, new MetadataSourceDefinition("open-library", "Open Library", Schemes, 10), timeout) { }

    protected override HttpRequestMessage CreateRequest(NormalizedIdentifier id)
    {
        var prefix = id.Scheme switch { BuiltInIdentifierSchemes.ISBN => "ISBN", BuiltInIdentifierSchemes.Oclc => "OCLC", BuiltInIdentifierSchemes.Lccn => "LCCN", _ => "OLID" };
        return Get($"https://openlibrary.org/api/books?bibkeys={prefix}:{E(id.Value)}&jscmd=data&format=json");
    }

    protected override async Task<MetadataCandidate?> ParseAsync(Stream stream, NormalizedIdentifier id, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var book = doc.RootElement.EnumerateObject().Select(property => property.Value).FirstOrDefault();
        return book.ValueKind == JsonValueKind.Object ? JsonMetadata.ParseOpenLibrary(book) : null;
    }
}

public sealed class GoogleBooksMetadataSource : HttpMetadataSource
{
    private static readonly IReadOnlySet<string> Schemes = SourceDefinitions.Set(BuiltInIdentifierSchemes.ISBN, BuiltInIdentifierSchemes.GoogleBooks);
    public GoogleBooksMetadataSource(HttpClient client, TimeSpan? timeout = null)
        : base(client, new MetadataSourceDefinition("google-books", "Google Books", Schemes, 20), timeout) { }

    protected override HttpRequestMessage CreateRequest(NormalizedIdentifier id) => id.Scheme == BuiltInIdentifierSchemes.GoogleBooks
        ? Get($"https://www.googleapis.com/books/v1/volumes/{E(id.Value)}")
        : Get($"https://www.googleapis.com/books/v1/volumes?q=isbn:{E(id.Value)}&maxResults=1");

    protected override async Task<MetadataCandidate?> ParseAsync(Stream stream, NormalizedIdentifier id, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;
        if (root.TryGetProperty("items", out var items)) root = items.EnumerateArray().FirstOrDefault();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("volumeInfo", out var info)) return null;
        return JsonMetadata.ParseGoogleBooks(info, JsonMetadata.String(root, "id"));
    }
}

public sealed class CalisMetadataSource : IMetadataSource
{
    private const string BaseUrl = "https://opac2.calis.edu.cn/prod-api/opac/codex/ekb/opac/bibliography";
    private readonly HttpClient _client;
    private readonly TimeSpan _timeout;

    public CalisMetadataSource(HttpClient client, TimeSpan? timeout = null)
    {
        _client = client;
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
    }

    public MetadataSourceDefinition Definition { get; } = SourceDefinitions.Def("calis", "CALIS Union Catalog", 1, BuiltInIdentifierSchemes.ISBN);

    public async Task<Result<MetadataCandidate>> LookupAsync(NormalizedIdentifier identifier, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            var cql = $"(bath.isbn=\"{identifier.Value}*\")";
            var body = JsonSerializer.Serialize(new
            {
                offset = 1,
                limit = 10,
                query = Convert.ToBase64String(Encoding.UTF8.GetBytes(cql)),
                sortkey = "relatesort",
                lang = "zh-cn"
            });
            using var searchRequest = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/simple/instances/1.0")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            searchRequest.Headers.UserAgent.ParseAdd("Patchouli/1.0 (bibliographic metadata lookup)");
            using var searchResponse = await _client.SendAsync(searchRequest, timeout.Token);
            if ((int)searchResponse.StatusCode == 429)
                return Failure(MetadataLookupErrorCodes.RateLimited, "CALIS Union Catalog rate limit was reached.");
            if (!searchResponse.IsSuccessStatusCode)
                return Failure(MetadataLookupErrorCodes.ProviderUnavailable, $"CALIS Union Catalog returned HTTP {(int)searchResponse.StatusCode}.");

            using var searchDocument = JsonDocument.Parse(await searchResponse.Content.ReadAsStringAsync(timeout.Token));
            var instances = searchDocument.RootElement.GetProperty("data").GetProperty("instances");
            foreach (var instance in instances.EnumerateArray())
            {
                var rid = JsonMetadata.String(instance, "rid");
                if (string.IsNullOrWhiteSpace(rid)) continue;
                var candidate = await GetMarcAsync(rid, identifier, timeout.Token);
                if (candidate is not null) return Result<MetadataCandidate>.Success(candidate with { SourceId = Definition.Id });
            }

            return Failure(MetadataLookupErrorCodes.NotFound, "CALIS Union Catalog did not find an exact ISBN record.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(MetadataLookupErrorCodes.Timeout, "CALIS Union Catalog request timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception) when ((int?)exception.StatusCode == 429)
        {
            return Failure(MetadataLookupErrorCodes.RateLimited, "CALIS Union Catalog rate limit was reached.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return Failure(MetadataLookupErrorCodes.InvalidResponse, $"CALIS Union Catalog response was invalid: {exception.Message}");
        }
        catch (HttpRequestException exception)
        {
            return Failure(MetadataLookupErrorCodes.ProviderUnavailable, $"CALIS Union Catalog request failed: {exception.Message}");
        }
    }

    private async Task<MetadataCandidate?> GetMarcAsync(string rid, NormalizedIdentifier requested, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync($"{BaseUrl}/marc/{Uri.EscapeDataString(rid)}", cancellationToken);
        if ((int)response.StatusCode == 429) throw new HttpRequestException("rate limited", null, response.StatusCode);
        if (!response.IsSuccessStatusCode) return null;
        using var envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var marcJson = envelope.RootElement.GetProperty("data").GetProperty("MARC").GetString();
        if (string.IsNullOrWhiteSpace(marcJson)) return null;
        using var marc = JsonDocument.Parse(marcJson);
        return CalisMarc.Parse(marc.RootElement, requested, rid);
    }

    private static Result<MetadataCandidate> Failure(string code, string message) => Result<MetadataCandidate>.Failure(code, message);
}

public sealed class NationalLibraryOfChinaMetadataSource : IMetadataSource
{
    private const string BaseUrl = "http://opac.nlc.cn/F";
    private readonly HttpClient _client;
    private readonly TimeSpan _timeout;

    public NationalLibraryOfChinaMetadataSource(HttpClient client, TimeSpan? timeout = null)
    {
        _client = client;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    public MetadataSourceDefinition Definition { get; } = SourceDefinitions.Def("nlc", "National Library of China", 2, BuiltInIdentifierSchemes.ISBN);

    public async Task<Result<MetadataCandidate>> LookupAsync(NormalizedIdentifier identifier, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            var url = $"{BaseUrl}?func=find-b&find_code=ISB&request={Uri.EscapeDataString(identifier.Value)}&local_base=NLC01";
            var html = await GetHtmlAsync(url, timeout.Token);
            if (!NlcHtml.HasMetadataTable(html))
            {
                var detail = NlcHtml.FirstDetailUrl(html);
                if (detail is null) return Failure(MetadataLookupErrorCodes.NotFound, "National Library of China did not find the ISBN.");
                html = await GetHtmlAsync(detail, timeout.Token);
            }

            var candidate = NlcHtml.Parse(html, identifier);
            return candidate is null
                ? Failure(MetadataLookupErrorCodes.NotFound, "National Library of China did not return usable metadata for the ISBN.")
                : Result<MetadataCandidate>.Success(candidate with { SourceId = Definition.Id });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(MetadataLookupErrorCodes.Timeout, "National Library of China request timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception) when ((int?)exception.StatusCode == 429)
        {
            return Failure(MetadataLookupErrorCodes.RateLimited, "National Library of China rate limit was reached.");
        }
        catch (HttpRequestException exception)
        {
            return Failure(MetadataLookupErrorCodes.ProviderUnavailable, $"National Library of China request failed: {exception.Message}");
        }
    }

    private async Task<string> GetHtmlAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 Patchouli/1.0");
        request.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");
        using var response = await _client.SendAsync(request, cancellationToken);
        if ((int)response.StatusCode == 429) throw new HttpRequestException("rate limited", null, response.StatusCode);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static Result<MetadataCandidate> Failure(string code, string message) => Result<MetadataCandidate>.Failure(code, message);
}

public sealed class NdlMetadataSource : HttpMetadataSource
{
    private static readonly IReadOnlySet<string> Schemes = SourceDefinitions.Set(BuiltInIdentifierSchemes.Ndlbibid, BuiltInIdentifierSchemes.Jpno, BuiltInIdentifierSchemes.Ndljp, BuiltInIdentifierSchemes.ISBN);
    public NdlMetadataSource(HttpClient client, TimeSpan? timeout = null)
        : base(client, new MetadataSourceDefinition("ndl", "National Diet Library", Schemes, 5), timeout) { }

    protected override HttpRequestMessage CreateRequest(NormalizedIdentifier id)
    {
        if (id.Scheme == BuiltInIdentifierSchemes.Ndljp)
            return Get($"https://dl.ndl.go.jp/api/iiif/{E(id.Value)}/manifest.json");

        return id.Scheme == BuiltInIdentifierSchemes.ISBN
            ? Get($"https://ndlsearch.ndl.go.jp/api/opensearch?cnt=10&isbn={E(id.Value)}")
            : Get($"https://ndlsearch.ndl.go.jp/api/opensearch?cnt=10&any={E(id.Value)}");
    }

    protected override async Task<MetadataCandidate?> ParseAsync(Stream stream, NormalizedIdentifier id, CancellationToken ct)
    {
        if (id.Scheme != BuiltInIdentifierSchemes.Ndljp)
        {
            var document = await XDocument.LoadAsync(stream, LoadOptions.None, ct);
            return XmlMetadata.ParseDublinCore(document, id);
        }

        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return JsonMetadata.ParseIiif(doc.RootElement, BuiltInIdentifierSchemes.Ndljp, id.Value);
    }
}

public sealed class CiniiMetadataSource : HttpMetadataSource
{
    private static readonly IReadOnlySet<string> Schemes = SourceDefinitions.Set(BuiltInIdentifierSchemes.Ncid, BuiltInIdentifierSchemes.Naid, BuiltInIdentifierSchemes.Crid);
    public CiniiMetadataSource(HttpClient client, TimeSpan? timeout = null)
        : base(client, new MetadataSourceDefinition("cinii", "CiNii Research", Schemes, 5), timeout) { }

    protected override HttpRequestMessage CreateRequest(NormalizedIdentifier id) => id.Scheme switch
    {
        BuiltInIdentifierSchemes.Ncid => Get($"https://ci.nii.ac.jp/ncid/{E(id.Value)}.json"),
        BuiltInIdentifierSchemes.Naid => Get($"https://ci.nii.ac.jp/naid/{E(id.Value)}.json"),
        _ => Get($"https://cir.nii.ac.jp/crid/{E(id.Value)}.json")
    };
    protected override async Task<MetadataCandidate?> ParseAsync(Stream stream, NormalizedIdentifier id, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return JsonMetadata.ParseJsonLd(doc.RootElement);
    }
}

public sealed class LibraryOfCongressMetadataSource : HttpMetadataSource
{
    public LibraryOfCongressMetadataSource(HttpClient client, TimeSpan? timeout = null)
        : base(client, SourceDefinitions.Def("library-of-congress", "Library of Congress", 5, BuiltInIdentifierSchemes.Lccn), timeout) { }

    protected override HttpRequestMessage CreateRequest(NormalizedIdentifier id) => Get($"https://www.loc.gov/item/{E(id.Value)}/?fo=json");
    protected override async Task<MetadataCandidate?> ParseAsync(Stream stream, NormalizedIdentifier id, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return JsonMetadata.ParseLoc(doc.RootElement, id.Value);
    }
}

public sealed class DnbMetadataSource : HttpMetadataSource
{
    private static readonly IReadOnlySet<string> Schemes = SourceDefinitions.Set(BuiltInIdentifierSchemes.Dnb, BuiltInIdentifierSchemes.Gnd, BuiltInIdentifierSchemes.UrnNbnDe);
    public DnbMetadataSource(HttpClient client, TimeSpan? timeout = null)
        : base(client, new MetadataSourceDefinition("dnb", "Deutsche Nationalbibliothek", Schemes, 5), timeout) { }

    protected override string Accept => "application/xml";
    protected override HttpRequestMessage CreateRequest(NormalizedIdentifier id)
    {
        var index = id.Scheme switch { BuiltInIdentifierSchemes.Gnd => "nid", BuiltInIdentifierSchemes.UrnNbnDe => "urn", _ => "idn" };
        return Get($"https://services.dnb.de/sru/dnb?version=1.1&operation=searchRetrieve&recordSchema=MARC21-xml&query={index}%3D{E(id.Value)}");
    }
    protected override async Task<MetadataCandidate?> ParseAsync(Stream stream, NormalizedIdentifier id, CancellationToken ct)
        => XmlMetadata.ParseMarc(await XDocument.LoadAsync(stream, LoadOptions.None, ct), "dnb");
}

public sealed class BnfMetadataSource : HttpMetadataSource
{
    private static readonly IReadOnlySet<string> Schemes = SourceDefinitions.Set(BuiltInIdentifierSchemes.Ark, BuiltInIdentifierSchemes.Frbnf);
    public BnfMetadataSource(HttpClient client, TimeSpan? timeout = null)
        : base(client, new MetadataSourceDefinition("bnf", "Bibliotheque nationale de France", Schemes, 5), timeout) { }

    protected override string Accept => "application/xml";
    protected override HttpRequestMessage CreateRequest(NormalizedIdentifier id)
    {
        var query = id.Scheme == BuiltInIdentifierSchemes.Frbnf ? "bib.fRBNF" : "bib.ark";
        return Get($"https://catalogue.bnf.fr/api/SRU?version=1.2&operation=searchRetrieve&recordSchema=intermarcXchange&query={query}%20all%20%22{E(id.Value)}%22");
    }
    protected override async Task<MetadataCandidate?> ParseAsync(Stream stream, NormalizedIdentifier id, CancellationToken ct)
        => XmlMetadata.ParseMarc(await XDocument.LoadAsync(stream, LoadOptions.None, ct), "bnf");
}

file static class SourceDefinitions
{
    public static MetadataSourceDefinition Def(string id, string name, int priority, params string[] schemes)
        => new(id, name, Set(schemes), priority);

    public static IReadOnlySet<string> Set(params string[] schemes) => schemes.ToHashSet(StringComparer.OrdinalIgnoreCase);
}

file static class CalisMarc
{
    public static MetadataCandidate? Parse(JsonElement root, NormalizedIdentifier requested, string rid)
    {
        if (!root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array) return null;
        var entries = fields.EnumerateArray().ToArray();
        IReadOnlyList<string> Subfields(string tag, string code) => entries
            .Where(entry => entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(tag, out _))
            .SelectMany(entry => entry.GetProperty(tag).TryGetProperty("subfields", out var values) ? values.EnumerateArray() : [])
            .Select(value => value.TryGetProperty(code, out var field) ? field.GetString()?.Trim() : null)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();

        var isbn = Subfields("010", "a").FirstOrDefault(value => IdentifierNormalizer.Normalize(BuiltInIdentifierSchemes.ISBN, value) is { IsSuccess: true } normalized && normalized.Value.Value == requested.Value);
        if (isbn is null) return null;

        var title = Subfields("200", "a").FirstOrDefault();
        if (string.IsNullOrWhiteSpace(title)) return null;
        var subtitle = Subfields("200", "e").FirstOrDefault();
        var authors = new List<MetadataCreator>();
        foreach (var tag in new[] { "701", "702" })
        {
            foreach (var entry in entries.Where(entry => entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(tag, out _)))
            {
                var subfields = entry.GetProperty(tag).GetProperty("subfields");
                var name = subfields.EnumerateArray().Select(value => value.TryGetProperty("a", out var field) ? field.GetString() : null).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                var role = subfields.EnumerateArray().Select(value => value.TryGetProperty("4", out var field) ? field.GetString() : null).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                if (name is null) continue;
                authors.Add(new MetadataCreator(role == "译" ? ItemCreatorRoles.Translator : ItemCreatorRoles.Author, Literal: name.Trim()));
            }
        }

        var year = Subfields("210", "d").Select(value => Regex.Match(value, @"\d{4}").Value).FirstOrDefault(value => value.Length == 4);
        var subjects = Subfields("606", "a").Concat(Subfields("690", "a")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new MetadataCandidate(
            "",
            title,
            subtitle,
            Creators: authors,
            Dates: year is null ? [] : [new MetadataDate(ItemDateRoles.Issued, [int.Parse(year)])],
            Publisher: Subfields("210", "c").FirstOrDefault(),
            Place: Subfields("210", "a").FirstOrDefault(),
            CollectionTitle: Subfields("225", "a").FirstOrDefault(),
            Pages: Subfields("215", "a").FirstOrDefault(),
            Language: Subfields("101", "a").FirstOrDefault(),
            Abstract: Subfields("330", "a").FirstOrDefault(),
            Tags: subjects,
            Identifiers:
            [
                new MetadataIdentifier(BuiltInIdentifierSchemes.ISBN, isbn),
                new MetadataIdentifier("calis", rid)
            ],
            SuggestedItemType: "book",
            TypeConfidence: 0.98);
    }
}

file static partial class NlcHtml
{
    public static bool HasMetadataTable(string html) => Regex.IsMatch(html, """<table[^>]+id\s*=\s*['"]td['"]""", RegexOptions.IgnoreCase);

    public static string? FirstDetailUrl(string html)
    {
        var match = Regex.Match(html, """<div[^>]+class\s*=\s*['"][^'"]*itemtitle[^'"]*['"][^>]*>.*?<a[^>]+href\s*=\s*['"](?<url>[^'"]+)""", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success) return null;
        var value = WebUtility.HtmlDecode(match.Groups["url"].Value);
        return Uri.TryCreate(value, UriKind.Absolute, out var absolute)
            ? absolute.ToString()
            : new Uri(new Uri("http://opac.nlc.cn"), value).ToString();
    }

    public static MetadataCandidate? Parse(string html, NormalizedIdentifier requested)
    {
        if (!HasMetadataTable(html)) return null;
        var isbnPattern = string.Join(@"(?:\s|&nbsp;|-)*", requested.Value.Select(character => Regex.Escape(character.ToString())));
        if (!Regex.IsMatch(html, isbnPattern, RegexOptions.IgnoreCase)) return null;
        var fields = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (Match row in Regex.Matches(html, @"<tr[^>]*>(?<row>.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var cells = Regex.Matches(row.Groups["row"].Value, """<td[^>]*class\s*=\s*['"][^'"]*td1[^'"]*['"][^>]*>(?<value>.*?)</td>""", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (cells.Count != 2) continue;
            var label = Text(cells[0].Groups["value"].Value);
            var value = Text(cells[1].Groups["value"].Value);
            if (label.Length == 0 || value.Length == 0) continue;
            if (!fields.TryGetValue(label, out var values)) fields[label] = values = [];
            values.Add(value);
        }

        string? First(string name) => fields.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;
        var title = First("题名与责任");
        if (string.IsNullOrWhiteSpace(title)) return null;
        title = Regex.Replace(title, @"\s*\[[^\]]+\].*$", string.Empty).Trim();
        var publication = First("出版项") ?? string.Empty;
        var year = Regex.Match(publication, @"\b\d{4}\b").Value;
        var publisher = Regex.Match(publication, @":\s*(?<publisher>[^,，]+)").Groups["publisher"].Value.Trim();
        var authors = (First("著者") ?? string.Empty)
            .Split(['\n', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Regex.Replace(value, @"\s*(著|编|译)$", string.Empty).Trim())
            .Where(value => value.Length > 0)
            .Select(value => new MetadataCreator(ItemCreatorRoles.Author, Literal: value))
            .ToArray();
        var tags = (First("主题") ?? string.Empty).Split(["--", ";", "；"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat((First("中图分类号") ?? string.Empty).Split([';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new MetadataCandidate(
            "",
            title,
            Creators: authors,
            Dates: year.Length == 4 ? [new MetadataDate(ItemDateRoles.Issued, [int.Parse(year)])] : [],
            Publisher: publisher.Length == 0 ? null : publisher,
            Abstract: First("内容提要"),
            Tags: tags,
            Identifiers: [new MetadataIdentifier(BuiltInIdentifierSchemes.ISBN, requested.Value)],
            SuggestedItemType: "book",
            TypeConfidence: 0.95);
    }

    private static string Text(string html) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", string.Empty)), @"\s+", " ").Trim();
}

file static class JsonMetadata
{
    public static MetadataCandidate ParseCrossref(JsonElement root)
    {
        var type = String(root, "type");
        return new MetadataCandidate("", FirstString(root, "title"), FirstString(root, "subtitle"),
            Creators: People(root, "author"), Dates: DateParts(root, "published-print", "published-online", "issued"),
            PublicationTitle: FirstString(root, "container-title"), Publisher: String(root, "publisher"),
            Volume: String(root, "volume"), Issue: String(root, "issue"), Pages: String(root, "page"),
            Language: String(root, "language"), Abstract: String(root, "abstract"), Tags: Strings(root, "subject"),
            Identifiers: IdentifierFields(root, ("DOI", BuiltInIdentifierSchemes.DOI), ("ISBN", BuiltInIdentifierSchemes.ISBN)),
            SuggestedItemType: MapType(type), TypeConfidence: type is null ? 0 : 0.95);
    }

    public static MetadataCandidate ParseDataCite(JsonElement root)
    {
        var title = root.TryGetProperty("titles", out var titles) ? String(titles.EnumerateArray().FirstOrDefault(), "title") : null;
        var creators = root.TryGetProperty("creators", out var people) ? people.EnumerateArray().Select(Person).Where(x => x is not null).Cast<MetadataCreator>().ToArray() : [];
        var year = Int(root, "publicationYear");
        var subjects = root.TryGetProperty("subjects", out var values) ? values.EnumerateArray().Select(x => String(x, "subject")).OfType<string>().ToArray() : [];
        var type = root.TryGetProperty("types", out var types) ? String(types, "resourceTypeGeneral") : null;
        return new MetadataCandidate("", title, Creators: creators, Dates: year is null ? [] : [new MetadataDate(ItemDateRoles.Issued, [year.Value])],
            Publisher: String(root, "publisher"), Language: String(root, "language"), Tags: subjects,
            Identifiers: IdentifierFields(root, ("doi", BuiltInIdentifierSchemes.DOI)), SuggestedItemType: MapType(type), TypeConfidence: type is null ? 0 : 0.9);
    }

    public static MetadataCandidate ParseOpenAlex(JsonElement root)
    {
        var authors = root.TryGetProperty("authorships", out var list) ? list.EnumerateArray().Select(entry =>
        {
            var author = entry.TryGetProperty("author", out var nested) ? nested : default;
            return new MetadataCreator(ItemCreatorRoles.Author, Literal: String(author, "display_name"));
        }).ToArray() : [];
        var location = root.TryGetProperty("primary_location", out var loc) && loc.TryGetProperty("source", out var source) ? source : default;
        var ids = root.TryGetProperty("ids", out var idObject) ? IdentifierFields(idObject, ("doi", BuiltInIdentifierSchemes.DOI), ("pmid", BuiltInIdentifierSchemes.Pmid), ("pmcid", BuiltInIdentifierSchemes.Pmcid), ("openalex", BuiltInIdentifierSchemes.OpenAlex)) : [];
        ids = ids.Select(x => x with { Value = LastPath(x.Value) }).ToArray();
        var year = Int(root, "publication_year");
        var type = String(root, "type");
        return new MetadataCandidate("", String(root, "title"), Creators: authors, Dates: year is null ? [] : [new MetadataDate(ItemDateRoles.Issued, [year.Value])],
            PublicationTitle: String(location, "display_name"), Volume: String(root, "biblio", "volume"), Issue: String(root, "biblio", "issue"),
            Pages: PageRange(root), Language: String(root, "language"), Identifiers: ids, SuggestedItemType: MapType(type), TypeConfidence: type is null ? 0 : 0.9);
    }

    public static MetadataCandidate ParseSemanticScholar(JsonElement root)
    {
        var authors = root.TryGetProperty("authors", out var list) ? list.EnumerateArray().Select(x => new MetadataCreator(ItemCreatorRoles.Author, Literal: String(x, "name"))).ToArray() : [];
        var journal = root.TryGetProperty("journal", out var value) ? value : default;
        var year = Int(root, "year");
        var ids = root.TryGetProperty("externalIds", out var external) ? IdentifierFields(external, ("DOI", BuiltInIdentifierSchemes.DOI), ("ArXiv", BuiltInIdentifierSchemes.ArXiv), ("PubMed", BuiltInIdentifierSchemes.Pmid), ("CorpusId", BuiltInIdentifierSchemes.SemanticScholar)) : [];
        var types = Strings(root, "publicationTypes");
        return new MetadataCandidate("", String(root, "title"), Creators: authors, Dates: year is null ? [] : [new MetadataDate(ItemDateRoles.Issued, [year.Value])],
            PublicationTitle: String(root, "venue"), Volume: String(journal, "volume"), Pages: String(journal, "pages"), Abstract: String(root, "abstract"),
            Identifiers: ids, SuggestedItemType: MapType(types.FirstOrDefault()), TypeConfidence: types.Count > 0 ? 0.9 : 0);
    }

    public static MetadataCandidate ParseOpenLibrary(JsonElement root)
    {
        var authors = ObjectNames(root, "authors", ItemCreatorRoles.Author);
        var publishers = ObjectValues(root, "publishers", "name");
        var subjects = ObjectValues(root, "subjects", "name");
        var identifiers = new List<MetadataIdentifier>();
        if (root.TryGetProperty("identifiers", out var identifierObject) && identifierObject.ValueKind == JsonValueKind.Object)
        {
            identifiers.AddRange(IdentifierFields(identifierObject,
                ("isbn_10", BuiltInIdentifierSchemes.ISBN),
                ("isbn_13", BuiltInIdentifierSchemes.ISBN),
                ("oclc", BuiltInIdentifierSchemes.Oclc),
                ("lccn", BuiltInIdentifierSchemes.Lccn)));
        }
        if (String(root, "key") is { } key)
            identifiers.Add(new MetadataIdentifier(BuiltInIdentifierSchemes.OpenLibrary, key.Trim('/').Split('/').Last()));
        return new MetadataCandidate("", String(root, "title"), String(root, "subtitle"), Creators: authors,
            Dates: LiteralDate(String(root, "publish_date")), Publisher: publishers.FirstOrDefault(), Place: ObjectValues(root, "publish_places", "name").FirstOrDefault(),
            Pages: String(root, "number_of_pages"), Tags: subjects, Identifiers: identifiers, SuggestedItemType: "book", TypeConfidence: 0.95);
    }

    public static MetadataCandidate ParseGoogleBooks(JsonElement info, string? volumeId)
    {
        var authors = Strings(info, "authors").Select(name => new MetadataCreator(ItemCreatorRoles.Author, Literal: name)).ToArray();
        var identifiers = new List<MetadataIdentifier>();
        if (!string.IsNullOrWhiteSpace(volumeId)) identifiers.Add(new(BuiltInIdentifierSchemes.GoogleBooks, volumeId));
        if (info.TryGetProperty("industryIdentifiers", out var ids))
            identifiers.AddRange(ids.EnumerateArray().Where(x => (String(x, "type") ?? "").StartsWith("ISBN", StringComparison.Ordinal)).Select(x => new MetadataIdentifier(BuiltInIdentifierSchemes.ISBN, String(x, "identifier")!)));
        return new MetadataCandidate("", String(info, "title"), String(info, "subtitle"), Creators: authors, Dates: LiteralDate(String(info, "publishedDate")),
            Publisher: String(info, "publisher"), Pages: String(info, "pageCount"), Language: String(info, "language"), Abstract: String(info, "description"),
            Tags: Strings(info, "categories"), Identifiers: identifiers, SuggestedItemType: "book", TypeConfidence: 0.95);
    }

    public static MetadataCandidate ParseIiif(JsonElement root, string scheme, string id)
    {
        var metadata = root.TryGetProperty("metadata", out var values) ? values : default;
        string? Find(string label) => metadata.ValueKind == JsonValueKind.Array
            ? metadata.EnumerateArray().Where(x => string.Equals(String(x, "label"), label, StringComparison.OrdinalIgnoreCase)).Select(x => String(x, "value")).FirstOrDefault()
            : null;
        return new MetadataCandidate("", String(root, "label"), Creators: ToLiteralPeople(Find("Creator") ?? Find("著者")), Dates: LiteralDate(Find("Date") ?? Find("出版年月日")),
            Publisher: Find("Publisher") ?? Find("出版者"), Language: Find("Language") ?? Find("言語"), Note: String(root, "description"),
            Identifiers: [new MetadataIdentifier(scheme, id)]);
    }

    public static MetadataCandidate ParseJsonLd(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) root = root.EnumerateArray().FirstOrDefault(x => x.ValueKind == JsonValueKind.Object && (String(x, "name") is not null || String(x, "headline") is not null));
        var authors = root.TryGetProperty("author", out var author) ? JsonLdPeople(author) : [];
        return new MetadataCandidate("", String(root, "name") ?? String(root, "headline"), Creators: authors, Dates: LiteralDate(String(root, "datePublished")),
            PublicationTitle: String(root, "isPartOf", "name"), Publisher: String(root, "publisher", "name"), Language: String(root, "inLanguage"),
            Abstract: String(root, "description"), Tags: Strings(root, "keywords"));
    }

    public static MetadataCandidate ParseLoc(JsonElement root, string lccn)
    {
        var title = FirstString(root, "title") ?? String(root, "item", "title");
        var nestedContributors = root.TryGetProperty("item", out var item) ? Strings(item, "contributors") : [];
        var contributors = Strings(root, "contributor").Concat(nestedContributors).Distinct().Select(name => new MetadataCreator(ItemCreatorRoles.Author, Literal: name)).ToArray();
        return new MetadataCandidate("", title, Creators: contributors, Dates: LiteralDate(FirstString(root, "date") ?? String(root, "item", "date")),
            Publisher: FirstString(root, "publisher"), Place: FirstString(root, "location"), Language: FirstString(root, "language"),
            Tags: Strings(root, "subject"), Identifiers: [new MetadataIdentifier(BuiltInIdentifierSchemes.Lccn, lccn)]);
    }

    public static IReadOnlyList<MetadataIdentifier> IdentifierFields(JsonElement root, params (string Property, string Scheme)[] fields)
    {
        var result = new List<MetadataIdentifier>();
        foreach (var (property, scheme) in fields)
        {
            if (!root.TryGetProperty(property, out var value)) continue;
            var values = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(Scalar) : [Scalar(value)];
            result.AddRange(values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => new MetadataIdentifier(scheme, x!)));
        }
        return result;
    }

    public static string? String(JsonElement root, string property)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) ? Scalar(value) : null;
    public static string? String(JsonElement root, string parent, string property)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(parent, out var value) ? String(value, property) : null;
    public static IReadOnlyList<string> Strings(JsonElement root, string property)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(property, out var value)) return [];
        return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(Scalar).OfType<string>().ToArray() : Scalar(value) is { } one ? [one] : [];
    }

    private static string? FirstString(JsonElement root, string property) => Strings(root, property).FirstOrDefault();
    private static int? Int(JsonElement root, string property) => int.TryParse(String(root, property), out var number) ? number : null;
    private static string? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => Clean(value.GetString()), JsonValueKind.Number => value.GetRawText(), _ => null
    };
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string LastPath(string value) => value.TrimEnd('/').Split('/').Last();
    private static string? PageRange(JsonElement root)
    {
        var first = String(root, "biblio", "first_page"); var last = String(root, "biblio", "last_page");
        return first is null ? null : last is null || last == first ? first : $"{first}-{last}";
    }
    private static MetadataCreator? Person(JsonElement person)
    {
        var family = String(person, "familyName") ?? String(person, "family"); var given = String(person, "givenName") ?? String(person, "given"); var literal = String(person, "name");
        return family is null && given is null && literal is null ? null : new MetadataCreator(ItemCreatorRoles.Author, family, given, literal);
    }
    private static IReadOnlyList<MetadataCreator> People(JsonElement root, string property)
        => root.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(Person).OfType<MetadataCreator>().ToArray() : [];
    private static IReadOnlyList<MetadataDate> DateParts(JsonElement root, params string[] properties)
    {
        foreach (var property in properties)
            if (root.TryGetProperty(property, out var date) && date.TryGetProperty("date-parts", out var outer) && outer.ValueKind == JsonValueKind.Array)
            {
                var parts = outer.EnumerateArray().FirstOrDefault();
                if (parts.ValueKind == JsonValueKind.Array) return [new MetadataDate(ItemDateRoles.Issued, parts.EnumerateArray().Select(x => x.GetInt32()).ToArray())];
            }
        return [];
    }
    private static IReadOnlyList<MetadataDate> LiteralDate(string? value) => value is null ? [] : [new MetadataDate(ItemDateRoles.Issued, Literal: value)];
    private static IReadOnlyList<MetadataCreator> ObjectNames(JsonElement root, string property, string role) => ObjectValues(root, property, "name").Select(name => new MetadataCreator(role, Literal: name)).ToArray();
    private static IReadOnlyList<string> ObjectValues(JsonElement root, string property, string child)
        => root.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().Select(x => String(x, child)).OfType<string>().ToArray() : [];
    private static IReadOnlyList<MetadataCreator> ToLiteralPeople(string? value) => value is null ? [] : value.Split([';', '／'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(name => new MetadataCreator(ItemCreatorRoles.Author, Literal: name)).ToArray();
    private static IReadOnlyList<MetadataCreator> JsonLdPeople(JsonElement value)
    {
        IEnumerable<JsonElement> values = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : new[] { value };
        return values.Select(x => new MetadataCreator(ItemCreatorRoles.Author, Family: String(x, "familyName"), Given: String(x, "givenName"), Literal: String(x, "name"))).ToArray();
    }
    private static string? MapType(string? value) => value?.ToLowerInvariant() switch
    {
        "journal-article" or "article" or "article-journal" => "article-journal",
        "book" or "book-chapter" or "monograph" => value.Equals("book-chapter", StringComparison.OrdinalIgnoreCase) ? "chapter" : "book",
        "proceedings-article" or "conference" or "conference-paper" => "paper-conference",
        "dissertation" or "thesis" => "thesis",
        "report" => "report",
        "posted-content" or "preprint" => "manuscript",
        _ => null
    };
}

file static class XmlMetadata
{
    public static MetadataCandidate? ParseDublinCore(XDocument document, NormalizedIdentifier requested)
    {
        var identifierTypes = requested.Scheme switch
        {
            BuiltInIdentifierSchemes.Ndlbibid => new[] { "NDLBibID" },
            BuiltInIdentifierSchemes.Jpno => new[] { "JPNO" },
            BuiltInIdentifierSchemes.ISBN => new[] { "ISBN", "ISBN13" },
            _ => Array.Empty<string>()
        };
        var record = document.Descendants()
            .Where(element => element.Name.LocalName is "item" or "recordData")
            .FirstOrDefault(element => element.Descendants().Any(identifier =>
                identifier.Name.LocalName == "identifier"
                && IdentifierMatches(identifier.Value, requested)
                && identifier.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "type"
                    && identifierTypes.Any(type => attribute.Value.EndsWith(type, StringComparison.OrdinalIgnoreCase)))))
            ?? (document.Descendants().Any(element => element.Name.LocalName is "item" or "recordData") ? null : document.Root);
        if (record is null) return null;

        string? First(string name) => record.Descendants()
            .FirstOrDefault(element => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim() is { Length: > 0 } value ? value : null;
        string[] All(string name) => record.Descendants()
            .Where(element => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var identifiers = record.Descendants()
            .Where(element => element.Name.LocalName == "identifier")
            .Select(element =>
            {
                var value = element.Value.Trim();
                var type = element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "type")?.Value;
                if (type?.EndsWith("NDLBibID", StringComparison.OrdinalIgnoreCase) == true || value.Contains("id.ndl.go.jp/bib/", StringComparison.OrdinalIgnoreCase))
                    return new MetadataIdentifier(BuiltInIdentifierSchemes.Ndlbibid, value.TrimEnd('/').Split('/').Last());
                if (type?.EndsWith("JPNO", StringComparison.OrdinalIgnoreCase) == true || value.Contains("id.ndl.go.jp/jpno/", StringComparison.OrdinalIgnoreCase))
                    return new MetadataIdentifier(BuiltInIdentifierSchemes.Jpno, value.TrimEnd('/').Split('/').Last());
                if (type?.EndsWith("ISBN", StringComparison.OrdinalIgnoreCase) == true || type?.EndsWith("ISBN13", StringComparison.OrdinalIgnoreCase) == true)
                    return new MetadataIdentifier(BuiltInIdentifierSchemes.ISBN, value);
                if (type?.EndsWith("NIIBibID", StringComparison.OrdinalIgnoreCase) == true)
                    return new MetadataIdentifier(BuiltInIdentifierSchemes.Ncid, value);
                return null;
            })
            .OfType<MetadataIdentifier>()
            .Append(new MetadataIdentifier(requested.Scheme, requested.Value))
            .DistinctBy(identifier => $"{identifier.Scheme}\0{identifier.Value}", StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new MetadataCandidate(
            "",
            First("title"),
            Creators: All("creator").Select(name => new MetadataCreator(ItemCreatorRoles.Author, Literal: name)).ToArray(),
            Dates: First("date") is { } date ? [new MetadataDate(ItemDateRoles.Issued, Literal: date)] : [],
            Publisher: First("publisher"),
            Language: First("language"),
            Abstract: First("description"),
            Tags: All("subject"),
            Identifiers: identifiers,
            SuggestedItemType: "book",
            TypeConfidence: 0.9);
    }

    private static bool IdentifierMatches(string rawValue, NormalizedIdentifier requested)
    {
        var normalized = IdentifierNormalizer.Normalize(requested.Scheme, rawValue);
        return normalized.IsSuccess
            && string.Equals(normalized.Value.Value, requested.Value, StringComparison.OrdinalIgnoreCase);
    }

    public static MetadataCandidate? ParsePubMed(XDocument document)
    {
        var article = document.Descendants().FirstOrDefault(x => x.Name.LocalName == "PubmedArticle");
        if (article is null) return null;
        var journal = article.Descendants().FirstOrDefault(x => x.Name.LocalName == "Journal");
        var ids = article.Descendants().Where(x => x.Name.LocalName == "ArticleId").Select(x => new MetadataIdentifier((string?)x.Attribute("IdType") switch
        {
            "doi" => BuiltInIdentifierSchemes.DOI, "pubmed" => BuiltInIdentifierSchemes.Pmid, "pmc" => BuiltInIdentifierSchemes.Pmcid, _ => ""
        }, x.Value)).Where(x => x.Scheme.Length > 0).ToArray();
        var authors = article.Descendants().Where(x => x.Name.LocalName == "Author").Select(x => new MetadataCreator(ItemCreatorRoles.Author,
            Text(x, "LastName"), Text(x, "ForeName"), Text(x, "CollectiveName"))).ToArray();
        var date = journal?.Descendants().FirstOrDefault(x => x.Name.LocalName == "PubDate");
        var year = Number(date, "Year");
        var month = Month(date);
        var day = month is null ? null : Number(date, "Day");
        var parts = new[] { year, month, day }.TakeWhile(x => x is not null).Select(x => x!.Value).ToArray();
        return new MetadataCandidate("", Text(article, "ArticleTitle"), Creators: authors, Dates: parts.Length == 0 ? [] : [new MetadataDate(ItemDateRoles.Issued, parts)],
            PublicationTitle: Text(journal, "Title"), Volume: Text(journal, "Volume"), Issue: Text(journal, "Issue"), Pages: Text(article, "MedlinePgn"),
            Language: Text(article, "Language"), Abstract: string.Join(" ", article.Descendants().Where(x => x.Name.LocalName == "AbstractText").Select(x => x.Value.Trim())),
            Tags: article.Descendants().Where(x => x.Name.LocalName == "Keyword").Select(x => x.Value.Trim()).ToArray(), Identifiers: ids,
            SuggestedItemType: "article-journal", TypeConfidence: 0.98);
    }

    public static MetadataCandidate? ParseArXiv(XDocument document)
    {
        var entry = document.Descendants().FirstOrDefault(x => x.Name.LocalName == "entry");
        if (entry is null) return null;
        var id = Text(entry, "id")?.Split('/').Last();
        var doi = Text(entry, "doi");
        var identifiers = new List<MetadataIdentifier>();
        if (id is not null) identifiers.Add(new(BuiltInIdentifierSchemes.ArXiv, id));
        if (doi is not null) identifiers.Add(new(BuiltInIdentifierSchemes.DOI, doi));
        return new MetadataCandidate("", Text(entry, "title"), Creators: entry.Elements().Where(x => x.Name.LocalName == "author").Select(x => new MetadataCreator(ItemCreatorRoles.Author, Literal: Text(x, "name"))).ToArray(),
            Dates: [new MetadataDate(ItemDateRoles.Issued, Literal: Text(entry, "published"))], Abstract: Text(entry, "summary"),
            Tags: entry.Elements().Where(x => x.Name.LocalName == "category").Select(x => (string?)x.Attribute("term")).OfType<string>().ToArray(), Identifiers: identifiers,
            SuggestedItemType: "manuscript", TypeConfidence: 0.95);
    }

    public static MetadataCandidate? ParseMarc(XDocument document, string source)
    {
        var record = document.Descendants().FirstOrDefault(x => x.Name.LocalName == "record" && x.Descendants().Any(y => y.Name.LocalName is "datafield" or "controlfield"));
        if (record is null) return null;
        string? Sub(string tag, params string[] codes) => record.Descendants().Where(x => x.Name.LocalName == "datafield" && (string?)x.Attribute("tag") == tag)
            .SelectMany(x => x.Elements().Where(y => y.Name.LocalName == "subfield" && codes.Contains((string?)y.Attribute("code")))).Select(x => x.Value.Trim().TrimEnd('/', ':', ';')).FirstOrDefault();
        var title = Sub("245", "a") ?? Sub("200", "a") ?? Sub("331", "a");
        var subtitle = Sub("245", "b") ?? Sub("200", "e") ?? Sub("335", "a");
        var authors = record.Descendants().Where(x => x.Name.LocalName == "datafield" && new[] { "100", "110", "700", "701", "800" }.Contains((string?)x.Attribute("tag")))
            .Select(x => x.Elements().FirstOrDefault(y => y.Name.LocalName == "subfield" && (string?)y.Attribute("code") == "a")?.Value).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => new MetadataCreator(ItemCreatorRoles.Author, Literal: x!.Trim())).ToArray();
        var date = Sub("264", "c") ?? Sub("260", "c") ?? Sub("210", "d") ?? Sub("419", "c");
        var isbn = Sub("020", "a") ?? Sub("010", "a") ?? Sub("540", "a");
        return new MetadataCandidate("", title, subtitle, Creators: authors, Dates: date is null ? [] : [new MetadataDate(ItemDateRoles.Issued, Literal: date)],
            Publisher: Sub("264", "b") ?? Sub("260", "b") ?? Sub("210", "c") ?? Sub("419", "b"), Place: Sub("264", "a") ?? Sub("260", "a") ?? Sub("210", "a"),
            Edition: Sub("250", "a") ?? Sub("205", "a") ?? Sub("403", "a"), Language: Sub("041", "a") ?? Sub("101", "a"),
            Identifiers: isbn is null ? [] : [new MetadataIdentifier(BuiltInIdentifierSchemes.ISBN, isbn)], SuggestedItemType: "book", TypeConfidence: 0.9);
    }

    private static string? Text(XContainer? root, string name) => root?.Descendants().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } value ? value : null;
    private static int? Number(XContainer? root, string name) => int.TryParse(Text(root, name), out var value) ? value : null;
    private static int? Month(XContainer? root)
    {
        var value = Text(root, "Month");
        if (int.TryParse(value, out var number)) return number;
        if (value is null || value.Length < 3) return null;
        var index = Array.FindIndex(System.Globalization.DateTimeFormatInfo.InvariantInfo.AbbreviatedMonthNames, x => string.Equals(x, value[..3], StringComparison.OrdinalIgnoreCase));
        return index < 0 ? null : index + 1;
    }
}
