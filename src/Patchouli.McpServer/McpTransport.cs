using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Database;
using Patchouli.Mcp;

namespace Patchouli.McpServer;

public static class McpOutputSanitizer
{
    private static readonly Regex Path = new(@"(?:file://\S+|[A-Za-z]:[\\/][^\s\""']+|/[^\s\""']+)", RegexOptions.Compiled);
    private static readonly Regex Secret = new(@"(?i)(?:api[_-]?key|provider[_-]?secret|secret|token|sk-[A-Za-z0-9_-]+)\s*[:=_-]*\s*[A-Za-z0-9_-]+", RegexOptions.Compiled);
    private static readonly Regex SensitiveToken = new(@"(?i)(?:cache[/\\]|page-renders|manifest\.json|model_path|[/\\]models[/\\]|tesseract|staging)", RegexOptions.Compiled);
    public static string Sanitize(string value) => SensitiveToken.Replace(Secret.Replace(Path.Replace(value, "[redacted]"), "[redacted]"), "[redacted]");
    public static bool IsSafe(string value) => !Path.IsMatch(value) && !Secret.IsMatch(value) && !SensitiveToken.IsMatch(value);
}

public sealed class McpProtocolHandler
{
    private readonly IMcpReadApi _api; private readonly SqliteConnectionFactory _db;
    public McpProtocolHandler(IMcpReadApi api, SqliteConnectionFactory db) { _api = api; _db = db; }
    public async Task<string> HandleAsync(string line, CancellationToken ct = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(line); var root = doc.RootElement; var id = root.TryGetProperty("id", out var i) ? i.GetRawText() : "null"; var method = root.GetProperty("method").GetString(); var pars = root.TryGetProperty("params", out var p) ? p : default;
            object result = method switch
            {
                "initialize" => new { protocolVersion = "2024-11-05", serverInfo = new { name = "Patchouli", version = Patchouli.Core.BuildInfo.Version }, capabilities = new { tools = new { } } },
                "tools/list" => new { tools = Tools() },
                "tools/call" => await CallAsync(pars, ct),
                "shutdown" => new { },
                _ => throw new InvalidOperationException("Unknown MCP method.")
            };
            var json = $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{JsonSerializer.Serialize(result)}}}";
            return McpOutputSanitizer.Sanitize(json);
        }
        catch (Exception ex) { return "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32602,\"message\":\"" + McpOutputSanitizer.Sanitize(ex.Message) + "\"}}"; }
    }
    private static object[] Tools() => [
        new { name="search_library", description="Read-only full text search.", inputSchema=new { type="object", required=new[]{"query"} } },
        new { name="get_item_metadata", description="Read-only item metadata.", inputSchema=new { type="object", required=new[]{"item_id"} } },
        new { name="get_document_status", description="Read-only document status.", inputSchema=new { type="object", required=new[]{"document_instance_id"} } },
        new { name="get_page_text", description="Read-only page text.", inputSchema=new { type="object", required=new[]{"document_instance_id","page_number"} } },
        new { name="get_page_blocks", description="Read-only page blocks.", inputSchema=new { type="object", required=new[]{"document_instance_id","page_number"} } },
        new { name="get_search_result_context", description="Read-only search context.", inputSchema=new { type="object", required=new[]{"search_unit_id"} } }];
    private async Task<object> CallAsync(JsonElement parameters, CancellationToken ct)
    {
        var name = parameters.GetProperty("name").GetString() ?? throw new InvalidOperationException("Tool name is required."); var a = parameters.TryGetProperty("arguments", out var args) ? args : default;
        Result<object> r = name switch
        {
            "search_library" => await SearchAsync(a, ct),
            "get_item_metadata" => Wrap(await _api.GetItemMetadataAsync(ItemId.Parse(a.GetProperty("item_id").GetString()!), ct)),
            "get_document_status" => Wrap(await _api.GetDocumentStatusAsync(DocumentInstanceId.Parse(a.GetProperty("document_instance_id").GetString()!), ct)),
            "get_page_text" => await PageTextAsync(a, ct),
            "get_page_blocks" => await PageBlocksAsync(a, ct),
            "get_search_result_context" => Wrap(await _api.GetSearchResultContextAsync(new McpSearchContextRequest(SearchUnitId.Parse(a.GetProperty("search_unit_id").GetString()!)), ct)),
            _ => Result<object>.Failure("unknown_tool", "Unknown tool.")
        };
        return r.IsSuccess ? new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(r.Value) } } } : new { isError = true, content = new[] { new { type = "text", text = $"{r.ErrorCode}: {r.ErrorMessage}" } } };
    }
    private async Task<Result<object>> SearchAsync(JsonElement a, CancellationToken ct) { var q=a.GetProperty("query").GetString()??"";var req=new McpSearchLibraryRequest(q,a.TryGetProperty("limit",out var l)?l.GetInt32():10,a.TryGetProperty("cursor",out var c)?c.GetString():null,null,a.TryGetProperty("include_evidence_refs",out var e)?e.GetBoolean():true,a.TryGetProperty("profile_id",out var p)&&Guid.TryParse(p.GetString(),out var pid)?new SearchProfileId(pid):null,a.TryGetProperty("profile_alias",out var al)?al.GetString():null,a.TryGetProperty("include_rewrite_plan",out var rp)?rp.GetBoolean():true);return Wrap(await _api.SearchLibraryAsync(req,ct)); }
    private async Task<Result<object>> PageTextAsync(JsonElement a,CancellationToken ct){var page=await PageAsync(a,ct);if(page.IsFailure)return Result<object>.Failure(page.ErrorCode!,page.ErrorMessage!);return Wrap(await _api.GetPageTextAsync(new McpPageTextRequest(page.Value,a.TryGetProperty("mode",out var m)?m.GetString()??McpReadMode.Current:McpReadMode.Current,a.TryGetProperty("evidence_ref",out var e)?e.GetString():null),ct));}
    private async Task<Result<object>> PageBlocksAsync(JsonElement a,CancellationToken ct){var page=await PageAsync(a,ct);if(page.IsFailure)return Result<object>.Failure(page.ErrorCode!,page.ErrorMessage!);return Wrap(await _api.GetPageBlocksAsync(new McpPageBlocksRequest(page.Value,IncludeBbox:a.TryGetProperty("include_bbox",out var b)&&b.GetBoolean()),ct));}
    private async Task<Result<PageId>> PageAsync(JsonElement a,CancellationToken ct){var doc=DocumentInstanceId.Parse(a.GetProperty("document_instance_id").GetString()!);var number=a.GetProperty("page_number").GetInt32();await using var c=_db.CreateConnection();await c.OpenAsync(ct);var id=await c.ExecuteScalarAsync<string?>("select page_id from pages where document_instance_id=@D and page_index=@P",new{D=doc.ToString(),P=number});return id is null?Result<PageId>.Failure("not_found","Page was not found."):Result<PageId>.Success(PageId.Parse(id));}
    private static Result<object> Wrap<T>(Result<T> result) => result.IsSuccess ? Result<object>.Success(result.Value!) : Result<object>.Failure(result.ErrorCode!, result.ErrorMessage!);
}
