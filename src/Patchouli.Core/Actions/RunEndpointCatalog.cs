using System.Text.Json;

namespace Patchouli.Core.Actions;

/// <summary>Shared message schemas used by the host and the CLI; no execution or transport state.</summary>
public static class RunEndpointCatalog
{
    public static IReadOnlyList<string> Domains { get; } = ["ocr", "translation"];

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
                "completed", ["cancel_translation"], ["runs/translation:send"])
        ];
    }
}
