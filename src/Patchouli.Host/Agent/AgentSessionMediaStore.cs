using System.Text.Json;
using System.Text.Json.Nodes;

namespace Patchouli.Host.Agent;

/// <summary>Issue #11: one image kept in the session-managed persistent media area.</summary>
/// <param name="MediaId">Stable media id; messages reference images by this id, never by path.</param>
/// <param name="MimeType">Always image/png in v1.</param>
/// <param name="ByteSize">Encoded PNG byte size.</param>
/// <param name="Width">Actual image width in pixels.</param>
/// <param name="Height">Actual image height in pixels.</param>
/// <param name="ResourceUri">Canonical resource URI pinned to the actually-used revision.</param>
/// <param name="Revision">Library revision the image was rendered from.</param>
public sealed record AgentSessionImageRef(
    string MediaId,
    string MimeType,
    long ByteSize,
    int Width,
    int Height,
    string ResourceUri,
    string Revision);

/// <summary>Issue #11: a media-store image with its bytes loaded.</summary>
public sealed record AgentSessionImageData(AgentSessionImageRef Ref, byte[] PngBytes);

/// <summary>
/// Issue #11: the session-managed persistent media area for tool-returned images.
/// Images live under <c>&lt;session-directory&gt;/media/</c> as <c>{media-id}.png</c> plus a
/// <c>{media-id}.json</c> metadata sidecar (MIME, size, resource URI, revision — never the
/// base64 bytes, so events and logs never re-copy them). A <c>media-index.json</c> maps
/// effect ids to their media ids. The store is persistent (not an evictable render cache),
/// so retry/resume/context rebuild reload images by media reference. Session media is
/// cleaned up with the session lifecycle: <see cref="AgentSessionStore.Purge"/> deletes the
/// whole session directory, including <c>media/</c>.
/// </summary>
public static class AgentSessionMediaStore
{
    public const string MediaDirectoryName = "media";

    private const string IndexFileName = "media-index.json";

    private static readonly JsonSerializerOptions IndexJson = new() { WriteIndented = false };

    /// <summary>Stores tool images for one effect; returns the media references.</summary>
    public static async Task<IReadOnlyList<AgentSessionImageRef>> StoreToolImagesAsync(
        string sessionDirectory,
        string effectId,
        IReadOnlyList<AgentImageResult> images,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(effectId);
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0)
        {
            return [];
        }

        string mediaDirectory = GetMediaDirectory(sessionDirectory);
        Directory.CreateDirectory(mediaDirectory);
        string safeEffect = Sanitize(effectId);
        List<AgentSessionImageRef> stored = [];
        for (int index = 0; index < images.Count; index++)
        {
            AgentImageResult image = images[index];
            byte[] pngBytes;
            try
            {
                pngBytes = Convert.FromBase64String(image.Base64Data);
            }
            catch (FormatException)
            {
                // Never persist a corrupt image; the tool result keeps its text payload.
                continue;
            }

            string mediaId = $"img-{safeEffect}-{index}";
            string pngPath = Path.Combine(mediaDirectory, mediaId + ".png");
            string sidecarPath = Path.Combine(mediaDirectory, mediaId + ".json");
            var reference = new AgentSessionImageRef(
                mediaId, image.MimeType, pngBytes.Length,
                image.Width, image.Height, image.ResourceUri, image.Revision);
            // Write-then-move inside the media directory so a crash never leaves a half file.
            string tempPath = pngPath + ".tmp";
            await File.WriteAllBytesAsync(tempPath, pngBytes, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, pngPath, overwrite: true);
            await File.WriteAllTextAsync(sidecarPath, JsonSerializer.Serialize(reference, IndexJson),
                cancellationToken).ConfigureAwait(false);
            stored.Add(reference);
        }

        if (stored.Count > 0)
        {
            await UpdateIndexAsync(mediaDirectory, effectId, stored, cancellationToken).ConfigureAwait(false);
        }

