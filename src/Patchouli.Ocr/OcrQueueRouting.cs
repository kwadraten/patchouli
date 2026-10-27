using Patchouli.Core.Credentials;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Ocr;

/// <summary>Queue resource classification shared by all OCR entry points.</summary>
public static class OcrQueueRouting
{
    /// <summary>
    /// Derives the queue resource lane from frozen inputs: the registered engine definition's
    /// declared adapter kind and the already-resolved provider identity from the frozen
    /// configuration — never a per-engine switch. MinerU's built-in cloud protocol is the one
    /// explicit built-in exception: its provider identity is intrinsic to the engine, not
    /// configurable. Engines with no registered definition keep the caller-declared lane.
    /// </summary>
    public static (string AdapterKind, string? ProviderId) ForEngine(string engineId,
        OcrEngineDefinition? definition = null, string? resolvedProviderId = null)
    {
        string adapterKind = definition?.AdapterKind
                             ?? (engineId == OcrEngineIds.MinerU
                                 ? OcrAdapterKind.CloudApi
                                 : OcrAdapterKind.LocalLibrary);
        string? providerId = engineId == OcrEngineIds.MinerU ? ProviderIds.MinerU : resolvedProviderId;
        return (adapterKind, providerId);
    }
}
