using System.Text.Json.Serialization;

namespace Patchouli.Mcp;

/// <summary>Call outcome, independent of the state of any background run being read or controlled.</summary>
public abstract record McpResponseMeta
{
    [JsonPropertyName("outcome")] public string Outcome { get; init; } = "success";

    [JsonPropertyName("summary")] public string Summary { get; init; } = string.Empty;
}

internal static class McpResponseSummary
{
    public static string Describe<TEntry>(McpResponseMeta meta, IReadOnlyList<TEntry> entries, string? continuation)
    {
        if (entries.Count == 0 && meta is not (McpFindMeta or McpFetchMeta))
        {
            return "No results returned.";
        }

        return meta switch
        {
            McpFindMeta find => $"Returned {entries.Count} entries; {find.FilteredTotal} match the scope and filters." +
                                (continuation is null ? " No further results." : " Continue with continuation."),
            McpFetchMeta => $"Read {entries.Count} requested resources or ranges.",
            McpCiteMeta cite => $"Rendered {entries.Count} references." +
                                (cite.Bibliography is null
                                    ? string.Empty
                                    : " Generated the deduplicated bibliography."),
            McpPutMeta => DescribePut(entries.OfType<McpPutResult>().Single()),
            McpAddMeta => DescribeAdd(entries.OfType<McpAddResult>().Single()),
            McpActionSendMeta send => DescribeSend(send, entries.OfType<McpActionSendResult>().Single()),
            _ => "Completed the request."
        };
    }

    private static string DescribePut(McpPutResult result)
    {
        string operation = result.ResourceType switch
        {
            "item_yaml" or "item_bib" => "Saved the item metadata",
            "csl_style" => "Replaced the CSL style",
            "document_page_map" => "Replaced the chapter and pagination map",
            "text_page" => "Committed the source page payloads",
            "translation_page" => "Saved the page translation",
            _ => "Saved the resource"
        };
        return $"{operation} at {result.Uri}." + (result.ModifiedBoxIds is null
            ? string.Empty
            : $" Wrote {result.ModifiedBoxIds.Count} box payloads.");
    }

    private static string DescribeAdd(McpAddResult result)
    {
        string operation = result.Reused ? "Reused the existing" : "Imported and committed the";
        if (result.Kind == "csl")
        {
            return $"{operation} CSL style at {result.StyleUri}. Installation: {result.ImportStatus}.";
        }

        return $"{operation} PDF item at {result.ItemUri}; document: {result.TextUri}. " +
               $"Import: {result.ImportStatus}; body: {result.BodyStatus}. Use the returned URIs for subsequent operations.";
    }

    private static string DescribeSend(McpActionSendMeta meta, McpActionSendResult result)
    {
        if (meta.Action == "cancel")
        {
            return
                "Processed the cancellation request. Check the returned task state; this receipt does not confirm worker termination. Already committed data is retained.";
        }

        if (meta.State == "started")
        {
            bool reused = result.Result switch
            {
                McpOcrResult ocr => ocr.Reused,
                McpTranslationActionResult translation => translation.Reused,
                McpMetadataBatchActionResult metadata => metadata.Reused,
                _ => false
            };
            return (reused ? "Reused the existing task receipt." : "Accepted the background task.") +
                   $" Read {result.RunUri} for its outcome; acceptance does not mean completion.";
        }

        return meta.Action switch
        {
            "lookup" => "Retrieved a metadata candidate. The item has not been modified.",
            "apply" or "enrich" when result.Result is McpMetadataApplyProjection applied =>
                $"Committed metadata to patchouli://items/{applied.ItemId}.yml from {applied.SourceId}; added {applied.AddedIdentifiers.Count} identifiers.",
            _ => $"Completed {meta.Action}."
        };
    }
}
