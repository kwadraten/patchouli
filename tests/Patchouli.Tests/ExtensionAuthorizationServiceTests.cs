using FluentAssertions;
using Patchouli.Core.Credentials;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Host.Extensions;

namespace Patchouli.Tests;

public sealed class ExtensionAuthorizationServiceTests
{
    [Fact]
    public void Capability_declaration_does_not_grant_custom_extension_access_and_grants_persist_across_restart()
    {
        string runtimeDirectory = CreateRuntimeDirectory();
        try
        {
            ExtensionAuthorizationService first = new(runtimeDirectory, []);
            ExtensionDescriptor descriptor = Descriptor("custom.extension", ExtensionCapabilityKind.Http,
                ExtensionCapabilityKind.Llm);

            first.Get(descriptor.Id).Should().Be(new ExtensionNetworkGrant());
            Action denied = () => first.Freeze(new FakeCapabilities(), descriptor,
                new ExtensionConfigSnapshot(new Dictionary<string, string>(), 1));
            denied.Should().Throw<ExtensionAuthorizationException>();

            first.Save(descriptor.Id, new ExtensionNetworkGrant(true, true));
            ExtensionAuthorizationService reloaded = new(runtimeDirectory, []);

            reloaded.Get(descriptor.Id).Should().Be(new ExtensionNetworkGrant(true, true));
            reloaded.Freeze(new FakeCapabilities(), descriptor,
                new ExtensionConfigSnapshot(new Dictionary<string, string>(), 2)).Should().NotBeNull();
        }
        finally
        {
            DeleteRuntimeDirectory(runtimeDirectory);
        }
    }

    [Fact]
    public void Explicit_saved_denial_overrides_the_bundled_extension_default_allow_policy()
    {
        string runtimeDirectory = CreateRuntimeDirectory();
        try
        {
            ExtensionAuthorizationService authorization = new(runtimeDirectory, ["translation.llm"]);
            ExtensionDescriptor descriptor = Descriptor("translation.llm", ExtensionCapabilityKind.Llm);

            authorization.Get(descriptor.Id).Should().Be(new ExtensionNetworkGrant(true, true));
            authorization.Save(descriptor.Id, new ExtensionNetworkGrant());

            authorization.Get(descriptor.Id).Should().Be(new ExtensionNetworkGrant());
            Action denied = () => authorization.Freeze(new FakeCapabilities(), descriptor,
                new ExtensionConfigSnapshot(new Dictionary<string, string>(), 1));
            denied.Should().Throw<ExtensionAuthorizationException>().WithMessage("*Llm*");
        }
        finally
        {
            DeleteRuntimeDirectory(runtimeDirectory);
        }
    }

    [Fact]
    public async Task A_frozen_run_keeps_its_grant_when_settings_revoke_access_for_future_runs()
    {
        string runtimeDirectory = CreateRuntimeDirectory();
        try
        {
            ExtensionAuthorizationService authorization = new(runtimeDirectory, []);
            ExtensionDescriptor descriptor = Descriptor("custom.http", ExtensionCapabilityKind.Http);
            FakeCapabilities inner = new();
            ExtensionConfigSnapshot config = new(new Dictionary<string, string>(), 1);
            authorization.Save(descriptor.Id, new ExtensionNetworkGrant(true));
            IExtensionCapabilities frozen = authorization.Freeze(inner, descriptor, config);

            authorization.Save(descriptor.Id, new ExtensionNetworkGrant());
            await frozen.Http.SendAsync(new HttpRequest("GET", new Uri("https://unit.test"),
                new Dictionary<string, string>()));

            inner.HttpCalls.Should().Be(1);
            Action futureRun = () => authorization.Freeze(inner, descriptor, config);
            futureRun.Should().Throw<ExtensionAuthorizationException>();
        }
        finally
        {
            DeleteRuntimeDirectory(runtimeDirectory);
        }
    }

    private static ExtensionDescriptor Descriptor(string id, params ExtensionCapabilityKind[] kinds)
    {
        return new ExtensionDescriptor(id, id, "1.0", "")
        {
            CapabilityRequests = kinds.Select((kind, index) => new ExtensionCapabilityRequest(kind, $"cap-{index}"))
                .ToArray()
        };
    }

    private static string CreateRuntimeDirectory()
    {
        return Path.Combine(Path.GetTempPath(), $"patchouli-extension-auth-{Guid.NewGuid():N}");
    }

    private static void DeleteRuntimeDirectory(string runtimeDirectory)
    {
        if (Directory.Exists(runtimeDirectory))
        {
            Directory.Delete(runtimeDirectory, true);
        }
    }

    private sealed class FakeCapabilities : IExtensionCapabilities, ILlmCapability, IHttpCapability
    {
        public int HttpCalls { get; private set; }
        public ILlmCapability Llm => this;
        public IHttpCapability Http => this;

        public IExtensionCapabilities ForRun(ExtensionConfigSnapshot config)
        {
            return this;
        }

        public Task WaitToProceedAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LlmResponse("ok"));
        }

        public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellationToken = default)
        {
            HttpCalls++;
            return Task.FromResult(new HttpResponse(200, new Dictionary<string, string>(), "ok"));
        }
    }
}
