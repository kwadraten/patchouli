using System.IO.Compression;
using FluentAssertions;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Ocr.MinerU;
using Patchouli.Ocr;
using Patchouli.Ocr.MinerU;

namespace Patchouli.Tests;

public sealed class MinerUResultArtifactTests
{
    // Synthetic protocol fixtures for dispatch boundaries, not captured OCR output.
    private const string Flat = """
                                [{"type":"text","page_idx":0,"bbox":[100,100,900,200],"text":"selected"}]
                                """;

    private const string V2 = """
                              [[{"type":"text","bbox":[100,100,900,200],"text":"selected"}]]
                              """;

    private const string Layout = """
                                  {"pdf_info":[{"page_idx":0,"page_size":[1000,1000],"para_blocks":[
                                    {"type":"text","bbox":[100,100,900,200],"lines":[
                                      {"spans":[{"type":"text","content":"selected"}]}]}]}]}
                                  """;

    private const string Middle = """
                                  {"schema":"docvortex.middle","schema_version":"2.0","pages":[
                                    {"page_idx":0,"blocks":[{"type":"text","index":0,
                                      "bbox":[0.1,0.1,0.9,0.2],"content":[{"type":"text","content":"selected"}]}]}]}
                                  """;

    [Theory]
    [InlineData("content_list.json")]
    [InlineData("paper_content_list.json")]
    [InlineData("cache/CONTENT_LIST.JSON")]
    [InlineData("cache/paper_content_list.json")]
    [InlineData("content_list_v2.json")]
    [InlineData("cache/PAPER_CONTENT_LIST_V2.JSON")]
    public void Imports_bare_prefixed_nested_and_case_insensitive_content_lists(string name)
    {
        WithZip([(name, name.Contains("v2", StringComparison.OrdinalIgnoreCase) ? V2 : Flat)], path =>
        {
            Result<string> result = new MinerUResultParser().ParsePlainText(new MinerUPreparedResult(path, [], null));
            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            result.Value.Should().Be("selected");
        });
    }

    [Theory]
    [InlineData("middle.json")]
    [InlineData("paper_middle.json")]
    [InlineData("cache/PAPER_MIDDLE.JSON")]
    [InlineData("middle_json.json")]
    [InlineData("layout.json")]
    [InlineData("content_list.json")]
    public void Dispatches_legacy_pdf_info_by_content_and_preserves_explicit_semantics(string name)
    {
        WithZip([(name, Layout)], path =>
        {
            using MinerUZipReader reader = MinerUZipReader.Open(path);
            Result<MinerUResultArtifact> result = MinerUResultArtifactReader.Read(reader);
            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            result.Value.Format.Should().Be(MinerUResultArtifactFormat.Layout);
            result.Value.Document.ContinuationSemantics.Should().Be(MinerUContinuationSemantics.Explicit);
            result.Value.Document.Pages.Single().Blocks.Single().Text.Should().Be("selected");
        });
    }

    [Theory]
    [InlineData("middle_json.json")]
    [InlineData("layout.json")]
    [InlineData("paper_middle.json")]
    [InlineData("content_list.json")]
    public void Dispatches_versioned_middle_by_content_even_under_legacy_names(string name)
    {
        WithZip([(name, Middle)], path =>
        {
            using MinerUZipReader reader = MinerUZipReader.Open(path);
            Result<MinerUResultArtifact> result = MinerUResultArtifactReader.Read(reader);
            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            result.Value.Format.Should().Be(MinerUResultArtifactFormat.Middle);
            result.Value.Document.ContinuationSemantics.Should().Be(MinerUContinuationSemantics.Explicit);
            result.Value.Document.Pages.Single().Blocks.Single().Text.Should().Be("selected");
        });
    }

    [Theory]
    [InlineData("middle_json.json")]
    [InlineData("layout.json")]
    public void Dispatches_a_content_list_by_content_under_intermediate_names(string name)
    {
        WithZip([(name, Flat)], path =>
        {
            Result<string> result = new MinerUResultParser().ParsePlainText(new MinerUPreparedResult(path, [], null));
            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            result.Value.Should().Be("selected");
        });
    }

    [Theory]
    [InlineData("notcontent_list.json")]
    [InlineData("notmiddle_json.json")]
    [InlineData("manifest.json")]
    [InlineData("full.md")]
    public void Does_not_accept_unrelated_names_as_tree_artifacts(string name)
    {
        WithZip([(name, Flat)], path =>
        {
            Result<string> result = new MinerUResultParser().ParsePlainText(new MinerUPreparedResult(path, [], null));
            result.ErrorCode.Should().Be("tree_artifact_required");
        });
    }

