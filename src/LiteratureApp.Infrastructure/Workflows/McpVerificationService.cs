using Dapper;
using LiteratureApp.Core.Import;
using LiteratureApp.Core.Results;
using LiteratureApp.Infrastructure.Database;
using LiteratureApp.Infrastructure.Search;
using LiteratureApp.Mcp;
using LiteratureApp.Search;

namespace LiteratureApp.Infrastructure.Workflows;

public sealed class McpVerificationService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IMcpReadApi _mcpReadApi;

    public McpVerificationService(SqliteConnectionFactory connectionFactory, IMcpReadApi mcpReadApi)
    {
        _connectionFactory = connectionFactory;
        _mcpReadApi = mcpReadApi;
    }

    public async Task<Result<McpVerificationResult>> VerifyAsync(
        string documentInstanceIdStr, string? searchTerm = null, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(documentInstanceIdStr, out var docGuid))
            return Result<McpVerificationResult>.Failure("invalid_id", "Document instance ID is not a valid GUID.");

        var documentInstanceId = new Core.Ids.DocumentInstanceId(docGuid);

        try
        {
            await using var connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var indexStatus = await connection.ExecuteScalarAsync<string?>(
                "select status from search_index_status where scope_type = 'document_instance' and scope_id = @Id;",
                new { Id = documentInstanceIdStr });

            var unitCount = await connection.ExecuteScalarAsync<int>(
                "select count(1) from search_units where document_instance_id = @Id and status = 'current';",
                new { Id = documentInstanceIdStr });

            string? sampleText = null;
            if (unitCount > 0)
            {
                sampleText = await connection.ExecuteScalarAsync<string?>(
                    "select resolved_text from search_units where document_instance_id = @Id and status = 'current' and length(trim(resolved_text)) > 0 limit 1;",
                    new { Id = documentInstanceIdStr });
            }

            if (string.IsNullOrWhiteSpace(searchTerm) && !string.IsNullOrWhiteSpace(sampleText))
            {
                var words = sampleText.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
                searchTerm = words.Length > 0 ? words[0] : null;
            }

            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                var searchResult = await _mcpReadApi.SearchLibraryAsync(
                    new McpSearchLibraryRequest(searchTerm, DocumentInstanceId: documentInstanceId, PageSize: 5),
                    cancellationToken);

                if (searchResult.IsSuccess && searchResult.Value.Results.Count > 0)
                {
                    var total = searchResult.Value.Results.Sum(r => r.MatchedUnits.Count);
                    var firstMatchText = searchResult.Value.Results
                        .SelectMany(r => r.MatchedUnits)
                        .FirstOrDefault()?.Text;

                    return Result<McpVerificationResult>.Success(new McpVerificationResult(
                        true,
                        indexStatus ?? "unknown",
                        total,
                        TruncateSample(firstMatchText ?? sampleText),
                        null));
                }
            }

            return Result<McpVerificationResult>.Success(new McpVerificationResult(
                unitCount > 0,
                indexStatus ?? "unknown",
                unitCount,
                TruncateSample(sampleText),
                unitCount > 0 ? "Search units exist but no matching terms found." : null));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Result<McpVerificationResult>.Failure(AppErrorCodes.DatabaseError,
                $"MCP verification failed: {ex.Message}");
        }
    }

    private static string? TruncateSample(string? text, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }
}
