using System.Security.Cryptography;
using System.Text;
using Patchouli.Extensions.Contracts;

namespace Patchouli.Host.Ocr;

internal sealed record OcrModelObservation(string AssetSet, string? Digest, bool IsComplete)
{
    public static OcrModelObservation Empty { get; } = new(string.Empty, null, true);
}

/// <summary>Observes declared, actually-required local assets; published expected hashes are not observations.</summary>
internal static class OcrModelObserver
{
    private sealed record Asset(string Key, string? Path, long? Bytes, string? ExpectedSha256);

    public static Task<OcrModelObservation> ObserveAsync(OcrEngineDefinition definition,
        IReadOnlyList<ConfigFieldDescriptor> fields, ExtensionConfigSnapshot config, string? scope,
        CancellationToken cancellationToken)
    {
        // Enumerating/statting/hash work must not inherit an admitting UI caller's thread.
        return Task.Run(() => ObserveCoreAsync(definition, fields, config, scope, cancellationToken),
            cancellationToken);
    }

    private static async Task<OcrModelObservation> ObserveCoreAsync(OcrEngineDefinition definition,
        IReadOnlyList<ConfigFieldDescriptor> fields, ExtensionConfigSnapshot config, string? scope,
        CancellationToken cancellationToken)
    {
        try
        {
            Asset[] assets = RequiredAssets(definition, fields, config, scope)
                .OrderBy(asset => asset.Key, StringComparer.Ordinal).ToArray();
            if (assets.Length == 0)
            {
                return OcrModelObservation.Empty;
            }

            string assetSet = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join('\n', assets.Select(asset => asset.Key + '\0' + asset.Path))))).ToLowerInvariant();
            using IncrementalHash aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (Asset asset in assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (asset.Path is null || !File.Exists(asset.Path))
                {
                    return new OcrModelObservation(assetSet, null, false);
                }

                await using FileStream stream = new(asset.Path, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (asset.Bytes is { } bytes && stream.Length != bytes)
                {
                    return new OcrModelObservation(assetSet, null, false);
                }

                byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                if (asset.ExpectedSha256 is { Length: > 0 } expected &&
                    !string.Equals(Convert.ToHexString(hash), expected, StringComparison.OrdinalIgnoreCase))
                {
                    return new OcrModelObservation(assetSet, null, false);
                }

                aggregate.AppendData(Encoding.UTF8.GetBytes(asset.Key + '\0'));
                aggregate.AppendData(hash);
            }

            return new OcrModelObservation(assetSet,
                "sha256:" + Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant(), true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              ArgumentException or NotSupportedException)
        {
            // Readiness reports an actionable failure only if this asset branch is actually used.
            return new OcrModelObservation(string.Empty, null, false);
        }
    }

    private static IEnumerable<Asset> RequiredAssets(OcrEngineDefinition definition,
        IReadOnlyList<ConfigFieldDescriptor> fields, ExtensionConfigSnapshot config, string? scope)
    {
        bool hasManagedDeclarations = fields.Any(field => field.ManagedFiles.Count > 0);
        if (hasManagedDeclarations)
        {
            foreach (ConfigFieldDescriptor field in fields)
            foreach (ExtensionManagedFile file in field.ManagedFiles)
            {
                if (!Matches(file.WhenKey, file.WhenValue, fields, config) ||
                    !Matches(file.RequiredWhenKey, file.RequiredWhenValue, fields, config) ||
                    (file.RequiredScopes is { Count: > 0 } && scope is not null &&
                     !file.RequiredScopes.Contains(scope, StringComparer.OrdinalIgnoreCase)))
                {
                    continue;
                }

                string? path = Value(field.Key, fields, config);
                yield return new Asset(field.Key + '/' + file.RelativePath,
                    string.IsNullOrWhiteSpace(path)
                        ? null
                        : Path.GetFullPath(
                            field.Kind == ConfigValueKind.DirectoryPath
                                ? Path.Combine(path, file.RelativePath.Replace('/', Path.DirectorySeparatorChar))
                                : path), file.ExpectedBytes, file.Sha256);
            }

            yield break;
        }

        // Undeclared private files are deliberately not traversed or guessed by the host.
        // Compatibility metadata may explicitly describe a model directory's constituent files.
        string? root = Value("model-path", fields, config);
        foreach (OcrModelResource resource in definition.ModelResources ?? [])
        {
            yield return new Asset("model-path/" + resource.Path,
                Path.IsPathRooted(resource.Path) ? resource.Path :
                string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(Path.Combine(root, resource.Path)),
                null, resource.Sha256);
        }
    }

    private static bool Matches(string? key, string? expected, IReadOnlyList<ConfigFieldDescriptor> fields,
        ExtensionConfigSnapshot config)
    {
        return key is null || string.Equals(Value(key, fields, config)?.Trim(), expected?.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? Value(string key, IReadOnlyList<ConfigFieldDescriptor> fields,
        ExtensionConfigSnapshot config)
    {
        return config.Values.TryGetValue(key, out string? value)
            ? value
            : fields.FirstOrDefault(field => field.Key == key)?.DefaultValue;
    }
}
