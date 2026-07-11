using System.Text.Json;
using System.Text.Json.Serialization;
using Patchouli.Core;
using Patchouli.Core.Csl;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.Csl;

public sealed class CslStyleCatalog : ICslStyleCatalog
{
    private const string GitHubChineseTreeUrl = "https://api.github.com/repos/zotero-chinese/styles/git/trees/main?recursive=1";
    private const string GitHubChineseRawRoot = "https://raw.githubusercontent.com/zotero-chinese/styles/main/";
    private const string GiteeChineseTreeUrl = "https://gitee.com/api/v5/repos/zotero-chinese-x/styles/git/trees/main?recursive=1";
    private const string GiteeChineseRawRoot = "https://gitee.com/zotero-chinese-x/styles/raw/main/";
    private const string ZoteroOfficialStylesJsonUrl = "https://www.zotero.org/styles-files/styles.json";
    private const string ZoteroOfficialStyleRoot = "https://www.zotero.org/styles/";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly SourceDefinition[] SourceDefinitions =
    [
        new(
            new CslCatalogSource(
                CslCatalogSourceIds.ZoteroChineseGitHub,
                "中文 CSL（GitHub）",
                "从 zotero-chinese/styles 的 GitHub 仓库直接获取。"),
            CatalogSourceKind.RepositoryTree,
            new Uri(GitHubChineseTreeUrl),
            GitHubChineseRawRoot),
        new(
            new CslCatalogSource(
                CslCatalogSourceIds.ZoteroChineseGitee,
                "中文 CSL（Gitee）",
                "从 zotero-chinese-x/styles 的 Gitee 镜像直接获取。"),
            CatalogSourceKind.RepositoryTree,
            new Uri(GiteeChineseTreeUrl),
            GiteeChineseRawRoot),
        new(
            new CslCatalogSource(
                CslCatalogSourceIds.ZoteroOfficial,
                "Zotero 官方样式",
                "从 Zotero Style Repository 官方索引获取。"),
            CatalogSourceKind.ZoteroOfficialJson,
            new Uri(ZoteroOfficialStylesJsonUrl),
            null)
    ];

    private readonly HttpClient _httpClient;
    private readonly string _cacheRoot;
    private SourceDefinition _currentSource = SourceDefinitions[0];

