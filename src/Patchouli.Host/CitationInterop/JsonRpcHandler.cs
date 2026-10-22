using System.Text.Json;
using System.Text.Json.Nodes;
using Patchouli.Core.CitationInterop;
using Patchouli.Core.Results;

namespace Patchouli.Host.CitationInterop;

/// <summary>
/// The TexPile JSON-RPC subset (issue #14 §7): <c>item.search</c> and
/// <c>item.export</c> over POST /better-bibtex/json-rpc. TexPile omits the
/// request <c>id</c>; unlike strict JSON-RPC notification rules, a request
/// without an id still gets a result envelope (with <c>"id": null</c>),
/// because that is what the BBT endpoint does and what TexPile parses.
/// Errors use JSON-RPC codes: -32700 parse, -32600 invalid request,
/// -32601 method not found, -32602 invalid params, -32000 domain failure
/// (message carries the stable Patchouli error code).
/// </summary>
public sealed class JsonRpcHandler
{
    private readonly CitationInteropService _service;

    public JsonRpcHandler(CitationInteropService service)
    {
        _service = service;
    }

    public async Task<string> HandleAsync(string requestBody, CancellationToken cancellationToken = default)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(requestBody);
        }
        catch (JsonException)
        {
            return Error(null, -32700, "Parse error: the request body is not valid JSON.");
        }

        if (root is not JsonObject request)
        {
            return Error(null, -32600, "Invalid request: the request body must be a JSON object.");
        }

        JsonNode? id = request.TryGetPropertyValue("id", out JsonNode? idNode) ? idNode?.DeepClone() : null;
        string? method = request.TryGetPropertyValue("method", out JsonNode? methodNode) &&
                         methodNode is JsonValue methodValue &&
                         methodValue.TryGetValue(out string? methodText)
            ? methodText
            : null;
        if (string.IsNullOrWhiteSpace(method))
        {
            return Error(id, -32600, "Invalid request: 'method' is required.");
        }

        try
        {
            return method switch
            {
                "item.search" => await HandleSearchAsync(request, id, cancellationToken),
                "item.export" => await HandleExportAsync(request, id, cancellationToken),
                _ => Error(id, -32601,
                    $"Method not found: '{method}' is not part of the supported subset (item.search, item.export).")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Error(id, -32603, $"Internal error ({exception.GetType().Name}).");
        }
    }

    private async Task<string> HandleSearchAsync(JsonObject request, JsonNode? id,
        CancellationToken cancellationToken)
    {
        string query = "";
        if (request.TryGetPropertyValue("params", out JsonNode? paramsNode) && paramsNode is not null)
        {
            if (paramsNode is not JsonArray parameters)
            {
                return Error(id, -32602, "Invalid params: 'params' must be an array.");
            }

            if (parameters.Count > 0 && parameters[0] is not null)
            {
                if (parameters[0] is JsonValue value && value.TryGetValue(out string? text))
                {
                    query = text ?? "";
                }
                else
                {
                    return Error(id, -32602,
                        "Invalid params: item.search accepts a single text query; condition objects are not supported.");
                }
            }
        }

        if (query.Length > CaywCompatibility.MaxSearchQueryLength)
        {
            return Error(id, -32602,
                $"Invalid params: the search query exceeds {CaywCompatibility.MaxSearchQueryLength} characters.");
        }

        Result<IReadOnlyList<CitationItemProjection>> results =
            await _service.SearchAsync(query, cancellationToken);
        return results.IsFailure
            ? Error(id, -32000, $"{results.ErrorCode}: {results.ErrorMessage}")
            : Success(id, CslJsonProjection.ToJsonArray(results.Value));
    }

    private async Task<string> HandleExportAsync(JsonObject request, JsonNode? id,
        CancellationToken cancellationToken)
    {
        if (!request.TryGetPropertyValue("params", out JsonNode? paramsNode) ||
            paramsNode is not JsonArray parameters ||
            parameters.Count < 2)
        {
            return Error(id, -32602, "Invalid params: item.export expects [citekeys, translator].");
        }

        if (parameters[0] is not JsonArray keyNodes)
        {
            return Error(id, -32602, "Invalid params: item.export citekeys must be an array of strings.");
        }

        List<string> keys = new();
        foreach (JsonNode? node in keyNodes)
        {
            if (node is JsonValue value && value.TryGetValue(out string? key) && !string.IsNullOrWhiteSpace(key))
            {
                keys.Add(key);
            }
            else
            {
                return Error(id, -32602, "Invalid params: item.export citekeys must be non-empty strings.");
            }
        }

        if (keys.Count > CaywCompatibility.MaxExportKeys)
        {
            return Error(id, -32602,
                $"Invalid params: at most {CaywCompatibility.MaxExportKeys} citation keys per export.");
        }

        string? translator = parameters[1] is JsonValue translatorValue &&
                             translatorValue.TryGetValue(out string? translatorText)
            ? translatorText
            : null;
        if (string.IsNullOrWhiteSpace(translator))
        {
            return Error(id, -32602, "Invalid params: item.export translator must be a string.");
        }

        Result<string> exported = await _service.ExportAsync(keys, translator, cancellationToken);
        return exported.IsFailure
            ? Error(id, -32000, $"{exported.ErrorCode}: {exported.ErrorMessage}")
            : Success(id, JsonValue.Create(exported.Value));
    }

    private static string Success(JsonNode? id, JsonNode? result)
    {
        JsonObject envelope = new()
        {
            ["jsonrpc"] = "2.0",
            ["result"] = result,
            ["id"] = id
        };
        return envelope.ToJsonString();
    }

    private static string Error(JsonNode? id, int code, string message)
    {
        JsonObject envelope = new()
        {
            ["jsonrpc"] = "2.0",
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            },
            ["id"] = id
        };
        return envelope.ToJsonString();
    }
}
