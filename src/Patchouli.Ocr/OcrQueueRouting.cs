using Patchouli.Core.Credentials;

namespace Patchouli.Ocr;

/// <summary>Queue resource classification shared by all OCR entry points.</summary>
public static class OcrQueueRouting
{
    public static (string AdapterKind, string? ProviderId) ForEngine(string engineId,
        string? multimodalProviderId = null)
    {
        return engineId switch
        {
            OcrEngineIds.MinerU => (OcrAdapterKind.CloudApi, ProviderIds.MinerU),
            OcrEngineIds.MultimodalLlm => (OcrAdapterKind.CloudApi, multimodalProviderId),
            _ => (OcrAdapterKind.LocalLibrary, null)
        };
    }
}
