using System.Globalization;
using System.Text.Json;
using Patchouli.Core.Results;
using Patchouli.Extensions.Contracts;
using Patchouli.Mcp;

namespace Patchouli.Host.Plugins;

public sealed record PluginCatalogArtifact(
    string Platform,
    string Url,
    long Size,
    string Sha256);

public sealed record PluginCatalogEntry(
    string Id,
    string Name,
    string Description,
    string Version,
    string License,
    string Repository,
    string SourceRevision,
    IReadOnlyList<string> RequestedVerbs,
    IReadOnlyList<PluginCatalogArtifact> Artifacts)
{
    public PluginCatalogArtifact? ArtifactFor(string platform)
    {
        return Artifacts.FirstOrDefault(artifact => string.Equals(artifact.Platform, platform,
            StringComparison.Ordinal));
    }
}

public sealed record ExtensionCatalogArtifact(
    string Url,
    long Size,
    string Sha256);

public sealed record ExtensionCatalogEntry(
    string Id,
    string Name,
    string Description,
    string Version,
    ExtensionKind Kind,
    string HostApi,
    string Repository,
    string SourceRevision,
    ExtensionCatalogArtifact Artifact);

public sealed record PluginCatalogIndex(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<PluginCatalogEntry> Plugins,
    IReadOnlyList<ExtensionCatalogEntry> Extensions);

public static class PluginCatalogReader
{
    public const string SupportedSchema = "patchouli.community.index/1";

    private static readonly HashSet<string> ValidVerbs = new(StringComparer.Ordinal)
        { "find", "fetch", "send", "view" };

