using Patchouli.Core.Layout;

namespace Patchouli.Extensions.Contracts;

/// <summary>Caller-owned image identity; coordinates in results are relative to this image.</summary>
public sealed record LayoutRecognitionInput(string Id, ReadOnlyMemory<byte> Image);

public sealed record LayoutRecognitionRegion(
    string Label,
    NormalizedBBox BBox,
    double Confidence,
    int? Order = null);

/// <summary>A caller-neutral task. No document, OCR, or plugin identity is required.</summary>
public interface ILayoutRecognitionJob
{
    ExtensionConfigSnapshot Config { get; }
    IProgress<ExtensionProgress>? Progress { get; }
    ValueTask<LayoutRecognitionInput?> NextInputAsync(CancellationToken cancellationToken = default);

    ValueTask SubmitAsync(LayoutRecognitionInput input, IReadOnlyList<LayoutRecognitionRegion> regions,
        CancellationToken cancellationToken = default);
}

public interface ILayoutRecognitionExtension : IExtensionDefinition
{
    Task RunAsync(ILayoutRecognitionJob job, IExtensionResources resources,
        CancellationToken cancellationToken = default);
}

/// <summary>Shared by host callers and every extension kind; model ids are extension package ids.</summary>
public interface ILayoutRecognitionCapability
{
    Task<IReadOnlyList<ExtensionDescriptor>> ListAsync(CancellationToken cancellationToken = default);
    Task RunAsync(string extensionId, ILayoutRecognitionJob job, CancellationToken cancellationToken = default);
}

public static class LayoutRecognition
{
    /// <summary>Convenience task for a single in-memory image; an empty region list is a valid result.</summary>
    public static async Task<IReadOnlyList<LayoutRecognitionRegion>> DetectAsync(
        ILayoutRecognitionCapability capability, string extensionId, ReadOnlyMemory<byte> image,
        CancellationToken cancellationToken = default)
    {
        SingleImageJob job = new(image);
        await capability.RunAsync(extensionId, job, cancellationToken).ConfigureAwait(false);
        return job.Result ?? throw new InvalidOperationException("The layout job returned without a result.");
    }

    private sealed class SingleImageJob(ReadOnlyMemory<byte> image) : ILayoutRecognitionJob
    {
        private readonly LayoutRecognitionInput _input = new("image", image);
        private int _issued;
        public IReadOnlyList<LayoutRecognitionRegion>? Result { get; private set; }
        public ExtensionConfigSnapshot Config { get; } = new(new Dictionary<string, string>(), 0);
        public IProgress<ExtensionProgress>? Progress => null;

        public ValueTask<LayoutRecognitionInput?> NextInputAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Interlocked.Exchange(ref _issued, 1) == 0 ? _input : null);
        }

        public ValueTask SubmitAsync(LayoutRecognitionInput input, IReadOnlyList<LayoutRecognitionRegion> regions,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(input, _input) || Result is not null)
            {
                throw new InvalidOperationException("Invalid or duplicate layout result.");
            }

            Result = Array.AsReadOnly(regions.ToArray());
            return ValueTask.CompletedTask;
        }
    }
}
