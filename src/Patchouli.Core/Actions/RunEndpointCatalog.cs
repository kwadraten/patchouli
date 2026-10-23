using System.Text.Json;

namespace Patchouli.Core.Actions;

/// <summary>Shared message schemas used by the host and the CLI; no execution or transport state.</summary>
public static class RunEndpointCatalog
{
    public static IReadOnlyList<string> Domains { get; } = ["ocr", "translation", "metadata"];

    public static string Uri(string domain)
    {
        return $"patchouli://runs/{domain}/control";
    }

    public static IReadOnlyList<ActionDescriptor> Messages { get; } = CreateMessages();

    public static ActionDescriptor? Find(string endpoint, string action)
    {
        return Messages.FirstOrDefault(value => value.Endpoint == endpoint && value.Id == action);
    }

    private static IReadOnlyList<ActionDescriptor> CreateMessages()
    {
        JsonElement empty = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"object","additionalProperties":false,"properties":{}}""");
        JsonElement ocrResult = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"object","required":["task_id","status_uri","state","engine_id","preset_id","preset_version_id","reused"],"properties":{"task_id":{"type":"string"},"status_uri":{"type":"string"},"state":{"type":"string"},"engine_id":{"type":"string"},"preset_id":{"type":"string"},"preset_version_id":{"type":"string"},"reused":{"type":"boolean"}}}""");
        JsonElement translationResult = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"object","required":["task_id","status_uri","state","total_pages","reused"],"properties":{"task_id":{"type":"string"},"status_uri":{"type":"string"},"state":{"type":"string"},"total_pages":{"type":"integer"},"reused":{"type":"boolean"}}}""");
        JsonElement metadataCandidate = MetadataCandidateSchema();
        JsonElement metadataAttempts = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{"source_id":{"type":"string"},"error_code":{"type":"string"},"error_message":{"type":"string"}}}}""");
        JsonElement proposalResult = MergeSchemaProperty(
            JsonSerializer.Deserialize<JsonElement>(
                """{"type":"object","required":["item_id","expected_updated_at","candidate","attempts"],"properties":{"item_id":{"type":"string"},"expected_updated_at":{"type":"string"},"candidate":{},"attempts":{}}}"""),
            "candidate", metadataCandidate);
        proposalResult = MergeSchemaProperty(proposalResult, "attempts", metadataAttempts);
        JsonElement applyResult = MergeSchemaProperty(
            JsonSerializer.Deserialize<JsonElement>(
                """{"type":"object","required":["item_id","source_id","attempts"],"properties":{"item_id":{"type":"string"},"source_id":{"type":"string"},"added_identifiers":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{"scheme":{"type":"string"},"value":{"type":"string"}},"required":["scheme","value"]}},"attempts":{}}}"""),
            "attempts", metadataAttempts);
        JsonElement lookupInput = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"object","additionalProperties":false,"properties":{"scheme":{"type":"string","minLength":1},"value":{"type":"string","minLength":1}},"required":["scheme","value"]}""");
        JsonElement applyInput = MergeSchemaProperty(
            JsonSerializer.Deserialize<JsonElement>(
                """{"type":"object","additionalProperties":false,"properties":{"expected_updated_at":{"type":"string","minLength":1},"candidate":{},"attempts":{}},"required":["expected_updated_at","candidate","attempts"]}"""),
            "candidate", metadataCandidate);
        JsonElement batchReceipt = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"object","required":["task_id","status_uri","state","total","reused"],"properties":{"task_id":{"type":"string"},"status_uri":{"type":"string"},"state":{"type":"string"},"total":{"type":"integer"},"reused":{"type":"boolean"}}}""");
        JsonElement batchInput = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"object","additionalProperties":false,"properties":{"item_ids":{"type":"array","minItems":1,"items":{"type":"string"}}},"required":["item_ids"]}""");
        JsonElement taskResult = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"object","required":["task_id","state","total","completed","succeeded","failed","items","reused"],"properties":{"task_id":{"type":"string"},"state":{"type":"string"},"total":{"type":"integer"},"completed":{"type":"integer"},"succeeded":{"type":"integer"},"failed":{"type":"integer"},"items":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{"item_id":{"type":"string"},"state":{"type":"string"},"source_id":{"type":"string"},"error_code":{"type":"string"},"error_message":{"type":"string"}},"required":["item_id","state"]}},"error_code":{"type":"string"},"error_message":{"type":"string"},"reused":{"type":"boolean"}}}""");
        return
        [
            new ActionDescriptor(Uri("ocr"), "start", ["Document"], JsonSerializer.Deserialize<JsonElement>(
                    """{"type":"object","additionalProperties":false,"properties":{"pages":{"type":"string","minLength":1},"engine":{"type":"string","minLength":1}}}"""),
                ocrResult, "started", ["ocr", "write_text", "rebuild_index", "engine_network"], ["runs/ocr:send"]),
            new ActionDescriptor(Uri("ocr"), "cancel", ["RunOcrStatus"], empty, ocrResult, "completed", ["cancel_ocr"],
                ["runs/ocr:send"]),
            new ActionDescriptor(Uri("ocr"), "retry", ["RunOcrStatus"], empty, ocrResult, "started",
                ["ocr", "write_text", "rebuild_index", "engine_network"], ["runs/ocr:send"]),
            new ActionDescriptor(Uri("translation"), "start", ["Document"], JsonSerializer.Deserialize<JsonElement>(
                    """{"type":"object","additionalProperties":false,"properties":{"pages":{"type":"string"},"target_language":{"type":"string"},"force":{"type":"boolean"}}}"""),
                translationResult, "started", ["translation", "write_text", "engine_network"],
                ["runs/translation:send"]),
            new ActionDescriptor(Uri("translation"), "cancel", ["RunTranslationStatus"], empty, translationResult,
                "completed", ["cancel_translation"], ["runs/translation:send"]),
            new ActionDescriptor(Uri("metadata"), "lookup", ["Item"], lookupInput, proposalResult, "completed",
                ["metadata_network"], ["runs/metadata:send"]),
            new ActionDescriptor(Uri("metadata"), "apply", ["Item"], applyInput, applyResult, "completed",
                ["metadata_write"], ["runs/metadata:send"]),
            new ActionDescriptor(Uri("metadata"), "enrich", ["Item"], empty, applyResult, "completed",
                ["metadata_network", "metadata_write"], ["runs/metadata:send"]),
            new ActionDescriptor(Uri("metadata"), "enrich_batch", ["ItemsScope"], batchInput, batchReceipt,
                "started", ["metadata_network", "metadata_write"], ["runs/metadata:send"]),
            new ActionDescriptor(Uri("metadata"), "cancel", ["RunMetadataStatus"], empty, taskResult, "completed",
                ["cancel_metadata"], ["runs/metadata:send"]),
            new ActionDescriptor(Uri("metadata"), "retry", ["RunMetadataStatus"], empty, batchReceipt, "started",
                ["metadata_network", "metadata_write"], ["runs/metadata:send"])
        ];
    }

    /// <summary>The unified metadata candidate wire shape shared by lookup results and apply inputs.</summary>
    private static JsonElement MetadataCandidateSchema()
    {
        return JsonSerializer.Deserialize<JsonElement>(
            """{"type":"object","additionalProperties":false,"required":["source_id","title"],"properties":{"source_id":{"type":"string","minLength":1},"title":{"type":"string","minLength":1},"subtitle":{"type":"string"},"title_short":{"type":"string"},"creators":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{"role":{"type":"string"},"family":{"type":"string"},"given":{"type":"string"},"literal":{"type":"string"},"suffix":{"type":"string"},"particles":{"type":"string"}},"required":["role"]}},"dates":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{"role":{"type":"string"},"date_parts":{"type":"array","items":{"type":"integer"}},"literal":{"type":"string"},"circa":{"type":"boolean"},"season":{"type":"string"}},"required":["role"]}},"publication_title":{"type":"string"},"container_title_short":{"type":"string"},"collection_title":{"type":"string"},"publisher":{"type":"string"},"place":{"type":"string"},"edition":{"type":"string"},"genre":{"type":"string"},"number":{"type":"string"},"chapter_number":{"type":"string"},"volume":{"type":"string"},"version":{"type":"string"},"issue":{"type":"string"},"pages":{"type":"string"},"language":{"type":"string"},"status":{"type":"string"},"note":{"type":"string"},"abstract":{"type":"string"},"tags":{"type":"array","items":{"type":"string"}},"identifiers":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{"scheme":{"type":"string"},"value":{"type":"string"}},"required":["scheme","value"]}},"suggested_item_type":{"type":"string"},"type_confidence":{"type":"number"}}}""");
    }

    /// <summary>Replaces one property entry of a schema object without a JSON string round-trip.</summary>
    private static JsonElement MergeSchemaProperty(JsonElement schema, string name, JsonElement value)
    {
        Dictionary<string, JsonElement> properties = schema.GetProperty("properties").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone());
        properties[name] = value.Clone();
        Dictionary<string, JsonElement> merged = schema.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone());
        merged["properties"] = JsonSerializer.SerializeToElement(properties);
        return JsonSerializer.SerializeToElement(merged);
    }
}
