using Patchouli.Extensions.Contracts;
using Patchouli.Infrastructure.Translation;

namespace Patchouli.Tests;

internal static class TranslationTestExtensionHarness
{
    public const string ExtensionId = "translation.llm";

    public static TranslationExtensionRunner CreateRunner(
        Func<TranslationExtensionContext, IExtensionCapabilities, CancellationToken,
            Task<TranslationExtensionResult>> translate,
        bool available = true,
        ExtensionConfigSnapshot? config = null)
    {
        FakeTranslationExtension extension = new(translate);
        FakeExtensionManager manager = new(extension, available, config: config);
        return new TranslationExtensionRunner(manager, new FakeCapabilities());
    }

    public static TranslationExtensionRunner CreateAcquireCancellationRunner(TaskCompletionSource acquireStarted)
    {
        FakeTranslationExtension extension = new((_, _, _) =>
            Task.FromResult(new TranslationExtensionResult([])));
        FakeExtensionManager manager = new(extension, true, async cancellationToken =>
        {
            acquireStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        });
        return new TranslationExtensionRunner(manager, new FakeCapabilities());
    }

    private sealed class FakeTranslationExtension(
        Func<TranslationExtensionContext, IExtensionCapabilities, CancellationToken,
            Task<TranslationExtensionResult>> translate) : ITranslationExtension
    {
        public ExtensionDescriptor Descriptor { get; } = new(
            ExtensionId, "Test translation", "1", "Controlled test extension");

        public IReadOnlyList<ConfigFieldDescriptor> ConfigFields { get; } = [];

        public Task<TranslationExtensionResult> TranslateAsync(TranslationExtensionContext context,
            IExtensionCapabilities capabilities, CancellationToken cancellationToken = default)
        {
            return translate(context, capabilities, cancellationToken);
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
        public IRetrievalExtension? Retrieval => null;

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeCapabilities : IExtensionCapabilities
    {
        public ILlmCapability Llm { get; } = new UnavailableLlmCapability();
        public IHttpCapability Http { get; } = new UnavailableHttpCapability();

        public IExtensionCapabilities ForRun(ExtensionConfigSnapshot config)
        {
            return this;
        }

        public Task WaitToProceedAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class UnavailableLlmCapability : ILlmCapability
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("This host test extension does not call an LLM.");
        }
    }

    private sealed class UnavailableHttpCapability : IHttpCapability
    {
        public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("This host test extension does not make HTTP requests.");
        }
    }
}
