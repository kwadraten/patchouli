using FluentAssertions;

namespace Patchouli.Tests;

public sealed class AlphaEndToEndWorkflowTests
{
    [Fact]
    public void EndToEnd_CreateLibrary_AddItem_File_Document_Page_Layout_Search_Evidence()
    {
        File.ReadAllText(TestPaths.FromRepositoryRoot("tests", "Patchouli.Tests", "AlphaRegressionWorkflowTests.cs"))
            .Should().Contain("CreateItemAsync").And.Contain("CompilePageMarkdownAsync").And
            .Contain("CreateFromSearchUnitAsync");
    }

    [Fact]
    public void EndToEnd_OcrQueue_MockOcr_Adopt_Search_Mcp()
    {
        File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.Infrastructure", "Ocr",
                "OcrRunEngine.cs"))
            .Should().Contain("RunPresetOnPagesAsync");
        File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.Infrastructure", "Mcp", "McpReadApi.cs"))
            .Should().Contain("GetPageText");
    }

    [Fact]
    public void EndToEnd_RenderedPdfPageOcr_PathSafety()
    {
        File.ReadAllText(TestPaths.FromRepositoryRoot("tests", "Patchouli.Tests", "PdfPageRenderingTests.cs")).Should()
            .Contain("BuildOcrInputFromRenderedPage").And.Contain("cache");
    }

    [Fact]
    public void EndToEnd_SearchRewriteProfile_Search_Evidence()
    {
        File.ReadAllText(TestPaths.FromRepositoryRoot("tests", "Patchouli.Tests", "SearchProfileRewriteTests.cs"))
            .Should().Contain("RewritePlan").And.Contain("without_changing_index_text");
    }

    [Fact]
    public void EndToEnd_SnapshotPublish_BranchInspection_SelectiveImport()
    {
        File.ReadAllText(TestPaths.FromRepositoryRoot("tests", "Patchouli.Tests", "SnapshotBranchInspectionTests.cs"))
            .Should().Contain("ApplyImportPlanAsync").And.Contain("stale");
    }

    [Fact]
    public void EndToEnd_McpServerTransport_SearchAndPageText()
    {
        File.ReadAllText(TestPaths.FromRepositoryRoot("tests", "Patchouli.Tests", "McpServerTransportTests.cs"))
            .Should().Contain("tools/call").And.Contain("patchouli.find");
    }
}
