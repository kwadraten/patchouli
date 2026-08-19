using FluentAssertions;
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

        LibraryShellViewModel.RequiresMinerUToken(OcrEngineIds.MinerU, credentialed).Should().BeTrue();
        LibraryShellViewModel.RequiresMinerUToken(OcrEngineIds.NdlKoten, local).Should().BeFalse();
        LibraryShellViewModel.RequiresMinerUToken(OcrEngineIds.NdlKoten, credentialed).Should().BeFalse();
    }
}
