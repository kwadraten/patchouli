using System.Net;
using System.Text;
using FluentAssertions;
using Patchouli.Core.Results;
using Patchouli.Core.Update;
using Patchouli.Infrastructure.Update;

namespace Patchouli.Tests;

/// <summary>
/// Issue #15: GitHub release metadata parsing. Drafts and pre-releases are
/// skipped, unparseable tags are ignored, and the newest stable release wins
/// by semantic version — never by the API's list order.
/// </summary>
public sealed class GitHubReleaseSourceTests
{
    private static string ReleaseJson(string tag, bool draft, bool prerelease,
        string assets = "[]")
    {
        return $$"""
                 {
                   "tag_name": "{{tag}}",
                   "name": "Release {{tag}}",
                   "body": "notes",
                   "draft": {{(draft ? "true" : "false")}},
                   "prerelease": {{(prerelease ? "true" : "false")}},
                   "published_at": "2026-10-19T00:00:00Z",
                   "assets": {{assets}}
                 }
                 """;
    }

    private static GitHubReleaseSource SourceFor(string jsonBody, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        });
        return new GitHubReleaseSource(new HttpClient(handler));
    }

    [Fact]
    public async Task Skips_drafts_prereleases_and_takes_highest_stable_by_semver()
    {
        // API order is deliberately unhelpful: the newest-by-version stable
        // release (0.3.13) is neither first nor last.
        string body = "[" + string.Join(",",
            ReleaseJson("v0.3.9", draft: false, prerelease: false),
            ReleaseJson("v0.4.0", draft: true, prerelease: false),
            ReleaseJson("v0.3.14", draft: false, prerelease: true),
            ReleaseJson("v0.3.13", draft: false, prerelease: false,
                """[{"name": "Patchouli.NET-0.3.13-linux-x64.AppImage", "browser_download_url": "https://example.test/a.AppImage", "size": 42}, {"name": "Patchouli.NET-0.3.13-linux-x64.AppImage.sha256", "browser_download_url": "https://example.test/a.AppImage.sha256", "size": 93}]"""),
            ReleaseJson("v0.3.10", draft: false, prerelease: false)) + "]";

        Result<ReleaseInfo?> result = await SourceFor(body).GetLatestReleaseAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.TagName.Should().Be("v0.3.13");
        // UpdateVersion is a record over an int[]: compare by ordering, not record equality.
        result.Value.Version.CompareTo(UpdateVersion.Parse("0.3.13")).Should().Be(0);
        result.Value.IsDraft.Should().BeFalse();
        result.Value.IsPrerelease.Should().BeFalse();
        result.Value.Assets.Should().ContainSingle()
            .Which.Should().Be(new ReleaseAssetInfo("Patchouli.NET-0.3.13-linux-x64.AppImage",
                "https://example.test/a.AppImage", 42, ChecksumSidecarStatus.Present));
    }

    [Fact]
    public async Task Marks_checksum_absence_only_when_release_asset_list_confirms_it()
    {
        string body = "[" + ReleaseJson("v0.3.13", draft: false, prerelease: false,
            """[{"name": "Patchouli.NET-0.3.13-win-x64-setup.exe", "browser_download_url": "https://example.test/a.exe", "size": 42}]""") + "]";

        Result<ReleaseInfo?> result = await SourceFor(body).GetLatestReleaseAsync();

        result.Value!.Assets.Should().ContainSingle().Which.ChecksumStatus.Should()
            .Be(ChecksumSidecarStatus.Absent);
    }

    [Fact]
    public async Task Ignores_releases_with_unparseable_tags()
    {
        string body = "[" + string.Join(",",
            ReleaseJson("not-a-version", draft: false, prerelease: false),
            ReleaseJson("v0.3.13", draft: false, prerelease: false)) + "]";

        Result<ReleaseInfo?> result = await SourceFor(body).GetLatestReleaseAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.TagName.Should().Be("v0.3.13");
    }

    [Fact]
    public async Task Returns_null_when_no_stable_release_exists()
    {
        string body = "[" + string.Join(",",
            ReleaseJson("v0.4.0", draft: true, prerelease: false),
            ReleaseJson("v0.3.14", draft: false, prerelease: true)) + "]";

        Result<ReleaseInfo?> result = await SourceFor(body).GetLatestReleaseAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task Http_error_is_a_failure_result()
    {
        Result<ReleaseInfo?> result = await SourceFor("[]", HttpStatusCode.InternalServerError)
            .GetLatestReleaseAsync();

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.NetworkError);
    }

    [Fact]
    public async Task Non_array_response_is_a_failure_result()
    {
        Result<ReleaseInfo?> result = await SourceFor("""{"message": "rate limited"}""")
            .GetLatestReleaseAsync();

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Empty_release_list_returns_null()
    {
        Result<ReleaseInfo?> result = await SourceFor("[]").GetLatestReleaseAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
