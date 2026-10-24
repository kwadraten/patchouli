using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Host.Extensions;

/// <summary>A pinned OCR invocation. The caller retains source-version and commit authority.</summary>
public sealed class ExtensionDomainRun : IAsyncDisposable
{
    private readonly IExtensionLease _lease;
    private readonly IExtensionResources _capabilities;
    private readonly ExtensionConfigSnapshot _config;
    private int _disposed;

    internal ExtensionDomainRun(IExtensionLease lease, IExtensionResources capabilities,
        ExtensionConfigSnapshot config)
    {
        _lease = lease;
        _capabilities = capabilities;
        _config = config;
    }

    public string Fingerprint => _lease.Fingerprint;
    public ExtensionKind Kind => _lease.Descriptor.Kind;
    public ExtensionConfigSnapshot Config => _config;

    public async Task<Result<OcrExtensionCandidate>> RecognizeAsync(OcrExtensionContext input,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        IOcrExtension extension = _lease.Ocr
                                  ?? throw new InvalidOperationException(
                                      "The selected extension does not implement OCR.");
        // The run owns its configuration, and image buffers are isolated from caller mutation.
        OcrExtensionContext context = input with
        {
            Config = _config,
            Inputs = Array.AsReadOnly(input.Inputs.Select(item => item with
            {
                ImageData = item.ImageData.ToArray()
            }).ToArray())
        };
        Result validInput = ExtensionCandidateValidation.ValidateOcr(context,
            new OcrExtensionCandidate(context.Page, []));
        if (validInput.IsFailure)
        {
            return Result<OcrExtensionCandidate>.Failure(validInput.ErrorCode!, validInput.ErrorMessage!);
        }

        OcrExtensionCandidate candidate = await extension.RecognizeAsync(context, _capabilities, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        Result valid = ExtensionCandidateValidation.ValidateOcr(context, candidate);
        return valid.IsSuccess
            ? Result<OcrExtensionCandidate>.Success(candidate with
            {
                Boxes = Array.AsReadOnly(candidate.Boxes.ToArray())
            })
            : Result<OcrExtensionCandidate>.Failure(valid.ErrorCode!, valid.ErrorMessage!);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _lease.DisposeAsync().ConfigureAwait(false);
        }
    }
}