    [Theory]
    [InlineData("middle_json.json", "{", "artifact_json_invalid")]
    [InlineData("layout.json", "{", "artifact_json_invalid")]
    [InlineData("layout.json", "{}", "artifact_structure_unrecognized")]
    [InlineData("layout.json", "{\"pdf_info\":{}}", "no_content")]
    [InlineData("middle_json.json", "{\"schema\":\"other\",\"pages\":[]}", "artifact_protocol_unsupported")]
    [InlineData("middle_json.json", "{\"schema\":\"docvortex.middle\",\"schema_version\":\"9.0\",\"pdf_info\":[]}",
        "artifact_protocol_unsupported")]
    [InlineData("middle_json.json", "{\"schema\":\"docvortex.middle\",\"schema_version\":\"2.0\"}",
        "artifact_structure_invalid")]
    [InlineData("content_list_v2.json", "[]", "no_content")]
    public void Preferred_artifact_failure_identifies_file_and_reason_without_fallback(
        string name, string json, string errorCode)
    {
        WithZip([(name, json), ("paper_content_list.json", Flat)], path =>
        {
            Result<string> result = new MinerUResultParser().ParsePlainText(new MinerUPreparedResult(path, [], null));
            result.IsFailure.Should().BeTrue();
            result.ErrorCode.Should().Be(errorCode);
            result.ErrorMessage.Should().Contain(name).And.Contain("No fallback was attempted");
        });
    }

    [Fact]
    public void Does_not_drop_unsupported_middle_styles_to_use_a_legacy_projection()
    {
        string underline = Middle.Replace("\"content\":\"selected\"",
            "\"content\":\"selected\",\"styles\":[\"underline\"]", StringComparison.Ordinal);
        WithZip([("middle_json.json", underline), ("paper_content_list.json", Flat)], path =>
        {
            Result<string> result = new MinerUResultParser().ParsePlainText(new MinerUPreparedResult(path, [], null));
            result.ErrorCode.Should().Be("unsupported_middle_semantics");
            result.ErrorMessage.Should().Contain("middle_json.json").And.Contain("underline")
                .And.Contain("No fallback was attempted");
        });
    }

    [Theory]
    [InlineData("a/content_list.json", "b/paper_content_list.json")]
    [InlineData("content_list.json", "content_list.json")]
    public void Rejects_equally_preferred_artifacts_instead_of_selecting_zip_order(string first, string second)
    {
        WithZip([(first, Flat), (second, Flat)], path =>
        {
            Result<string> result = new MinerUResultParser().ParsePlainText(new MinerUPreparedResult(path, [], null));
            result.ErrorCode.Should().Be("artifact_ambiguous");
            result.ErrorMessage.Should().Contain(first).And.Contain(second);
        });
    }

    [Fact]
    public void Keeps_richer_layout_ahead_of_content_list_regardless_of_archive_order()
    {
        WithZip([
            ("content_list.json", Flat.Replace("selected", "projection", StringComparison.Ordinal)),
            ("paper_middle.json", Layout)
        ], path =>
        {
            Result<string> result = new MinerUResultParser().ParsePlainText(new MinerUPreparedResult(path, [], null));
            result.Value.Should().Be("selected");
        });
    }

    [Theory]
    [InlineData("\"bbox\":[100,100,900,200]", "\"bbox\":[-1,100,900,200]", "bbox_invalid")]
    [InlineData("\"type\":\"text\"", "\"type\":\"unknown_widget\"", "unsupported_box_type")]
    public void Filename_compatibility_does_not_bypass_candidate_validation(string before, string after, string code)
    {
        WithZip([("content_list.json", Flat.Replace(before, after, StringComparison.Ordinal))], path =>
        {
            Page page = new(PageId.New(), DocumentInstanceId.New(), 0, null, 1000, 1000, 0,
                "pdf", 1000, 1000, "test", null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            Result<OcrDocumentTreeCandidate> result = new MinerUResultParser()
                .ParseStructuredTree(new MinerUPreparedResult(path, [], null), [page]);
            result.IsSuccess.Should().BeTrue(result.ErrorMessage);
            result.Value.Validate().IsFailure.Should().BeTrue();
            result.Value.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == code && diagnostic.BlocksCommit);
        });
    }

    private static void WithZip((string Name, string Json)[] entries, Action<string> inspect)
    {
        string path = Path.Combine(Path.GetTempPath(), $"mineru-artifact-{Guid.NewGuid():N}.zip");
        try
        {
            using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach ((string name, string json) in entries)
                {
                    using StreamWriter writer = new(archive.CreateEntry(name).Open());
                    writer.Write(json);
                }
            }

            inspect(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
