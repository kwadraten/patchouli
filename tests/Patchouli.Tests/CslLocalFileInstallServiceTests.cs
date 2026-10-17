using FluentAssertions;
using Patchouli.Core.Csl;
using Patchouli.Core.Import;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Csl;

namespace Patchouli.Tests;

/// <summary>
/// Issue #11: CSL local file install — create/reuse/conflict semantics live in the
/// domain service; installing never changes the default style; dependent styles with
/// missing parents report dependencies without silent downloads.
/// </summary>
public sealed class CslLocalFileInstallServiceTests : IDisposable
{
    private const string StyleXmlTemplate = """
        <style xmlns="http://purl.org/net/xbiblio/csl" class="in-text" version="1.0">
          <info><title>{0}</title><id>http://www.zotero.org/styles/{1}</id>{2}</info>
          <citation><layout/></citation>
        </style>
        """;

    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"patchouli-csl-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        TestTempFileCleanup.DeleteDirectoryWithRetry(_root);
    }

    private string WriteStyleFile(string fileName, string xml)
    {
        string path = Path.Combine(_root, fileName);
        File.WriteAllText(path, xml);
        return path;
    }

    private static string StyleXml(string id, string title, string extraInfo = "") =>
        string.Format(StyleXmlTemplate, title, id, extraInfo);

    [Fact]
    public async Task Install_new_id_returns_canonical_style_uri()
    {
        var store = new InMemoryCslStyleStore();
        var service = new CslLocalFileInstallService(store);
        string path = WriteStyleFile("fresh.csl", StyleXml("fresh-style", "Fresh Style"));

        Result<CslLocalInstallResult> result = await service.InstallFromFileAsync(path);

        result.IsSuccess.Should().BeTrue();
        result.Value.StyleId.Should().Be("fresh-style");
        result.Value.StyleUri.Should().Be("patchouli://csl-styles/fresh-style.csl");
        result.Value.Reused.Should().BeFalse();
        result.Value.Conflict.Should().BeFalse();
        result.Value.Renderable.Should().BeTrue();
        store.Styles.Should().ContainKey("fresh-style");
    }

    [Fact]
    public async Task Install_same_id_same_content_reuses()
    {
        var store = new InMemoryCslStyleStore();
        var service = new CslLocalFileInstallService(store);
        string path = WriteStyleFile("dup.csl", StyleXml("dup-style", "Dup Style"));

        Result<CslLocalInstallResult> first = await service.InstallFromFileAsync(path);
        Result<CslLocalInstallResult> second = await service.InstallFromFileAsync(path);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        second.Value.Reused.Should().BeTrue();
        second.Value.Conflict.Should().BeFalse();
        store.InstallCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Install_same_id_different_content_is_conflict_not_silent_overwrite()
    {
        var store = new InMemoryCslStyleStore();
        var service = new CslLocalFileInstallService(store);
        string pathA = WriteStyleFile("a.csl", StyleXml("clash", "Title A"));
        string pathB = WriteStyleFile("b.csl", StyleXml("clash", "Title B"));

        Result<CslLocalInstallResult> first = await service.InstallFromFileAsync(pathA);
        Result<CslLocalInstallResult> second = await service.InstallFromFileAsync(pathB);

        first.IsSuccess.Should().BeTrue();
        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(FileImportErrorCodes.StyleConflict);
        // The original content is untouched; the update path is put.
        store.Styles["clash"].DisplayName.Should().Be("Title A");
    }

    [Fact]
    public async Task Install_dependent_style_with_missing_parent_reports_not_silent_download()
    {
        var store = new InMemoryCslStyleStore();
        var service = new CslLocalFileInstallService(store);
        string xml = StyleXml("child-style", "Child Style",
            "<link href=\"http://www.zotero.org/styles/ghost-parent\" rel=\"independent-parent\"/>");
        string path = WriteStyleFile("child.csl", xml);

        Result<CslLocalInstallResult> result = await service.InstallFromFileAsync(path);

        result.IsSuccess.Should().BeTrue();
        result.Value.Renderable.Should().BeFalse();
        result.Value.MissingParent.Should().Be("ghost-parent");
        result.Value.Dependencies.Should().Contain("ghost-parent");
        result.Value.ErrorMessage.Should().NotBeNullOrEmpty();
        store.Styles.Should().NotContainKey("ghost-parent");
    }

    [Fact]
    public async Task Install_dependent_style_with_present_parent_is_renderable()
    {
        var store = new InMemoryCslStyleStore();
        var service = new CslLocalFileInstallService(store);
        await service.InstallFromFileAsync(WriteStyleFile("parent.csl", StyleXml("base-parent", "Base")));
        string xml = StyleXml("child2", "Child 2",
            "<link href=\"http://www.zotero.org/styles/base-parent\" rel=\"independent-parent\"/>");

        Result<CslLocalInstallResult> result =
            await service.InstallFromFileAsync(WriteStyleFile("child2.csl", xml));

        result.IsSuccess.Should().BeTrue();
        result.Value.Renderable.Should().BeTrue();
        result.Value.MissingParent.Should().BeNull();
    }

    [Fact]
    public async Task Install_invalid_xml_fails()
    {
        var service = new CslLocalFileInstallService(new InMemoryCslStyleStore());
        string path = WriteStyleFile("bad.csl", "<style><info>");

        Result<CslLocalInstallResult> result = await service.InstallFromFileAsync(path);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Install_missing_file_fails_with_not_found()
    {
        var service = new CslLocalFileInstallService(new InMemoryCslStyleStore());

        Result<CslLocalInstallResult> result =
            await service.InstallFromFileAsync(Path.Combine(_root, "nope.csl"));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(AppErrorCodes.NotFound);
    }

    private sealed class InMemoryCslStyleStore : ICslStyleStore
    {
        public readonly Dictionary<string, CslStyle> Styles = new(StringComparer.Ordinal);
        public int InstallCallCount { get; private set; }

        public Task<Result<IReadOnlyList<CslStyle>>> ListInstalledStylesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<CslStyle>>.Success(Styles.Values.ToArray()));

        public Task<Result<CslStyle>> GetStyleAsync(string styleId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Styles.TryGetValue(styleId, out CslStyle? style)
                ? Result<CslStyle>.Success(style)
                : Result<CslStyle>.Failure(AppErrorCodes.NotFound, "Style not found."));

        public Task<Result<string>> GetStyleContentAsync(string styleId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<string>.Failure(AppErrorCodes.NotFound, "Style not found."));

        public Task<Result<CslStyle>> InstallStyleAsync(CslCatalogStyle catalogStyle, string contentXml,
            CancellationToken cancellationToken = default)
        {
            InstallCallCount++;
            string hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(contentXml))).ToLowerInvariant();
            var style = new CslStyle(catalogStyle.StyleId, catalogStyle.DisplayName,
                catalogStyle.DefaultLocale, catalogStyle.SourceUrl, catalogStyle.SourceKind,
                hash, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true, false);
            Styles[catalogStyle.StyleId] = style;
            return Task.FromResult(Result<CslStyle>.Success(style));
        }

        public Task<Result<CslStyle>> ReplaceStyleAsync(string styleId, string contentXml,
            string expectedRevision, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<CslStyle>.Failure(AppErrorCodes.UnsupportedOperation, "n/a"));

        public Task<Result<CslStyle>> DisableStyleAsync(string styleId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<CslStyle>.Failure(AppErrorCodes.UnsupportedOperation, "n/a"));

        public Task<Result> RemoveStyleAsync(string styleId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Failure(AppErrorCodes.UnsupportedOperation, "n/a"));

        public Task<Result<CslSettings>> GetSettingsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<CslSettings>.Failure(AppErrorCodes.UnsupportedOperation, "n/a"));

        public Task<Result<CslSettings>> SaveSettingsAsync(string? defaultStyleId, string? locale,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<CslSettings>.Failure(AppErrorCodes.UnsupportedOperation, "n/a"));
    }
}
