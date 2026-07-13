using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Credentials;

public sealed record ProviderCredential(
    CredentialId CredentialId,
    string ProviderId,
    string DisplayName,
    string SecretValue,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ProviderCredentialMetadata(
    CredentialId CredentialId,
    string ProviderId,
    string DisplayName,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public static class ProviderCredentialStatus
{
    public const string Active = "active";
    public const string Revoked = "revoked";
    public const string Purged = "purged";
}

public static class ProviderIds
{
    public const string MinerU = "mineru";
    public const string MultimodalLlmOcr = "multimodal-llm-ocr";
}

public interface ICredentialStore
{
    Task<Result<ProviderCredentialMetadata>> SaveAsync(string providerId, string displayName,
        string secretValue, CancellationToken cancellationToken = default);

    Task<Result<string>> GetActiveSecretForProviderAsync(string providerId,
        CancellationToken cancellationToken = default);

    Task<Result> RemoveAsync(string providerId, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<ProviderCredentialMetadata>>> ListAsync(CancellationToken cancellationToken = default);
}
