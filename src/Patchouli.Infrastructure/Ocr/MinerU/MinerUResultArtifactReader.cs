using System.Text.Json;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Ocr.MinerU;

internal enum MinerUResultArtifactFormat
{
    Middle,
    Layout,
    ContentList
}

internal sealed record MinerUResultArtifact(
    string Name,
    string Json,
    MinerUResultArtifactFormat Format,
    MinerUContentListDocument Document);

internal static class MinerUResultArtifactReader
{
    public static Result<MinerUResultArtifact> Read(MinerUZipReader reader)
    {
        var candidates = reader.GetFileNames()
            .Select(name => new { Name = name, Priority = GetPriority(name) })
            .Where(candidate => candidate.Priority >= 0)
            .OrderBy(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0)
        {
            return Result<MinerUResultArtifact>.Failure("tree_artifact_required",
                "MinerU result has no verifiable tree artifact; full.md cannot be imported as a pseudo box.");
        }

        string[] preferred = candidates.Where(candidate => candidate.Priority == candidates[0].Priority)
            .Select(candidate => candidate.Name).ToArray();
        if (preferred.Length != 1)
        {
            return Result<MinerUResultArtifact>.Failure("artifact_ambiguous",
                $"MinerU result has multiple preferred tree artifacts: {string.Join(", ", preferred)}. " +
                "Select a single document result; no fallback was attempted.");
        }

        string name = preferred[0];
        try
        {
            string json = reader.ReadEntryContent(name);
            using JsonDocument parsed = JsonDocument.Parse(json);
            JsonElement root = parsed.RootElement;
            MinerUResultArtifactFormat format;
            MinerUContentListDocument? document;
            if (root.ValueKind == JsonValueKind.Object &&
                (root.TryGetProperty("schema", out _) || root.TryGetProperty("schema_version", out _)))
            {
                if (!root.TryGetProperty("schema", out JsonElement schema) ||
                    schema.ValueKind != JsonValueKind.String || schema.GetString() != "docvortex.middle" ||
                    !root.TryGetProperty("schema_version", out JsonElement version) ||
                    version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")
                {
                    return Failure("artifact_protocol_unsupported", name,
                        "Expected docvortex.middle schema version 2.0; the declared protocol is unsupported.");
                }

                format = MinerUResultArtifactFormat.Middle;
                document = MinerUMiddleJsonParser.Parse(json);
            }
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("pdf_info", out _))
            {
                format = MinerUResultArtifactFormat.Layout;
                document = MinerULayoutJsonParser.Parse(json);
            }
            else if (root.ValueKind == JsonValueKind.Array ||
                     (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("pages", out _)))
            {
                format = MinerUResultArtifactFormat.ContentList;
                document = new MinerUContentListParser().Parse(json);
            }
            else
            {
                return Failure("artifact_structure_unrecognized", name,
                    "Expected a versioned Middle JSON, legacy pdf_info, or content-list structure.");
            }

            return document is null || document.Pages.Count == 0
                ? Failure("no_content", name, "The tree artifact contains no readable pages.")
                : Result<MinerUResultArtifact>.Success(new MinerUResultArtifact(name, json, format, document));
        }
        catch (JsonException exception)
        {
            return Failure("artifact_json_invalid", name, exception.Message);
        }
        catch (UnsupportedMiddleJsonSemanticsException exception)
        {
            return Failure("unsupported_middle_semantics", name, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Failure("artifact_structure_invalid", name, exception.Message);
        }
        catch (KeyNotFoundException exception)
        {
            return Failure("artifact_structure_invalid", name, exception.Message);
        }
        catch (FormatException exception)
        {
            return Failure("artifact_structure_invalid", name, exception.Message);
        }
        catch (OverflowException exception)
        {
            return Failure("artifact_structure_invalid", name, exception.Message);
        }
    }

    private static Result<MinerUResultArtifact> Failure(string code, string name, string detail)
    {
        return Result<MinerUResultArtifact>.Failure(code,
            $"MinerU tree artifact '{name}' could not be imported: {detail} No fallback was attempted.");
    }

    private static int GetPriority(string path)
    {
        string name = path.Replace('\\', '/').Split('/')[^1];
        // Keep the richest result first. Names discover candidates; their JSON selects the adapter.
        string[] names =
            ["middle_json.json", "layout.json", "middle.json", "content_list_v2.json", "content_list.json"];
        for (int index = 0; index < names.Length; index++)
        {
            if (name.Equals(names[index], StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("_" + names[index], StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }
}