        return stored;
    }

    /// <summary>Loads one image by media id; null when the media is missing.</summary>
    public static AgentSessionImageData? TryLoadImage(string sessionDirectory, string mediaId)
    {
        if (string.IsNullOrWhiteSpace(sessionDirectory) || string.IsNullOrWhiteSpace(mediaId))
        {
            return null;
        }

        string mediaDirectory = GetMediaDirectory(sessionDirectory);
        string pngPath = Path.Combine(mediaDirectory, Sanitize(mediaId) + ".png");
        string sidecarPath = Path.Combine(mediaDirectory, Sanitize(mediaId) + ".json");
        if (!File.Exists(pngPath) || !File.Exists(sidecarPath))
        {
            return null;
        }

        try
        {
            AgentSessionImageRef? reference =
                JsonSerializer.Deserialize<AgentSessionImageRef>(File.ReadAllText(sidecarPath));
            if (reference is null || !string.Equals(reference.MediaId, Sanitize(mediaId), StringComparison.Ordinal))
            {
                return null;
            }

            return new AgentSessionImageData(reference, File.ReadAllBytes(pngPath));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Lists the media references stored for one effect id.</summary>
    public static IReadOnlyList<AgentSessionImageRef> ListForEffect(string sessionDirectory, string effectId)
    {
        if (string.IsNullOrWhiteSpace(sessionDirectory) || string.IsNullOrWhiteSpace(effectId))
        {
            return [];
        }

        try
        {
            string indexPath = Path.Combine(GetMediaDirectory(sessionDirectory), IndexFileName);
            if (!File.Exists(indexPath))
            {
                return [];
            }

            JsonObject? index = JsonNode.Parse(File.ReadAllText(indexPath)) as JsonObject;
            JsonArray? refs = index?[effectId] as JsonArray;
            if (refs is null)
            {
                return [];
            }

            List<AgentSessionImageRef> result = [];
            foreach (JsonNode? node in refs)
            {
                AgentSessionImageRef? reference = node.Deserialize<AgentSessionImageRef>(IndexJson);
                if (reference is not null)
                {
                    result.Add(reference);
                }
            }

            return result;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string GetMediaDirectory(string sessionDirectory)
    {
        return Path.Combine(sessionDirectory, MediaDirectoryName);
    }

    private static async Task UpdateIndexAsync(string mediaDirectory, string effectId,
        IReadOnlyList<AgentSessionImageRef> stored, CancellationToken cancellationToken)
    {
        string indexPath = Path.Combine(mediaDirectory, IndexFileName);
        JsonObject index;
        try
        {
            index = File.Exists(indexPath)
                ? JsonNode.Parse(await File.ReadAllTextAsync(indexPath, cancellationToken)
                    .ConfigureAwait(false)) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            index = new JsonObject();
        }

        JsonArray array = [];
        foreach (AgentSessionImageRef reference in stored)
        {
            array.Add(JsonSerializer.SerializeToNode(reference, IndexJson));
        }

        index[effectId] = array;
        string tempPath = indexPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, index.ToJsonString(IndexJson), cancellationToken)
            .ConfigureAwait(false);
        File.Move(tempPath, indexPath, overwrite: true);
    }

    private static string Sanitize(string value)
    {
        return string.Concat(value.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '_'));
    }
}

/// <summary>
/// Issue #11: the payload envelope for a tool result that carries session media images.
/// The recorded event payload stays a plain string (no F# union changes): when a tool
/// returns images, the interpreter wraps the original payload as
/// <c>{"result": &lt;original&gt;, "images": [{media_id, mime_type, ...}]}</c>.
/// The model adapter unwraps it, keeps the original text, and appends one
/// <c>LlmImagePart</c> data-URI part per image — the image bytes travel to the provider as
/// real vision input, never as a path or base64 text. Media ids survive context
/// compression because they live in the recorded payload; a restored session re-resolves
/// them from the persistent media area.
/// </summary>
public static class AgentToolImagePayload
{
    private const string ResultProperty = "result";
    private const string ImagesProperty = "images";

    /// <summary>Wraps the original payload with its stored image references.</summary>
    public static string Wrap(string payload, IReadOnlyList<AgentSessionImageRef> images)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(images);
        JsonNode resultNode;
        try
        {
            resultNode = JsonNode.Parse(payload) ?? JsonValue.Create(payload)!;
        }
        catch (JsonException)
        {
            resultNode = JsonValue.Create(payload)!;
        }

        JsonArray imageArray = [];
        foreach (AgentSessionImageRef image in images)
        {
            imageArray.Add(new JsonObject
            {
                ["media_id"] = image.MediaId,
                ["mime_type"] = image.MimeType,
                ["byte_size"] = image.ByteSize,
                ["width"] = image.Width,
                ["height"] = image.Height,
                ["resource_uri"] = image.ResourceUri,
                ["revision"] = image.Revision
            });
        }

        return new JsonObject
        {
            [ResultProperty] = resultNode,
            [ImagesProperty] = imageArray
        }.ToJsonString();
    }

    /// <summary>
    /// Unwraps a tool result payload. Returns true and the original text plus image
    /// references when the payload carries images; otherwise returns false.
    /// </summary>
    public static bool TryUnwrap(string payload,
        out string resultText, out IReadOnlyList<AgentSessionImageRef> images)
    {
        resultText = payload;
        images = [];
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        JsonObject? wrapper;
        try
        {
            wrapper = JsonNode.Parse(payload) as JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }

        if (wrapper is null || wrapper[ImagesProperty] is not JsonArray imageArray || imageArray.Count == 0)
        {
            return false;
        }

        List<AgentSessionImageRef> refs = [];
        foreach (JsonNode? node in imageArray)
        {
            if (node is not JsonObject image)
            {
                continue;
            }

            string? mediaId = image["media_id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(mediaId))
            {
                continue;
            }

            refs.Add(new AgentSessionImageRef(
                mediaId!,
                image["mime_type"]?.GetValue<string>() ?? "image/png",
                image["byte_size"]?.GetValue<long>() ?? 0,
                image["width"]?.GetValue<int>() ?? 0,
                image["height"]?.GetValue<int>() ?? 0,
                image["resource_uri"]?.GetValue<string>() ?? "",
                image["revision"]?.GetValue<string>() ?? ""));
        }

        if (refs.Count == 0)
        {
            return false;
        }

        JsonNode? resultNode = wrapper[ResultProperty];
        resultText = resultNode is JsonValue value && value.TryGetValue<string>(out string? text)
            ? text
            : resultNode?.ToJsonString() ?? payload;
        images = refs;
        return true;
    }
}
