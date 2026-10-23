using Patchouli.Extensions.Contracts;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Patchouli.Extensions;

namespace Patchouli.Host.Extensions;

/// <summary>A read-only view of package metadata and a schema only when its source is trusted.</summary>
public sealed record ExtensionConfigurationDescription(
    string Id,
    ExtensionKind? Kind,
    string Name,
    string Version,
    string Summary,
    string SourceFingerprint,
    string? SchemaFingerprint,
    IReadOnlyList<ConfigFieldDescriptor> Fields,
    IReadOnlyList<ExtensionDiagnostic> Diagnostics,
    string Source)
{
    public bool HasTrustedSchema => SchemaFingerprint is not null;
    public bool SchemaMatchesSource => SchemaFingerprint == SourceFingerprint;
    public string? SchemaHostApi { get; init; }
    public string? ToolchainIdentity { get; init; }
    public string? ToolchainIdentityRawText => ToolchainIdentity;
}

/// <summary>
/// Reads package identity without compiling or activating it. Generated built-in schemas and
/// matching Active generations are the only trusted field sources.
/// </summary>
public static class ExtensionConfigurationDescriptionService
{
    private const string SchemaResourceName = "Patchouli.Infrastructure.Extensions.BuiltInExtensionSchemas.json";
    private static readonly Lazy<BuiltInSchemaIndex?> BuiltInSchemas = new(ReadBuiltInSchemas);

    public static async Task<ExtensionConfigurationDescription> ReadAsync(
        IExtensionManager manager,
        IReadOnlyList<ExtensionPackageSnapshot> bundledPackages,
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(bundledPackages);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        ExtensionPackageSnapshot? bundledPackage = bundledPackages.FirstOrDefault(item => item.Id == id);
        ExtensionPackageSnapshot package;
        try
        {
            package = await manager.ReadSourceAsync(id, cancellationToken).ConfigureAwait(false);
            if (package.Sources.Count == 0 && package.BinaryFiles.Count == 0 && bundledPackage is not null)
            {
                package = bundledPackage;
            }
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or FileNotFoundException)
        {
            package = bundledPackage
                      ?? throw new FileNotFoundException($"Extension '{id}' was not found.");
        }

        ExtensionEntryMetadata? metadata = null;
        List<ExtensionDiagnostic> metadataDiagnostics = [];
        try
        {
            metadata = ExtensionEntryMetadata.Parse(package);
        }
        catch (ExtensionMetadataException error)
        {
            ExtensionDiagnostic diagnostic = new(ExtensionDiagnosticSeverity.Error, error.Code, error.Message,
                new ExtensionSourceLocation(error.FileName, error.Line, error.Column, error.Line, error.Column + 1));
            metadataDiagnostics.Add(diagnostic);
        }

        ExtensionGenerationInfo? activeGeneration =
            manager.List().FirstOrDefault(item => item.Id == id && item.IsActive);
        if (activeGeneration is not null)
        {
            await using IExtensionLease? lease = await manager.AcquireAsync(id, cancellationToken)
                .ConfigureAwait(false);
            if (lease is not null)
            {
                return new ExtensionConfigurationDescription(id, metadata?.Kind ?? lease.Descriptor.Kind,
                        metadata?.Name ?? lease.Descriptor.Name, metadata?.Version ?? lease.Descriptor.Version,
                        metadata?.Summary ?? lease.Descriptor.Description, package.Fingerprint, lease.Fingerprint,
                        lease.ConfigFields, metadataDiagnostics,
                        lease.Fingerprint == package.Fingerprint ? "active" : "active (working source differs)")
                    { SchemaHostApi = lease.Descriptor.HostApi };
            }
        }

        if (metadata is null)
        {
            return new ExtensionConfigurationDescription(id, null, id, "", "", package.Fingerprint,
                null, [], metadataDiagnostics, "unknown");
        }

        BuiltInSchemaIndex? schemaIndex = BuiltInSchemas.Value;
        BuiltInSchemaPackage? bundledSchema = schemaIndex?.Packages.FirstOrDefault(item =>
            item.Id == id && item.Fingerprint == package.Fingerprint && item.SourceHash == package.Fingerprint);
        if (metadata.HostApi == ExtensionHostApi.Current && schemaIndex is not null &&
            bundledSchema?.Fields is not null && bundledSchema.Diagnostics is not null)
        {
            return new ExtensionConfigurationDescription(id, metadata.Kind, metadata.Name, metadata.Version,
                metadata.Summary, package.Fingerprint, bundledSchema.Fingerprint, bundledSchema.Fields,
                bundledSchema.Diagnostics, "built-in schema")
            {
                SchemaHostApi = ExtensionHostApi.Current,
                ToolchainIdentity = schemaIndex.ToolchainIdentity
            };
        }

        return new ExtensionConfigurationDescription(id, metadata.Kind, metadata.Name, metadata.Version,
            metadata.Summary, package.Fingerprint, null, [], [], "uninspected");
    }

    private static BuiltInSchemaIndex? ReadBuiltInSchemas()
    {
        Assembly assembly = typeof(Patchouli.Infrastructure.Translation.BuiltInTranslationExtensions).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(SchemaResourceName);
        if (stream is null)
        {
            return null;
        }

        try
        {
            JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
            options.Converters.Add(new JsonStringEnumConverter());
            using JsonDocument document = JsonDocument.Parse(stream);
            BuiltInSchemaIndex? index = document.RootElement.Deserialize<BuiltInSchemaIndex>(options);
            if (index is null || string.IsNullOrWhiteSpace(index.ToolchainIdentity) ||
                index.Packages is null ||
                index.SchemaVersion != 2 ||
                !MatchesRuntimeToolchain(index.ToolchainIdentity))
            {
                return null;
            }

            return index;
        }
        catch (JsonException exception)
        {
            System.Diagnostics.Trace.WriteLine(
                $"Built-in extension schemas are unavailable: {exception.GetType().Name}");
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              BadImageFormatException or InvalidOperationException or
                                              KeyNotFoundException)
        {
            System.Diagnostics.Trace.WriteLine(
                $"Built-in extension schema identity is unavailable: {exception.GetType().Name}");
            return null;
        }
    }

    private static bool MatchesRuntimeToolchain(string identity)
    {
        try
        {
            return string.Equals(identity, ExtensionArtifact.runtimeToolchainIdentity(),
                StringComparison.Ordinal);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private sealed record BuiltInSchemaIndex(
        int SchemaVersion,
        string ToolchainIdentity,
        IReadOnlyList<BuiltInSchemaPackage> Packages);

    private sealed record BuiltInSchemaPackage(
        string Id,
        string Fingerprint,
        string SourceHash,
        IReadOnlyList<ConfigFieldDescriptor> Fields,
        IReadOnlyList<ExtensionDiagnostic> Diagnostics);
}
