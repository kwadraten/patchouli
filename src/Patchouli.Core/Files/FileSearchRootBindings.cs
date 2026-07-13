using Patchouli.Core.Ids;

namespace Patchouli.Core.Files;

public sealed record FileSearchRootDefinition(
    FileSearchRootId RootId,
    string DisplayName,
    string Purpose,
    bool Enabled);

public sealed record FileSearchRootDeviceBinding(
    FileSearchRootId RootId,
    string DeviceId,
    string LocalPath,
    string ProviderIdentity,
    string AuthorizationKind,
    byte[]? AuthorizationPayload,
    string Availability);
