using System.Text.Json.Serialization;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Mcp;

/// <summary>The OCR queue's read-only status projection behind <c>patchouli://runs/ocr/</c>.</summary>
public interface IMcpOcrRunsApi
{
    /// <summary>Every queued or completed OCR task, in a stable order.</summary>
    Task<Result<IReadOnlyList<McpOcrTaskStatusProjection>>> ListTasksAsync(
        CancellationToken cancellationToken = default);

    /// <summary>One queued/completed OCR task's live status, or NotFound when the task id is unknown.</summary>
    Task<Result<McpOcrTaskStatusProjection>> TryGetTaskStatusAsync(string taskId,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     The volatile direct translation runs behind <c>patchouli://runs/translation/</c>. A run belongs
///     to exactly one document, accepts an optional one-based page selector and returns the same
///     per-page outcome projection the host recorded. The run id is the task id.
/// </summary>
public interface IMcpTranslationRunsApi
{
    /// <summary>
    ///     Starts one direct translation run over the requested pages (empty selects the whole
    ///     document) and returns its initial running snapshot.
    /// </summary>
    Task<Result<McpTranslationRunStatusProjection>> StartAsync(McpTranslationRunStartRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Every run this host still holds in memory, newest first.</summary>
    Task<Result<IReadOnlyList<McpTranslationRunStatusProjection>>> ListRunsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>One run's live status, or NotFound when the run id is unknown.</summary>
    Task<Result<McpTranslationRunStatusProjection>> TryGetRunStatusAsync(string taskId,
        CancellationToken cancellationToken = default);

    /// <summary>Cancels a running translation and returns its post-cancel snapshot.</summary>
    Task<Result<McpTranslationRunStatusProjection>> CancelAsync(string taskId,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     One direct translation start request. <see cref="Pages" /> uses the same one-based
///     <c>1,3-5</c> selector as OCR; blank or null selects the whole document.
/// </summary>
/// <param name="DocumentInstanceId">The document to translate.</param>
/// <param name="Pages">Optional one-based page selector; blank selects the whole document.</param>
/// <param name="TargetLanguage">Optional target-language override for this run only.</param>
/// <param name="ForceRetranslate">When true, pages with a current translation are translated again.</param>
/// <param name="RequestId">Optional process-local idempotency key.</param>
public sealed record McpTranslationRunStartRequest(
    DocumentInstanceId DocumentInstanceId,
    string? Pages = null,
    string? TargetLanguage = null,
    bool ForceRetranslate = false,
    string? RequestId = null);

/// <summary>One direct translation run's observable state projected for runs/translation status.</summary>
public sealed record McpTranslationRunStatusProjection(
    [property: JsonPropertyName("task_id")]
    string TaskId,
    [property: JsonPropertyName("document_id")]
    string DocumentId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("completed")]
    int Completed,
    [property: JsonPropertyName("succeeded")]
    int Succeeded,
    [property: JsonPropertyName("partial")]
    int Partial,
    [property: JsonPropertyName("failed")] int Failed,
    [property: JsonPropertyName("skipped")]
    int Skipped,
    [property: JsonPropertyName("cancelled")]
    int Cancelled,
    [property: JsonPropertyName("pages")] IReadOnlyList<McpTranslationPageStatusProjection> Pages,
    [property: JsonPropertyName("error_code")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorCode = null,
    [property: JsonPropertyName("error_message")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorMessage = null,
    [property: JsonPropertyName("reused")] bool Reused = false);

/// <summary>One settled page of a direct translation run.</summary>
public sealed record McpTranslationPageStatusProjection(
    [property: JsonPropertyName("page_index")]
    int PageIndex,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("committed_box_count")]
    int CommittedBoxCount,
    [property: JsonPropertyName("error_code")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorCode = null,
    [property: JsonPropertyName("error_message")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorMessage = null);

/// <summary>The receipt returned by translation endpoint start/cancel messages.</summary>
public sealed record McpTranslationActionResult(
    [property: JsonPropertyName("task_id")]
    string TaskId,
    [property: JsonPropertyName("status_uri")]
    string StatusUri,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("total_pages")]
    int TotalPages,
    [property: JsonPropertyName("reused")] bool Reused = false);

/// <summary>
///     One OCR task's live status projection; OCR keeps its own state vocabulary, distinct from
///     translation runs. The volatile
///     queue <c>state</c> is accompanied by the durable three-phase outcome when a run exists:
///     <c>recognition_state</c> (persisted run state), <c>committed</c>/<c>committed_page_count</c>
///     (candidate adoption), and <c>index_state</c> (search index readiness). A failed task can still
///     report <c>committed: true</c> with <c>index_state: "not_indexed"</c> when recognition and
///     body commit succeeded and only the index rebuild failed.
/// </summary>
public sealed record McpOcrTaskStatusProjection(
    [property: JsonPropertyName("task_id")]
    string TaskId,
    [property: JsonPropertyName("item_title")]
    string ItemTitle,
    [property: JsonPropertyName("task_kind")]
    string TaskKind,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("engine_id")]
    string EngineId,
    [property: JsonPropertyName("page_count")]
    int PageCount,
    [property: JsonPropertyName("page_progress")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    McpOcrPageProgress? PageProgress,
    [property: JsonPropertyName("last_error_code")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? LastErrorCode,
    [property: JsonPropertyName("recognition_state")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? RecognitionState = null,
    [property: JsonPropertyName("committed")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Committed = null,
    [property: JsonPropertyName("committed_page_count")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? CommittedPageCount = null,
    [property: JsonPropertyName("index_state")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? IndexState = null);

public sealed record McpOcrPageProgress(
    [property: JsonPropertyName("succeeded")]
    int Succeeded,
    [property: JsonPropertyName("failed")] int Failed,
    [property: JsonPropertyName("processing")]
    int Processing,
    [property: JsonPropertyName("total")] int Total);

/// <summary>One metadata creator entry in the unified candidate wire shape.</summary>
public sealed record McpMetadataCreatorDto(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("family")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Family = null,
    [property: JsonPropertyName("given")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Given = null,
    [property: JsonPropertyName("literal")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Literal = null,
    [property: JsonPropertyName("suffix")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Suffix = null,
    [property: JsonPropertyName("particles")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Particles = null);

/// <summary>One metadata date entry in the unified candidate wire shape.</summary>
public sealed record McpMetadataDateDto(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("date_parts")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<int>? DateParts = null,
    [property: JsonPropertyName("literal")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Literal = null,
    [property: JsonPropertyName("circa")] bool Circa = false,
    [property: JsonPropertyName("season")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Season = null);

/// <summary>One identifier entry in the unified candidate wire shape.</summary>
public sealed record McpMetadataIdentifierDto(
    [property: JsonPropertyName("scheme")] string Scheme,
    [property: JsonPropertyName("value")] string Value);

/// <summary>The unified metadata candidate shared by lookup results and apply inputs.</summary>
public sealed record McpMetadataCandidateDto(
    [property: JsonPropertyName("source_id")]
    string SourceId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("subtitle")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Subtitle = null,
    [property: JsonPropertyName("title_short")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TitleShort = null,
    [property: JsonPropertyName("creators")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<McpMetadataCreatorDto>? Creators = null,
    [property: JsonPropertyName("dates")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<McpMetadataDateDto>? Dates = null,
    [property: JsonPropertyName("publication_title")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PublicationTitle = null,
    [property: JsonPropertyName("container_title_short")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ContainerTitleShort = null,
    [property: JsonPropertyName("collection_title")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? CollectionTitle = null,
    [property: JsonPropertyName("publisher")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Publisher = null,
    [property: JsonPropertyName("place")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Place = null,
    [property: JsonPropertyName("edition")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Edition = null,
    [property: JsonPropertyName("genre")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Genre = null,
    [property: JsonPropertyName("number")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Number = null,
    [property: JsonPropertyName("chapter_number")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ChapterNumber = null,
    [property: JsonPropertyName("volume")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Volume = null,
    [property: JsonPropertyName("version")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Version = null,
    [property: JsonPropertyName("issue")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Issue = null,
    [property: JsonPropertyName("pages")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Pages = null,
    [property: JsonPropertyName("language")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Language = null,
    [property: JsonPropertyName("status")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Status = null,
    [property: JsonPropertyName("note")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Note = null,
    [property: JsonPropertyName("abstract")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Abstract = null,
    [property: JsonPropertyName("tags")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Tags = null,
    [property: JsonPropertyName("identifiers")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<McpMetadataIdentifierDto>? Identifiers = null,
    [property: JsonPropertyName("suggested_item_type")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SuggestedItemType = null,
    [property: JsonPropertyName("type_confidence")]
    double TypeConfidence = 0);

/// <summary>One source lookup attempt recorded while gathering a metadata candidate.</summary>
public sealed record McpMetadataAttemptDto(
    [property: JsonPropertyName("source_id")]
    string SourceId,
    [property: JsonPropertyName("error_code")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorCode = null,
    [property: JsonPropertyName("error_message")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorMessage = null);

/// <summary>The read-only lookup proposal returned by the metadata endpoint lookup message.</summary>
public sealed record McpMetadataProposalProjection(
    [property: JsonPropertyName("item_id")]
    string ItemId,
    [property: JsonPropertyName("expected_updated_at")]
    string ExpectedUpdatedAt,
    [property: JsonPropertyName("candidate")]
    McpMetadataCandidateDto Candidate,
    [property: JsonPropertyName("attempts")]
    IReadOnlyList<McpMetadataAttemptDto> Attempts);

/// <summary>The committed outcome returned by the metadata endpoint apply/enrich messages.</summary>
public sealed record McpMetadataApplyProjection(
    [property: JsonPropertyName("item_id")]
    string ItemId,
    [property: JsonPropertyName("source_id")]
    string SourceId,
    [property: JsonPropertyName("added_identifiers")]
    IReadOnlyList<McpMetadataIdentifierDto> AddedIdentifiers,
    [property: JsonPropertyName("attempts")]
    IReadOnlyList<McpMetadataAttemptDto> Attempts);

/// <summary>One item's result inside a metadata enrich_batch task projection.</summary>
public sealed record McpMetadataTaskItemProjection(
    [property: JsonPropertyName("item_id")]
    string ItemId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("source_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SourceId = null,
    [property: JsonPropertyName("error_code")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorCode = null,
    [property: JsonPropertyName("error_message")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorMessage = null);

/// <summary>One volatile metadata enrich_batch task's status projection.</summary>
public sealed record McpMetadataTaskStatusProjection(
    [property: JsonPropertyName("task_id")]
    string TaskId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("completed")]
    int Completed,
    [property: JsonPropertyName("succeeded")]
    int Succeeded,
    [property: JsonPropertyName("failed")] int Failed,
    [property: JsonPropertyName("items")] IReadOnlyList<McpMetadataTaskItemProjection> Items,
    [property: JsonPropertyName("error_code")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorCode = null,
    [property: JsonPropertyName("error_message")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorMessage = null,
    [property: JsonPropertyName("reused")] bool Reused = false);

/// <summary>The receipt returned by metadata endpoint enrich_batch/retry messages.</summary>
public sealed record McpMetadataBatchActionResult(
    [property: JsonPropertyName("task_id")]
    string TaskId,
    [property: JsonPropertyName("status_uri")]
    string StatusUri,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("reused")] bool Reused = false);

/// <summary>
///     The volatile metadata enrich_batch tasks and the synchronous lookup/apply/enrich channel behind
///     <c>patchouli://runs/metadata/</c>. Lookup returns a read-only proposal; apply validates the
///     expected item version and commits atomically through the Core write path; enrich is the Core
///     combination of both over the item's own identifiers.
/// </summary>
public interface IMcpMetadataRunsApi
{
    Task<Result<McpMetadataProposalProjection>> LookupAsync(string itemId, string scheme, string value,
        CancellationToken cancellationToken = default);

    Task<Result<McpMetadataApplyProjection>> ApplyAsync(string itemId, string expectedUpdatedAt,
        McpMetadataCandidateDto candidate, IReadOnlyList<McpMetadataAttemptDto> attempts,
        CancellationToken cancellationToken = default);

    Task<Result<McpMetadataApplyProjection>> EnrichAsync(string itemId,
        CancellationToken cancellationToken = default);

    Task<Result<McpMetadataTaskStatusProjection>> StartBatchAsync(IReadOnlyList<string> itemIds, string? requestId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<McpMetadataTaskStatusProjection>>> ListRunsAsync(
        CancellationToken cancellationToken = default);

    Task<Result<McpMetadataTaskStatusProjection>> TryGetRunStatusAsync(string taskId,
        CancellationToken cancellationToken = default);

    Task<Result<McpMetadataTaskStatusProjection>> CancelAsync(string taskId,
        CancellationToken cancellationToken = default);

    Task<Result<McpMetadataTaskStatusProjection>> RetryAsync(string taskId,
        CancellationToken cancellationToken = default);
}
