using FluentAssertions;
using Patchouli.Core.Credentials;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Host.Agent;
using Patchouli.Llm;
using Patchouli.Workflows.Scripting;

namespace Patchouli.Tests;

public sealed class AgentLlmClientCacheTests
{
    [Fact]
    public async Task Removing_credentials_after_resolution_fails_even_when_a_client_is_cached()
    {
        MutableCredentialStore credentials = new() { Secret = "temporary-test-key" };
        LlmProviderAgentClientProvider provider = new(LlmAppSettings.Default, credentials);
        ModelSelection selection = new("openai", "test-model");

        Result<ILlmChatClient> first = await provider.TryGetAsync(selection, CancellationToken.None);
        first.IsSuccess.Should().BeTrue();

        credentials.Secret = null;
        Result<ILlmChatClient> afterRemoval = await provider.TryGetAsync(selection, CancellationToken.None);

        afterRemoval.IsFailure.Should().BeTrue();
        afterRemoval.ErrorCode.Should().Be(LlmFailureCodes.AuthFailed);
    }

    private sealed class MutableCredentialStore : ICredentialStore
    {
        public string? Secret { get; set; }

        public Task<Result<ProviderCredentialMetadata>> SaveAsync(string providerId, string displayName,
            string secretValue, CancellationToken cancellationToken = default)
        {
            Secret = secretValue;
            ProviderCredentialMetadata metadata = new(CredentialId.New(), providerId, displayName,
                ProviderCredentialStatus.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            return Task.FromResult(Result<ProviderCredentialMetadata>.Success(metadata));
        }

        public Task<Result<string>> GetActiveSecretForProviderAsync(string providerId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Secret is null
                ? Result<string>.Failure(AppErrorCodes.NotFound, "No credential is configured.")
                : Result<string>.Success(Secret));
        }

        public Task<Result> RemoveAsync(string providerId, CancellationToken cancellationToken = default)
        {
            Secret = null;
            return Task.FromResult(Result.Success());
        }

        public Task<Result<IReadOnlyList<ProviderCredentialMetadata>>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ProviderCredentialMetadata> values = Array.Empty<ProviderCredentialMetadata>();
            return Task.FromResult(Result<IReadOnlyList<ProviderCredentialMetadata>>.Success(values));
        }
    }
}
