using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Infrastructure.Bibliography.MetadataLookup;

/// <summary>Adapts one leased FSX metadata extension to the host metadata source contract.</summary>
public sealed class ExtensionMetadataSource : IMetadataSource
{
    private readonly IExtensionManager _manager;
    private readonly string _extensionId;
    private readonly Func<IExtensionLease, ExtensionConfigSnapshot, IExtensionCapabilities> _capabilitiesFactory;
    private readonly Func<CancellationToken, Task>? _initializeAsync;

    public ExtensionMetadataSource(
        IExtensionManager manager,
        string extensionId,
        MetadataSourceDefinition definition,
        Func<IExtensionLease, ExtensionConfigSnapshot, IExtensionCapabilities> capabilitiesFactory,
        Func<CancellationToken, Task>? initializeAsync = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _extensionId = extensionId.Trim();
        _capabilitiesFactory = capabilitiesFactory ?? throw new ArgumentNullException(nameof(capabilitiesFactory));
        _initializeAsync = initializeAsync;
    }

    public MetadataSourceDefinition Definition { get; }

    public async Task<Result<MetadataCandidate>> LookupAsync(
        NormalizedIdentifier identifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        if (!Definition.SupportedSchemes.Contains(identifier.Scheme))
        {
            return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.UnsupportedIdentifier,
                $"The {Definition.DisplayName} source does not support {identifier.Scheme}.");
        }

        try
        {
            if (_initializeAsync is not null)
            {
                await _initializeAsync(cancellationToken).ConfigureAwait(false);
            }

            await using IExtensionLease? lease = await _manager.AcquireAsync(_extensionId, cancellationToken)
                .ConfigureAwait(false);
            if (lease?.Metadata is not { } extension)
            {
                return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.ProviderUnavailable,
                    $"The {Definition.DisplayName} extension is not active.");
            }

            if (!string.Equals(extension.SourceDefinition.Id, Definition.Id, StringComparison.OrdinalIgnoreCase) ||
                !extension.SourceDefinition.SupportedSchemes.Contains(identifier.Scheme))
            {
                return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.ProviderUnavailable,
                    $"The active {Definition.DisplayName} extension has an incompatible source definition.");
            }

            ExtensionConfigSnapshot config = ExtensionConfiguration.Resolve(lease.ConfigFields, lease.Config);
            IExtensionCapabilities capabilities = _capabilitiesFactory(lease, config);
            Result<MetadataCandidate> result = await extension.LookupAsync(identifier, config, capabilities,
                cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                return Result<MetadataCandidate>.Failure(result.ErrorCode!, result.ErrorMessage!);
            }

            if (!string.Equals(result.Value.SourceId, Definition.Id, StringComparison.OrdinalIgnoreCase))
            {
                return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.InvalidResponse,
                    $"{Definition.DisplayName} returned a candidate for a different source.");
            }

            return Result<MetadataCandidate>.Success(result.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (UnexpectedExceptionReporter.ReportCatch(
                                          error, "infrastructure.metadata-extension-source"))
        {
            return Result<MetadataCandidate>.Failure(MetadataLookupErrorCodes.ProviderUnavailable,
                $"{Definition.DisplayName} lookup failed: {error.Message}");
        }
    }
}
