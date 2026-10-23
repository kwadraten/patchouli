using System.Net.Http;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Tests;

/// <summary>Offline HttpClient adapter for extension tests; each fixture owns a fresh message handler.</summary>
internal sealed class TestExtensionResources : IExtensionResources, IDisposable
{
    private readonly HttpMessageHandler _handler;

    public TestExtensionResources(HttpMessageHandler handler, ILlmCapability? llm = null)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        Http = new HttpClient(_handler, false);
        Llm = llm ?? UnavailableLlm.Instance;
    }

    public static TestExtensionResources Unavailable()
    {
        return new TestExtensionResources(new UnavailableHttpHandler());
    }

    public HttpClient Http { get; }
    public ILlmCapability Llm { get; }

    public IExtensionResources ForRun(ExtensionConfigSnapshot config)
    {
        return this;
    }

    public ILlmCapability ResolveLlm(string? defaultDomain, ExtensionModelSelection? selection)
    {
        return Llm;
    }

    public Task WaitToProceedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<string?> GetCredentialAsync(string reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }

    public void Dispose()
    {
        Http.Dispose();
        _handler.Dispose();
    }

    private sealed class UnavailableLlm : ILlmCapability
    {
        public static UnavailableLlm Instance { get; } = new();

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("This extension test does not exercise an LLM provider.");
        }
    }

    private sealed class UnavailableHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException("This extension test does not exercise HTTP.");
        }
    }
}
