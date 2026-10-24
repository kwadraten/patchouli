using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Extensions;
using Patchouli.Mcp;
using Patchouli.Ocr;

namespace Patchouli.Host.Mcp;

/// <summary>
/// Issue #28: host-side <see cref="IMcpImplementationCatalogApi"/> over the real
/// registries — the OCR adapter registry (the engine ids <c>runs/ocr/control</c>
/// actually accepts), the extension catalog (translation extension ids), and the
/// metadata source registry (source ids with their identifier schemes). Nothing is
/// hardcoded: adding an adapter, extension or source changes the endpoint
/// descriptions without touching this class.
/// </summary>
public sealed class McpHostImplementationCatalogApi(
    IOcrAdapterRegistry ocrAdapters,
    Func<string> defaultOcrEngineId,
    TranslationExtensionCatalog extensionCatalog,
    IMetadataSourceRegistry metadataSources,
    Func<string> configuredTranslationEngineId) : IMcpImplementationCatalogApi
{
    public async Task<Result<IReadOnlyList<McpImplementationEntry>>> ListAsync(string domain,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return domain switch
            {
                "ocr" => Result<IReadOnlyList<McpImplementationEntry>>.Success(ListOcrEngines()),
                "translation" => await ListTranslationExtensionsAsync(cancellationToken).ConfigureAwait(false),
                "metadata" => Result<IReadOnlyList<McpImplementationEntry>>.Success(ListMetadataSources()),
                _ => Result<IReadOnlyList<McpImplementationEntry>>.Failure(AppErrorCodes.InvalidArgument,
                    $"Unknown runs domain '{domain}'.")
            };
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "host.mcp-implementation-catalog", domain))
        {
            return Result<IReadOnlyList<McpImplementationEntry>>.Failure(
                "implementation_catalog_unavailable", "The implementation catalog is unavailable.");
        }
    }

    private IReadOnlyList<McpImplementationEntry> ListOcrEngines()
    {
        string defaultId = (defaultOcrEngineId() ?? string.Empty).Trim().ToLowerInvariant();
        List<McpImplementationEntry> entries = [];
        if (defaultId.Length > 0)
        {
            // The literal selector "default" is passable as the start message's engine
            // argument; list it explicitly so an agent does not have to discover the
            // alias from failure messages.
            entries.Add(new McpImplementationEntry(
                "default",
                "Configured default engine",
                $"Selector alias: resolves to the host's configured OCR engine '{defaultId}'."));
        }

        foreach (OcrEngineCapability capability in ocrAdapters.ListCapabilities())
        {
            entries.Add(new McpImplementationEntry(
                capability.EngineId,
                capability.DisplayName,
                string.IsNullOrWhiteSpace(capability.Notes) ? null : capability.Notes,
                IsDefault: string.Equals(capability.EngineId, defaultId, StringComparison.Ordinal)));
        }

        return entries;
    }

    private async Task<Result<IReadOnlyList<McpImplementationEntry>>> ListTranslationExtensionsAsync(
        CancellationToken cancellationToken)
    {
        string configured = configuredTranslationEngineId();
        IReadOnlyList<ExtensionDescriptor> descriptors =
            await extensionCatalog.ListExtensionsByKindAsync(ExtensionKind.Translation, cancellationToken)
                .ConfigureAwait(false);
        return Result<IReadOnlyList<McpImplementationEntry>>.Success(descriptors
            .Select(descriptor => new McpImplementationEntry(
                descriptor.Id,
                descriptor.Name,
                string.IsNullOrWhiteSpace(descriptor.Description) ? null : descriptor.Description,
                IsDefault: string.Equals(descriptor.Id, configured, StringComparison.OrdinalIgnoreCase)))
            .ToArray());
    }

    private IReadOnlyList<McpImplementationEntry> ListMetadataSources()
    {
        // The registry's own order is the lookup priority order; the entry's active
        // flag is the source's declared default (per-library user preferences are
        // applied by the lookup service itself, not by the registry definitions).
        return metadataSources.Sources
            .Select(source => new McpImplementationEntry(
                source.Id,
                source.DisplayName,
                IdentifierSchemes: source.SupportedSchemes.OrderBy(scheme => scheme, StringComparer.Ordinal)
                    .ToArray(),
                Active: source.DefaultEnabled))
            .ToArray();
    }
}
