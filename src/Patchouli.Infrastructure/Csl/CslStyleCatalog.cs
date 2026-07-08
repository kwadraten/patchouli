using System.Text.Json;
using System.Text.RegularExpressions;
using Patchouli.Core.Csl;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.Csl;

public sealed class CslStyleCatalog : ICslStyleCatalog
{
    private const string SourceKind = "zotero_chinese";
    private static readonly Uri CatalogUri = new("https://zotero-chinese.github.io/styles/");
    private static readonly Regex StyleLink = new(
        "<a[^>]+href=\"(?<href>[^\"]+\\.csl)\"[^>]*>(?<name>.*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private readonly HttpClient _httpClient;
    private readonly string _cachePath;

    public CslStyleCatalog(SqliteConnectionFactory connectionFactory, HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        var root = CslStoragePaths.GetStylesRoot(connectionFactory.DatabasePath);
        Directory.CreateDirectory(root);
        _cachePath = Path.Combine(root, "catalog-cache.json");
    }

    public async Task<Result<IReadOnlyList<CslCatalogStyle>>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var html = await _httpClient.GetStringAsync(CatalogUri, cancellationToken);
            var styles = ParseStyles(html);
            await File.WriteAllTextAsync(_cachePath, JsonSerializer.Serialize(styles, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            return Result<IReadOnlyList<CslCatalogStyle>>.Success(styles);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result<IReadOnlyList<CslCatalogStyle>>.Failure(AppErrorCodes.DatabaseError, $"CSL catalog refresh failed: {exception.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<CslCatalogStyle>>> SearchAsync(string? query = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var styles = await LoadCachedStylesAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(query))
            {
                return Result<IReadOnlyList<CslCatalogStyle>>.Success(styles);
            }

            var needle = query.Trim();
            return Result<IReadOnlyList<CslCatalogStyle>>.Success(styles
                .Where(style =>
                    style.StyleId.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || style.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result<IReadOnlyList<CslCatalogStyle>>.Failure(AppErrorCodes.DatabaseError, $"CSL catalog search failed: {exception.Message}");
        }
    }

    private async Task<IReadOnlyList<CslCatalogStyle>> LoadCachedStylesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_cachePath))
        {
            var refreshed = await RefreshAsync(cancellationToken);
            return refreshed.IsSuccess ? refreshed.Value : Array.Empty<CslCatalogStyle>();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_cachePath, cancellationToken);
            return JsonSerializer.Deserialize<CslCatalogStyle[]>(json) ?? Array.Empty<CslCatalogStyle>();
        }
        catch
        {
            var refreshed = await RefreshAsync(cancellationToken);
            return refreshed.IsSuccess ? refreshed.Value : Array.Empty<CslCatalogStyle>();
        }
    }

    private static IReadOnlyList<CslCatalogStyle> ParseStyles(string html)
    {
        var styles = new Dictionary<string, CslCatalogStyle>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in StyleLink.Matches(html))
        {
            var href = System.Net.WebUtility.HtmlDecode(match.Groups["href"].Value);
            var text = StripTags(System.Net.WebUtility.HtmlDecode(match.Groups["name"].Value)).Trim();
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            var absolute = new Uri(CatalogUri, href);
            var fileName = Path.GetFileNameWithoutExtension(absolute.AbsolutePath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            var styleId = fileName.Trim();
            var displayName = string.IsNullOrWhiteSpace(text) ? HumanizeStyleId(styleId) : text;
            styles[styleId] = new CslCatalogStyle(styleId, displayName, absolute.ToString(), SourceKind);
        }

        return styles.Values.OrderBy(style => style.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string StripTags(string value) => Regex.Replace(value, "<.*?>", string.Empty);

    private static string HumanizeStyleId(string styleId)
        => string.Join(" ", styleId.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
}
