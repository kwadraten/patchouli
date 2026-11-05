using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Host.Extensions;

/// <summary>Generation-pinned layout jobs shared by host callers and extensions.</summary>
public sealed class LayoutRecognitionService(
    TranslationExtensionCatalog catalog,
    ExtensionManagedFileService? managedFiles = null) : ILayoutRecognitionCapability
{
    private static readonly AsyncLocal<IReadOnlySet<string>?> CallPath = new();

    public Task<IReadOnlyList<ExtensionDescriptor>> ListAsync(CancellationToken cancellationToken = default)
    {
        return catalog.ListExtensionsByKindAsync(ExtensionKind.LayoutRecognition, cancellationToken);
    }

    public async Task RunAsync(string extensionId, ILayoutRecognitionJob job,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        ArgumentNullException.ThrowIfNull(job);
        IReadOnlySet<string>? previous = CallPath.Value;
        if (previous?.Contains(extensionId) == true)
        {
            throw new InvalidOperationException("Recursive layout model invocation.");
        }

        CallPath.Value = new HashSet<string>(previous ?? new HashSet<string>(), StringComparer.Ordinal) { extensionId };
        try
        {
            await catalog.EnsureExtensionAsync(extensionId, cancellationToken).ConfigureAwait(false);
            await using IExtensionLease lease = await catalog.Manager.AcquireAsync(extensionId, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidOperationException("The layout model is not active.");
            if (lease.Descriptor.Kind != ExtensionKind.LayoutRecognition ||
                lease.Definition is not ILayoutRecognitionExtension extension)
            {
                throw new InvalidOperationException("Select a layout recognition extension.");
            }

            ExtensionConfigSnapshot config = ExtensionConfiguration.Resolve(lease.ConfigFields,
                managedFiles?.ResolveDefaults(extensionId, lease.ConfigFields, lease.Config) ?? lease.Config);
            if (managedFiles is not null)
            {
                Result ready = await managedFiles.EnsureAsync(extensionId, lease.ConfigFields, config,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (ready.IsFailure)
                {
                    throw new InvalidOperationException(ready.ErrorMessage);
                }
            }

            GuardedJob guarded = new(job, config);
            try
            {
                await Task.Run(() => extension.RunAsync(guarded, catalog.Resources.ForRun(config), cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                await guarded.VerifyCompletedAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await guarded.CloseAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            CallPath.Value = previous;
        }
    }

    private sealed class GuardedJob(ILayoutRecognitionJob caller, ExtensionConfigSnapshot config)
        : ILayoutRecognitionJob
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<string, LayoutRecognitionInput> _pending = new(StringComparer.Ordinal);
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private bool _exhausted;
        private bool _closed;
        public ExtensionConfigSnapshot Config => config;
        public IProgress<ExtensionProgress>? Progress => caller.Progress;

        public async ValueTask<LayoutRecognitionInput?> NextInputAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                if (_exhausted)
                {
                    return null;
                }

                LayoutRecognitionInput? input = await caller.NextInputAsync(cancellationToken).ConfigureAwait(false);
                if (input is null)
                {
                    _exhausted = true;
                    return null;
                }

                if (string.IsNullOrWhiteSpace(input.Id) || input.Image.IsEmpty || !_seen.Add(input.Id))
                {
                    throw new ArgumentException("Layout inputs require unique ids and nonempty images.");
                }

                _pending.Add(input.Id, input);
                return input;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask SubmitAsync(LayoutRecognitionInput input,
            IReadOnlyList<LayoutRecognitionRegion> regions, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(regions);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                if (!_pending.TryGetValue(input.Id, out LayoutRecognitionInput? issued) ||
                    !ReferenceEquals(issued, input))
                {
                    throw new ArgumentException("Foreign or already settled layout input.");
                }

                LayoutRecognitionRegion[] frozen = regions.ToArray();
                if (frozen.Any(region => region is null || string.IsNullOrWhiteSpace(region.Label) ||
                                         !double.IsFinite(region.Confidence) || region.Confidence is < 0 or > 1 ||
                                         region.BBox.Validate().IsFailure))
                {
                    throw new ArgumentException("Invalid layout region geometry, label or confidence.");
                }

                await caller.SubmitAsync(input, Array.AsReadOnly(frozen), cancellationToken).ConfigureAwait(false);
                _pending.Remove(input.Id);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task VerifyCompletedAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_exhausted || _pending.Count != 0)
                {
                    throw new InvalidOperationException("The layout extension did not settle every input.");
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task CloseAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _closed = true;
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
