using FluentAssertions;
using Patchouli.Host.Import;
using Patchouli.Ocr;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

public sealed class OcrTokenGatingTests
{
    [Fact]
    public void Requires_mineru_token_only_for_mineru_capability()
    {
        OcrEngineCapability credentialed = new(OcrEngineIds.MinerU, "MinerU", false, true, false, true, false, false,
            false, true, false, [], "");
        OcrEngineCapability local = credentialed with { EngineId = OcrEngineIds.NdlKoten, RequiresCredential = false };

        LibraryImportOrchestrator.RequiresMinerUToken(OcrEngineIds.MinerU, credentialed).Should().BeTrue();
        LibraryImportOrchestrator.RequiresMinerUToken(OcrEngineIds.NdlKoten, local).Should().BeFalse();
        LibraryImportOrchestrator.RequiresMinerUToken(OcrEngineIds.NdlKoten, credentialed).Should().BeFalse();
    }
}
