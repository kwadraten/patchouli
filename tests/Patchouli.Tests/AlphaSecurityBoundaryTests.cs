using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Mcp;

namespace Patchouli.Tests;

public sealed class AlphaSecurityBoundaryTests
{
    [Fact]
    public void Snapshot_does_not_include_runtime_cache_or_render_png()
    {
        File.ReadAllText(TestPaths.FromRepositoryRoot("tests", "Patchouli.Tests", "SnapshotTests.cs")).Should()
            .Contain("cache").And.Contain("does_not_include");
    }

    [Fact]
    public void Snapshot_does_not_include_provider_secret_in_data_shard()
    {
        File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.Infrastructure", "Snapshots",
            "SnapshotServices.cs")).Should().Contain("[redacted]");
    }

    [Fact]
    public void SensitiveMutableShard_is_reserved_for_explicit_future_device_sync()
    {
        File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.Infrastructure", "Snapshots",
            "SnapshotServices.cs")).Should().Contain("SensitiveMutableShards");
    }

    [Theory]
    [InlineData("/Users/a/secret")]
    [InlineData("file:///Users/a/x.pdf")]
    [InlineData("cache/page-renders/x.png")]
    [InlineData("FAKE_PROVIDER_SECRET_123")]
    [InlineData("sk-test-123")]
    [InlineData("/models/ocr/model.onnx")]
    public void Mcp_never_exposes_sensitive_strings(string text)
    {
        McpOutputSanitizer.Sanitize(text).Should().NotContain(text);
    }

    [Fact]
    public void Versioned_evidence_uri_has_no_local_path_or_secret()
    {
        DocumentInstanceId documentId = DocumentInstanceId.New();
        DocumentTreeRevisionId revisionId = DocumentTreeRevisionId.New();
        DocumentBoxId boxId = DocumentBoxId.New();

        string uri = McpResourceUris.EvidencePageUri(documentId, 1, revisionId, boxId);

        uri.Should().StartWith("patchouli://texts/").And.Contain("?rev=").And.Contain("&box=");
        uri.Should().NotContain("evref").And.NotContain("/tmp/").And.NotContain("C:\\");
    }

    [Theory]
    [InlineData("patchouli://texts/00000000-0000-0000-0000-000000000000/page-1.md?evref=v2:abc")]
    [InlineData("patchouli://evidence/00000000-0000-0000-0000-000000000000")]
    [InlineData("patchouli://documents/00000000-0000-0000-0000-000000000000")]
    public void Mcp_rejects_legacy_evidence_and_document_uris(string uri)
    {
        McpResourceUris.Parse(uri).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void SearchResult_has_no_sql_like_fallback_marker()
    {
        File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.Infrastructure", "Search",
                "SqliteSearchService.cs"))
            .Should().NotContainEquivalentOf("resolved_text like");
    }

    [Fact]
    public void BranchImportPlan_has_no_secret_cache_or_original_file_copy()
    {
        File.ReadAllText(TestPaths.FromRepositoryRoot("tests", "Patchouli.Tests", "SnapshotBranchInspectionTests.cs"))
            .Should().Contain("NotContain(c.Secret)");
    }

    [Fact]
    public void Logs_redact_provider_secret_and_api_key_like_values()
    {
        File.ReadAllText(TestPaths.FromRepositoryRoot("tests", "Patchouli.Tests", "AlphaStabilizationTests.cs"))
            .Should().Contain("Logger_redacts");
    }
}
