using System.Net.Http.Headers;
using System.Text.Json;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Results;
using Patchouli.Core.Update;

namespace Patchouli.Infrastructure.Update;

/// <summary>
/// Issue #15: <see cref="IUpdateSource"/> backed directly by the GitHub
/// Releases API (<c>https://api.github.com/repos/kwadraten/patchouli/releases</c>).
/// Drafts and pre-releases are skipped; the newest stable release by semantic
/// version wins (the API's list order is not trusted for this).
/// </summary>
public sealed class GitHubReleaseSource : IUpdateSource
{
    public const string ReleasesApiUrl =
        "https://api.github.com/repos/kwadraten/patchouli/releases";

    private const int PageSize = 100;

    private readonly HttpClient _httpClient;

    public GitHubReleaseSource(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<Result<ReleaseInfo?>> GetLatestReleaseAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get,
                $"{ReleasesApiUrl}?per_page={PageSize}");
            // GitHub rejects API calls without a User-Agent.
            if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            {
                _httpClient.DefaultRequestHeaders.UserAgent.Add(
                    new ProductInfoHeaderValue("Patchouli.NET", Patchouli.Core.BuildInfo.Version));
            }

            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Result<ReleaseInfo?>.Failure(AppErrorCodes.NetworkError,
                    $"检查更新失败：GitHub API 返回 HTTP {(int)response.StatusCode}。");
            }

            await using Stream content = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument
                .ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Result<ReleaseInfo?>.Failure(AppErrorCodes.NetworkError,
                    "检查更新失败：GitHub API 返回了意外的响应格式。");
            }

            ReleaseInfo? latest = null;
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                ReleaseInfo? release = TryParseRelease(element);
                if (release is null || release.IsDraft || release.IsPrerelease)
                {
                    continue;
                }

                if (latest is null || release.Version.CompareTo(latest.Version) > 0)
                {
                    latest = release;
                }
            }

            return Result<ReleaseInfo?>.Success(latest);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout rather than caller cancellation.
            return Result<ReleaseInfo?>.Failure(AppErrorCodes.NetworkError,
                $"检查更新超时：{exception.Message}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            return Result<ReleaseInfo?>.Failure(AppErrorCodes.NetworkError,
                $"检查更新失败：网络错误（{exception.Message}）。");
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.github-release-source"))
        {
            return Result<ReleaseInfo?>.Failure(AppErrorCodes.NetworkError,
                $"检查更新失败：{exception.Message}");
        }
    }

    private static ReleaseInfo? TryParseRelease(JsonElement element)
    {
        if (!element.TryGetProperty("tag_name", out JsonElement tag) ||
            tag.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        if (!UpdateVersion.TryParse(tag.GetString(), out UpdateVersion? version) || version is null)
        {
            return null;
        }

        bool draft = element.TryGetProperty("draft", out JsonElement draftElement) &&
                     draftElement.ValueKind == JsonValueKind.True;
        bool prerelease = element.TryGetProperty("prerelease", out JsonElement prereleaseElement) &&
                          prereleaseElement.ValueKind == JsonValueKind.True;

        string title = element.TryGetProperty("name", out JsonElement nameElement) &&
                       nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString() ?? string.Empty
            : string.Empty;
        string body = element.TryGetProperty("body", out JsonElement bodyElement) &&
                      bodyElement.ValueKind == JsonValueKind.String
            ? bodyElement.GetString() ?? string.Empty
            : string.Empty;

        DateTimeOffset? publishedAt = null;
        if (element.TryGetProperty("published_at", out JsonElement publishedElement) &&
            publishedElement.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(publishedElement.GetString(), out DateTimeOffset parsed))
        {
            publishedAt = parsed;
        }

        List<ReleaseAssetInfo> assets = [];
        if (element.TryGetProperty("assets", out JsonElement assetsElement) &&
            assetsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assetsElement.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out JsonElement assetName) ||
                    assetName.ValueKind != JsonValueKind.String ||
                    !asset.TryGetProperty("browser_download_url", out JsonElement downloadUrl) ||
                    downloadUrl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                long size = asset.TryGetProperty("size", out JsonElement sizeElement) &&
                            sizeElement.ValueKind == JsonValueKind.Number &&
                            sizeElement.TryGetInt64(out long parsedSize)
                    ? parsedSize
                    : 0;
                assets.Add(new ReleaseAssetInfo(
                    assetName.GetString() ?? string.Empty,
                    downloadUrl.GetString() ?? string.Empty,
                    size));
            }
        }

        HashSet<string> assetNames = assets.Select(static asset => asset.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        assets = assets
            .Where(static asset => !asset.Name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
            .Select(asset => asset with
            {
                ChecksumStatus = assetNames.Contains(asset.Name + ".sha256")
                    ? ChecksumSidecarStatus.Present
                    : ChecksumSidecarStatus.Absent
            })
            .ToList();

        return new ReleaseInfo(
            tag.GetString() ?? string.Empty,
            version,
            title,
            body,
            draft,
            prerelease,
            publishedAt,
            assets);
    }
}