    public static Result<PluginCatalogIndex> Parse(ReadOnlySpan<byte> content)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(content.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            JsonElement root = document.RootElement;
            RequireObject(root, "index");
            if (RequiredString(root, "schema") != SupportedSchema)
            {
                throw Error("The community index schema is not supported by this host.");
            }

            if (!DateTimeOffset.TryParse(RequiredString(root, "generated_at"), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTimeOffset generatedAt))
            {
                throw Error("The community index generated_at value is invalid.");
            }

            JsonElement plugins = Required(root, "plugins");
            if (plugins.ValueKind != JsonValueKind.Array)
            {
                throw Error("The community index plugins value must be an array.");
            }

            HashSet<string> ids = new(StringComparer.Ordinal);
            List<PluginCatalogEntry> entries = [];
            foreach (JsonElement plugin in plugins.EnumerateArray())
            {
                PluginCatalogEntry entry = ParsePlugin(plugin);
                if (!ids.Add(entry.Id))
                {
                    throw Error($"Duplicate plugin id '{entry.Id}'.");
                }

                entries.Add(entry);
            }

            List<ExtensionCatalogEntry> extensions = [];
            if (root.TryGetProperty("extensions", out JsonElement extensionList))
            {
                if (extensionList.ValueKind != JsonValueKind.Array)
                {
                    throw Error("The community index extensions value must be an array.");
                }

                HashSet<string> extensionIds = new(StringComparer.Ordinal);
                foreach (JsonElement extension in extensionList.EnumerateArray())
                {
                    ExtensionCatalogEntry entry = ParseExtension(extension);
                    if (!extensionIds.Add(entry.Id))
                    {
                        throw Error($"Duplicate extension id '{entry.Id}'.");
                    }

                    extensions.Add(entry);
                }
            }

            return Result<PluginCatalogIndex>.Success(
                new PluginCatalogIndex(generatedAt, entries, extensions));
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException
                                              or OverflowException)
        {
            return Result<PluginCatalogIndex>.Failure(AppErrorCodes.ValidationFailed,
                $"Cannot read the community plugin index: {exception.Message}");
        }
    }

    private static PluginCatalogEntry ParsePlugin(JsonElement plugin)
    {
        RequireObject(plugin, "plugin");
        string id = RequiredString(plugin, "id");
        if (!McpResourceUris.IsValidPluginId(id))
        {
            throw Error($"Plugin id '{id}' is invalid.");
        }

        string version = RequiredString(plugin, "version");
        if (!ExtensionVersion.TryParse(version, out _))
        {
            throw Error($"Plugin '{id}' has invalid SemVer '{version}'.");
        }

        string repository = HttpsUrl(RequiredString(plugin, "repository"), $"plugin '{id}' repository");
        string revision = RequiredString(plugin, "source_revision");
        if (revision.Length is not (40 or 64) || !revision.All(char.IsAsciiHexDigit))
        {
            throw Error($"Plugin '{id}' has an invalid source revision.");
        }

        IReadOnlyList<string> verbs = StringArray(Required(plugin, "requested_verbs"), "requested_verbs");
        if (verbs.Count == 0 || verbs.Any(verb => !ValidVerbs.Contains(verb)) ||
            verbs.Distinct(StringComparer.Ordinal).Count() != verbs.Count)
        {
            throw Error($"Plugin '{id}' requested_verbs must be unique find/fetch/send/view values.");
        }

        if (StringArray(Required(plugin, "library_grants"), "library_grants").Count != 0)
        {
            throw Error($"Plugin '{id}' requests library grants not supported by this host.");
        }

        JsonElement artifacts = Required(plugin, "artifacts");
        if (artifacts.ValueKind != JsonValueKind.Array)
        {
            throw Error($"Plugin '{id}' artifacts must be an array.");
        }

        HashSet<string> platforms = new(StringComparer.Ordinal);
        List<PluginCatalogArtifact> parsedArtifacts = [];
        foreach (JsonElement artifact in artifacts.EnumerateArray())
        {
            RequireObject(artifact, "artifact");
            string platform = RequiredString(artifact, "platform");
            if (!IsPlatform(platform) || !platforms.Add(platform))
            {
                throw Error($"Plugin '{id}' has an invalid or duplicate platform '{platform}'.");
            }

            string url = HttpsUrl(RequiredString(artifact, "url"), $"plugin '{id}' artifact URL");
            if (!Required(artifact, "size").TryGetInt64(out long size) || size <= 0)
            {
                throw Error($"Plugin '{id}' artifact size must be positive.");
            }

            string sha256 = RequiredString(artifact, "sha256").ToLowerInvariant();
            if (sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit))
            {
                throw Error($"Plugin '{id}' artifact SHA-256 is invalid.");
            }

            parsedArtifacts.Add(new PluginCatalogArtifact(platform, url, size, sha256));
        }

        if (parsedArtifacts.Count == 0)
        {
            throw Error($"Plugin '{id}' has no release artifacts.");
        }

        return new PluginCatalogEntry(id, RequiredString(plugin, "name"),
            RequiredString(plugin, "description"), version, RequiredString(plugin, "license"), repository,
            revision.ToLowerInvariant(), verbs, parsedArtifacts);
    }

    private static ExtensionCatalogEntry ParseExtension(JsonElement extension)
    {
        RequireObject(extension, "extension");
        string id = RequiredString(extension, "id");
        if (!System.Text.RegularExpressions.Regex.IsMatch(id,
                "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            throw Error($"Extension id '{id}' is invalid.");
        }

        string version = RequiredString(extension, "version");
        if (!ExtensionVersion.TryParse(version, out _))
        {
            throw Error($"Extension '{id}' has invalid SemVer '{version}'.");
        }

        ExtensionKind kind = RequiredString(extension, "kind") switch
        {
            "translation" => ExtensionKind.Translation,
            "metadata" => ExtensionKind.Metadata,
            "ocr" => ExtensionKind.Ocr,
            "retrieval" => ExtensionKind.Retrieval,
            "pdf-text-layer" => ExtensionKind.PdfTextLayer,
            "layout-recognition" => ExtensionKind.LayoutRecognition,
            _ => throw Error($"Extension '{id}' has an unsupported kind.")
        };
        string hostApi = RequiredString(extension, "host_api");
        if (!ExtensionHostApi.IsSupported(hostApi))
        {
            throw Error($"Extension '{id}' targets an unsupported host API '{hostApi}'.");
        }

        string repository = HttpsUrl(RequiredString(extension, "repository"),
            $"extension '{id}' repository");
        string revision = RequiredString(extension, "source_revision");
        if (revision.Length is not (40 or 64) || !revision.All(char.IsAsciiHexDigit))
        {
            throw Error($"Extension '{id}' has an invalid source revision.");
        }

        JsonElement artifact = Required(extension, "artifact");
        RequireObject(artifact, "artifact");
        string url = HttpsUrl(RequiredString(artifact, "url"), $"extension '{id}' artifact URL");
        if (!Required(artifact, "size").TryGetInt64(out long size) || size <= 0)
        {
            throw Error($"Extension '{id}' artifact size must be positive.");
        }

        string sha256 = RequiredString(artifact, "sha256").ToLowerInvariant();
        if (sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit))
        {
            throw Error($"Extension '{id}' artifact SHA-256 is invalid.");
        }

        return new ExtensionCatalogEntry(id, RequiredString(extension, "name"),
            RequiredString(extension, "description"), version, kind, hostApi, repository,
            revision.ToLowerInvariant(), new ExtensionCatalogArtifact(url, size, sha256));
    }

    private static bool IsPlatform(string value)
    {
        return value is "win-x64" or "win-arm64" or "osx-x64" or "osx-arm64" or "linux-x64" or "linux-arm64";
    }

    private static string HttpsUrl(string value, string field)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw Error($"The {field} must be an HTTPS URL without credentials or a fragment.");
        }

        return uri.AbsoluteUri;
    }

    private static IReadOnlyList<string> StringArray(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Error($"{field} must be an array of strings.");
        }

        return value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
            ? item.GetString()!
            : throw Error($"{field} must contain only strings.")).ToArray();
    }

    private static JsonElement Required(JsonElement value, string name)
    {
        return value.TryGetProperty(name, out JsonElement property)
            ? property
            : throw Error($"Missing required index field '{name}'.");
    }

    private static string RequiredString(JsonElement value, string name)
    {
        JsonElement property = Required(value, name);
        return property.ValueKind == JsonValueKind.String && property.GetString() is { Length: > 0 } text
            ? text
            : throw Error($"Index field '{name}' must be a non-empty string.");
    }

    private static void RequireObject(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Error($"Index {field} must be an object.");
        }
    }

    private static InvalidDataException Error(string message)
    {
        return new InvalidDataException(message);
    }
}
