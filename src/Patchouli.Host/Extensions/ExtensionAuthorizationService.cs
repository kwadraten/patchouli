using System.Text.Json;
using Patchouli.Core.Diagnostics;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Host.Extensions;

public sealed record ExtensionNetworkGrant(bool Http = false, bool Llm = false);

public sealed class ExtensionAuthorizationException(string message) : InvalidOperationException(message);

/// <summary>Host policy is separate from a script's capability declarations and configuration.</summary>
public sealed class ExtensionAuthorizationService
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly IReadOnlySet<string> _bundledIds;
    private Dictionary<string, ExtensionNetworkGrant> _grants = new(StringComparer.Ordinal);

    public ExtensionAuthorizationService(string runtimeDirectory, IEnumerable<string> bundledIds)
    {
        _path = Path.Combine(runtimeDirectory, "capability-grants.json");
        _bundledIds = bundledIds.ToHashSet(StringComparer.Ordinal);
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            _grants = JsonSerializer.Deserialize<Dictionary<string, ExtensionNetworkGrant>>(File.ReadAllText(_path))
                      ?? new Dictionary<string, ExtensionNetworkGrant>(StringComparer.Ordinal);
        }
        catch (JsonException exception)
        {
            UnexpectedExceptionReporter.ReportCatch(exception, "host.extensions.grants.read");
            // A corrupt grant file never creates authorization for custom code.
        }
    }

    public ExtensionNetworkGrant Get(string id)
    {
        lock (_gate)
        {
            return _grants.GetValueOrDefault(id) ??
                   (_bundledIds.Contains(id) ? new ExtensionNetworkGrant(true, true) : new ExtensionNetworkGrant());
        }
    }

    public void Save(string id, ExtensionNetworkGrant grant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            Dictionary<string, ExtensionNetworkGrant> next = new(_grants, StringComparer.Ordinal) { [id] = grant };
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temporary = _path + ".pending";
            File.WriteAllText(temporary, JsonSerializer.Serialize(next));
            File.Move(temporary, _path, true);
            _grants = next;
        }
    }

    public IExtensionCapabilities Freeze(IExtensionCapabilities capabilities, ExtensionDescriptor descriptor,
        ExtensionConfigSnapshot config)
    {
        ExtensionNetworkGrant grant = Get(descriptor.Id);
        IExtensionCapabilities frozen = capabilities.ForRun(config);
        foreach (ExtensionCapabilityRequest request in descriptor.CapabilityRequests)
        {
            if ((request.Kind == ExtensionCapabilityKind.Http && !grant.Http) ||
                (request.Kind == ExtensionCapabilityKind.Llm && !grant.Llm))
            {
                throw new ExtensionAuthorizationException(
                    $"Extension '{descriptor.Id}' requires host authorization for {request.Kind}. " +
                    "Grant it in the extension settings before starting a run.");
            }

            if (request.Kind == ExtensionCapabilityKind.Llm)
            {
                ExtensionModelSelection? selection = config.Values.TryGetValue(request.Key, out string? value) &&
                                                     !string.IsNullOrWhiteSpace(value)
                    ? JsonSerializer.Deserialize<ExtensionModelSelection>(value,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    : null;
                frozen.ResolveLlmSlot(request.Key, request.DefaultDomain, selection);
            }
        }

        return new AuthorizedCapabilities(frozen, grant);
    }

    private sealed class AuthorizedCapabilities(IExtensionCapabilities inner, ExtensionNetworkGrant grant)
        : IExtensionCapabilities, ILlmCapability, IHttpCapability
    {
        public ILlmCapability Llm => this;
        public IHttpCapability Http => this;
        public string ConfigurationFingerprint => inner.ConfigurationFingerprint;

        public ILlmCapability ResolveLlm(string? domain, ExtensionModelSelection? selection)
        {
            if (!grant.Llm)
            {
                throw new ExtensionAuthorizationException(
                    "The host has not authorized model access for this extension.");
            }

            return inner.ResolveLlm(domain, selection);
        }

        public IHttpCapability ResolveHttp(string? domain, ExtensionConfigSnapshot config)
        {
            if (!grant.Http)
            {
                throw new ExtensionAuthorizationException(
                    "The host has not authorized HTTP access for this extension.");
            }

            return inner.ResolveHttp(domain, config);
        }

        public ILlmCapability ResolveLlmSlot(string key, string? domain, ExtensionModelSelection? selection)
        {
            if (!grant.Llm)
            {
                throw new ExtensionAuthorizationException(
                    "The host has not authorized model access for this extension.");
            }

            return inner.ResolveLlmSlot(key, domain, selection);
        }

        public IHttpCapability ResolveHttpSlot(string key, string? domain, ExtensionConfigSnapshot config)
        {
            if (!grant.Http)
            {
                throw new ExtensionAuthorizationException(
                    "The host has not authorized HTTP access for this extension.");
            }

            return inner.ResolveHttpSlot(key, domain, config);
        }

        public IExtensionCapabilities ForRun(ExtensionConfigSnapshot config)
        {
            return new AuthorizedCapabilities(inner.ForRun(config), grant);
        }

        public Task WaitToProceedAsync(CancellationToken cancellationToken = default)
        {
            return inner.WaitToProceedAsync(cancellationToken);
        }

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            if (!grant.Llm)
            {
                throw new ExtensionAuthorizationException(
                    "The host has not authorized model access for this extension.");
            }

            return inner.Llm.CompleteAsync(request, cancellationToken);
        }

        public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellationToken = default)
        {
            if (!grant.Http)
            {
                throw new ExtensionAuthorizationException(
                    "The host has not authorized HTTP access for this extension.");
            }

            return inner.Http.SendAsync(request, cancellationToken);
        }
    }
}