    public CslStyleCatalog(SqliteConnectionFactory connectionFactory, HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"{BuildInfo.AppName}/{BuildInfo.Version}");
        }

        _cacheRoot = CslStoragePaths.GetStylesRoot(connectionFactory.DatabasePath);
        Directory.CreateDirectory(_cacheRoot);
    }

    public IReadOnlyList<CslCatalogSource> Sources { get; } = SourceDefinitions.Select(source => source.Source).ToArray();

    public CslCatalogSource CurrentSource => _currentSource.Source;

    public Result SetSource(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, "CSL catalog source is required.");
        }

        var source = SourceDefinitions.FirstOrDefault(
            definition => definition.Source.SourceId.Equals(sourceId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, $"Unknown CSL catalog source: {sourceId}.");
        }

        _currentSource = source;
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<CslCatalogStyle>>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var source = _currentSource;
        try
        {
            return await RefreshSourceAsync(source, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.csl-style-catalog"))
        {
            return Result<IReadOnlyList<CslCatalogStyle>>.Failure(
                AppErrorCodes.DatabaseError,
                $"CSL catalog refresh failed for '{source.Source.DisplayName}': {exception.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<CslCatalogStyle>>> SearchAsync(string? query = null, CancellationToken cancellationToken = default)
    {
        var source = _currentSource;
        try
        {
            var stylesResult = await LoadCachedStylesAsync(source, cancellationToken);
            if (stylesResult.IsFailure)
            {
                return Result<IReadOnlyList<CslCatalogStyle>>.Failure(stylesResult.ErrorCode!, stylesResult.ErrorMessage!);
            }

            var styles = stylesResult.Value;
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
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.csl-style-catalog"))
        {
            return Result<IReadOnlyList<CslCatalogStyle>>.Failure(
                AppErrorCodes.DatabaseError,
                $"CSL catalog search failed for '{source.Source.DisplayName}': {exception.Message}");
        }
    }

    private async Task<Result<IReadOnlyList<CslCatalogStyle>>> RefreshSourceAsync(
        SourceDefinition source,
        CancellationToken cancellationToken)
    {
        var styles = await LoadStylesFromSourceAsync(source, cancellationToken);
        await File.WriteAllTextAsync(
            GetCachePath(source),
            JsonSerializer.Serialize(styles, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        return Result<IReadOnlyList<CslCatalogStyle>>.Success(styles);
    }

    private async Task<Result<IReadOnlyList<CslCatalogStyle>>> LoadCachedStylesAsync(
        SourceDefinition source,
        CancellationToken cancellationToken)
    {
        var cachePath = GetCachePath(source);
        if (!File.Exists(cachePath))
        {
            return await RefreshSourceAsync(source, cancellationToken);
        }

        try
        {
            var json = await File.ReadAllTextAsync(cachePath, cancellationToken);
            return Result<IReadOnlyList<CslCatalogStyle>>.Success(
                JsonSerializer.Deserialize<CslCatalogStyle[]>(json) ?? Array.Empty<CslCatalogStyle>());
        }
        catch
        {
            return await RefreshSourceAsync(source, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<CslCatalogStyle>> LoadStylesFromSourceAsync(
        SourceDefinition source,
        CancellationToken cancellationToken)
    {
        var json = await _httpClient.GetStringAsync(source.IndexUri, cancellationToken);
        return source.Kind switch
        {
            CatalogSourceKind.RepositoryTree => ParseRepositoryTreeStyles(source, json),
            CatalogSourceKind.ZoteroOfficialJson => ParseZoteroOfficialStyles(json),
            _ => Array.Empty<CslCatalogStyle>()
        };
    }

    private static IReadOnlyList<CslCatalogStyle> ParseRepositoryTreeStyles(SourceDefinition source, string json)
    {
        var response = JsonSerializer.Deserialize<GitTreeResponse>(json, JsonOptions);
        var styles = new Dictionary<string, CslCatalogStyle>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in response?.Tree ?? [])
        {
            if (!node.Type.Equals("blob", StringComparison.OrdinalIgnoreCase)
                || !node.Path.StartsWith("src/", StringComparison.OrdinalIgnoreCase)
                || !node.Path.EndsWith(".csl", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileName = GetLastPathSegment(node.Path);
            var styleId = StripExtension(fileName);
            if (string.IsNullOrWhiteSpace(styleId))
            {
                continue;
            }

            var displayName = styleId;
            var sourceUrl = source.RawRoot is null ? null : $"{source.RawRoot}{EscapePath(node.Path)}";
            styles[styleId] = new CslCatalogStyle(styleId, displayName, sourceUrl, source.Source.SourceId);
        }

        return styles.Values.OrderBy(style => style.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<CslCatalogStyle> ParseZoteroOfficialStyles(string json)
    {
        var records = JsonSerializer.Deserialize<ZoteroOfficialStyleData[]>(json, JsonOptions) ?? [];
        var styles = new Dictionary<string, CslCatalogStyle>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.Name))
            {
                continue;
            }

            var styleId = record.Name.Trim();
            var displayName = string.IsNullOrWhiteSpace(record.Title) ? HumanizeStyleId(styleId) : record.Title.Trim();
            var sourceUrl = string.IsNullOrWhiteSpace(record.Href)
                ? $"{ZoteroOfficialStyleRoot}{EscapePathSegment(styleId)}"
                : record.Href.Trim();
            styles[styleId] = new CslCatalogStyle(styleId, displayName, sourceUrl, CslCatalogSourceIds.ZoteroOfficial);
        }

        return styles.Values.OrderBy(style => style.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private string GetCachePath(SourceDefinition source)
        => Path.Combine(_cacheRoot, $"catalog-cache-{source.Source.SourceId}.json");

    private static string EscapePath(string path)
        => string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(EscapePathSegment));

    private static string EscapePathSegment(string segment) => Uri.EscapeDataString(segment.Trim());

    private static string GetLastPathSegment(string path)
        => path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";

    private static string StripExtension(string fileName)
        => fileName.EndsWith(".csl", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName;

    private static string HumanizeStyleId(string styleId)
        => string.Join(" ", styleId.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private enum CatalogSourceKind
    {
        RepositoryTree,
        ZoteroOfficialJson
    }

    private sealed record SourceDefinition(
        CslCatalogSource Source,
        CatalogSourceKind Kind,
        Uri IndexUri,
        string? RawRoot);

    private sealed class GitTreeResponse
    {
        public GitTreeNode[] Tree { get; set; } = [];
    }

    private sealed class GitTreeNode
    {
        public string Path { get; set; } = "";
        public string Type { get; set; } = "";
    }

    private sealed class ZoteroOfficialStyleData
    {
        public string? Title { get; set; }
        public string? Name { get; set; }
        public string? Href { get; set; }

        [JsonPropertyName("titleShort")]
        public string? TitleShort { get; set; }
    }
}
