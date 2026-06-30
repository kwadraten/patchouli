using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Credentials;

public sealed record ProviderCredential(CredentialId CredentialId, LibraryId LibraryId, string ProviderId, string DisplayName, string SecretValue, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record ProviderCredentialMetadata(CredentialId CredentialId, LibraryId LibraryId, string ProviderId, string DisplayName, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record ProviderCredentialBinding(string BindingId, CredentialId CredentialId, OcrPresetId? PresetId, string ProviderId, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public static class ProviderCredentialStatus { public const string Active = "active"; public const string Revoked = "revoked"; public const string Purged = "purged"; }
public static class ProviderCredentialBindingStatus { public const string Active = "active"; public const string CredentialMissing = "credential_missing"; public const string Revoked = "revoked"; }
public static class CredentialStoreShardKind { public const string SensitiveMutable = "sensitive_mutable"; }
public static class ProviderIds { public const string MinerU = "mineru"; }
public interface ICredentialStore
{
    Task<Result<ProviderCredentialMetadata>> SaveCredentialAsync(string providerId, string displayName, string secretValue, CancellationToken cancellationToken = default);
    Task<Result<ProviderCredentialMetadata>> SaveOrUpdateProviderCredentialAsync(string providerId, string displayName, string secretValue, CancellationToken cancellationToken = default);
    Task<Result<ProviderCredentialMetadata>> GetCredentialMetadataAsync(CredentialId credentialId, CancellationToken cancellationToken = default);
    Task<Result<string>> GetSecretForInternalUseAsync(CredentialId credentialId, CancellationToken cancellationToken = default);
    Task<Result<string>> GetActiveSecretForProviderAsync(string providerId, CancellationToken cancellationToken = default);
    Task<Result<ProviderCredentialBinding>> BindCredentialToPresetAsync(CredentialId credentialId, OcrPresetId presetId, CancellationToken cancellationToken = default);
    Task<Result> RevokeCredentialAsync(CredentialId credentialId, CancellationToken cancellationToken = default);
    Task<Result> EmergencyPurgeCredentialAsync(CredentialId credentialId, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<ProviderCredentialMetadata>>> ListCredentialsAsync(CancellationToken cancellationToken = default);
}
