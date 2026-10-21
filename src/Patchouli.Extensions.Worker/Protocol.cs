using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Extensions.Worker;

internal sealed record InitializeRequest(
    string WorkerInstanceId,
    string Generation,
    string RuntimeDirectory,
    string ExtensionId,
    IReadOnlyDictionary<string, string> Sources,
    string Fingerprint);

internal sealed record InitializeResult(
    ExtensionDescriptor Descriptor,
    IReadOnlyList<ConfigFieldDescriptor> ConfigFields,
    MetadataSourceWire? MetadataSource,
    bool HasDerivedState);

internal sealed record MetadataSourceWire(
    string Id,
    string DisplayName,
    string[] SupportedSchemes,
    int DefaultPriority,
    bool DefaultEnabled)
{
    internal static MetadataSourceWire From(MetadataSourceDefinition definition)
    {
        return new MetadataSourceWire(definition.Id, definition.DisplayName,
            definition.SupportedSchemes.ToArray(), definition.DefaultPriority, definition.DefaultEnabled);
    }

    internal MetadataSourceDefinition ToDefinition()
    {
        return new MetadataSourceDefinition(Id, DisplayName, SupportedSchemes.ToHashSet(StringComparer.Ordinal),
            DefaultPriority,
            DefaultEnabled);
    }
}

internal sealed record InvocationIdentity(
    string WorkerInstanceId,
    string Generation,
    string RunId,
    string InvocationId);

internal sealed record InvocationRequest(
    InvocationIdentity Identity,
    System.Text.Json.JsonElement Context,
    ExtensionConfigSnapshot Config,
    string CapabilitiesFingerprint);

internal sealed record CapabilityCall(
    InvocationIdentity Identity,
    string Slot,
    string Operation,
    System.Text.Json.JsonElement Request);

internal sealed record ProgressCall(InvocationIdentity Identity, ExtensionProgress Progress);

internal sealed record MetadataResultWire(MetadataCandidate? Candidate, string? ErrorCode, string? ErrorMessage);
