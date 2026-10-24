using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Translation;
using Patchouli.Core.Translation;

namespace Patchouli.Tests;

/// <summary>
/// Builds a real <see cref="TranslationExtensionRunner"/> whose extension is a typed fake. The fake speaks only
/// the task-level contract: it pulls pages with <see cref="ITranslationJob.NextPageAsync"/>, answers for the page,
/// and commits each answer through the real host job, so the engine, the host job and the SQLite write path all
/// stay in the exercised path.
/// </summary>
/// <remarks>
/// R9 boundary: this fake is for generic host contracts only — job page pull/commit, submission, status
/// mapping and run cancellation. It must never stand in for engine-owned protocol behaviour (HTTP
/// method/body, LLM prompt packing, per-box ordering, error mapping, timeout/retry policy, derived caches),
/// which stays on the real built-in scripts in BuiltInTranslationExtensionProtocolTests and
/// TranslationJobWorkflowTests. Do not re-add the per-page compatibility entry removed by ADR 0051.
/// </remarks>
internal static class TranslationTestExtensionHarness
{
    public const string ExtensionId = TranslationSettings.DefaultEngineId;

    /// <summary>
    /// Creates a runner whose extension translates each handed-out page with <paramref name="translate"/> and
    /// commits the returned outcomes through the job until no page remains.
    /// </summary>
    public static TranslationExtensionRunner CreateRunner(
        Func<TranslationPageInput, IExtensionResources, CancellationToken,
            Task<IReadOnlyList<ExtensionBoxOutcome>>> translate,
        bool available = true,
        ExtensionConfigSnapshot? config = null)
    {
        return CreateRunner(async (job, resources, cancellationToken) =>
        {
            while (await job.NextPageAsync(cancellationToken).ConfigureAwait(false) is { } page)
            {
                IReadOnlyList<ExtensionBoxOutcome> outcomes =
                    await translate(page, resources, cancellationToken).ConfigureAwait(false);
                await job.CommitPageAsync(new TranslationPageResult(page.Page, outcomes), cancellationToken)
                    .ConfigureAwait(false);
            }
        }, available, config);
    }

    /// <summary>
    /// Creates a runner that hands the raw <see cref="ITranslationJob"/> to the test, for early returns,
    /// conflicts and any page order the test wants to exercise.
    /// </summary>
    public static TranslationExtensionRunner CreateRunner(
        Func<ITranslationJob, IExtensionResources, CancellationToken, Task> run,
        bool available,
        ExtensionConfigSnapshot? config)
    {
        ArgumentNullException.ThrowIfNull(run);
        FakeTranslationExtension extension = new(run);
        FakeExtensionManager manager = new(extension, available, config: config);
        return new TranslationExtensionRunner(manager, TestExtensionResources.Unavailable());
    }

    public static TranslationExtensionRunner CreateAcquireCancellationRunner(TaskCompletionSource acquireStarted)
    {
        FakeTranslationExtension extension = new(static (_, _, _) => Task.CompletedTask);
        FakeExtensionManager manager = new(extension, true, async cancellationToken =>
        {
            acquireStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        });
        return new TranslationExtensionRunner(manager, TestExtensionResources.Unavailable());
    }

    private sealed class FakeTranslationExtension(
        Func<ITranslationJob, IExtensionResources, CancellationToken, Task> run) : ITranslationExtension
    {
        public ExtensionDescriptor Descriptor { get; } = new(
            ExtensionId, "Test translation", "1", "Controlled test extension");

        public IReadOnlyList<ConfigFieldDescriptor> ConfigFields { get; } = [];

        public Task RunAsync(ITranslationJob job, IExtensionResources resources,
            CancellationToken cancellationToken = default)
        {
            return run(job, resources, cancellationToken);
        }
    }

    private sealed class FakeExtensionManager(
        FakeTranslationExtension extension,
        bool available,
        Func<CancellationToken, ValueTask<IExtensionLease?>>? acquireOverride = null,
        ExtensionConfigSnapshot? config = null) : IExtensionManager
    {
        public ValueTask<IExtensionLease?> AcquireAsync(string id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (acquireOverride is not null)
            {
                return acquireOverride(cancellationToken);
            }

            return ValueTask.FromResult<IExtensionLease?>(available && id == extension.Descriptor.Id
                ? new FakeExtensionLease(extension, config)
                : null);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public Task<ExtensionCheckResult> CheckAsync(ExtensionPackageSnapshot package,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExtensionCheckResult> LoadAsync(ExtensionPackageSnapshot package,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExtensionCheckResult> ActivateAsync(string id, ExtensionPackageSnapshot package,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task SaveSourceAsync(string id, IReadOnlyDictionary<string, string> sources,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExtensionPackageSnapshot> ReadSourceAsync(string id,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public IReadOnlyList<ExtensionGenerationInfo> List()
        {
            return [];
        }

        public Task SaveConfigAsync(string id, ExtensionConfigSnapshot config,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExtensionConfigSnapshot> ReadConfigAsync(string id,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> RestoreAsync(string id, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> PromoteLastKnownGoodAsync(string id, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> RollbackAsync(string id, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeExtensionLease(FakeTranslationExtension extension, ExtensionConfigSnapshot? config)
        : IExtensionLease
    {
        public ExtensionDescriptor Descriptor => extension.Descriptor;
        public string Fingerprint => "test-extension-generation";

        public ExtensionConfigSnapshot Config { get; } =
            config ?? new ExtensionConfigSnapshot(new Dictionary<string, string>(), 0);

        public IReadOnlyList<ConfigFieldDescriptor> ConfigFields => extension.ConfigFields;
        public IExtensionDefinition Definition => extension;
        public ITranslationExtension Translation => extension;
        public IMetadataExtension? Metadata => null;
        public IOcrExtension? Ocr => null;

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
