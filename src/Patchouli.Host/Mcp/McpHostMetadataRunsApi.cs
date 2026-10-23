using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;
using Patchouli.Mcp;

namespace Patchouli.Host.Mcp;

/// <summary>
///     Host-side adapter over the metadata endpoint channel: synchronous lookup/apply/enrich and the
///     volatile enrich_batch task registry. It only maps data and parses item/task identities;
///     validation, source selection, optimistic concurrency and commits stay in the domain services.
/// </summary>
public sealed class McpHostMetadataRunsApi(IMetadataRunService runs) : IMcpMetadataRunsApi
{
    public async Task<Result<McpMetadataProposalProjection>> LookupAsync(string itemId, string scheme, string value,
        CancellationToken cancellationToken = default)
    {
        Result<ItemId> parsed = ParseItemId(itemId);
        if (parsed.IsFailure)
        {
            return Result<McpMetadataProposalProjection>.Failure(parsed.ErrorCode!, parsed.ErrorMessage!);
        }

        Result<MetadataCandidateProposal> proposal =
            await runs.LookupAsync(parsed.Value, scheme, value, cancellationToken).ConfigureAwait(false);
        return proposal.IsFailure
            ? Result<McpMetadataProposalProjection>.Failure(proposal.ErrorCode!, proposal.ErrorMessage!)
            : Result<McpMetadataProposalProjection>.Success(Project(proposal.Value));
    }

    public async Task<Result<McpMetadataApplyProjection>> ApplyAsync(string itemId, string expectedUpdatedAt,
        McpMetadataCandidateDto candidate, IReadOnlyList<McpMetadataAttemptDto> attempts,
        CancellationToken cancellationToken = default)
    {
        Result<ItemId> parsed = ParseItemId(itemId);
        if (parsed.IsFailure)
        {
            return Result<McpMetadataApplyProjection>.Failure(parsed.ErrorCode!, parsed.ErrorMessage!);
        }

        if (!DateTimeOffset.TryParse(expectedUpdatedAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset expected))
        {
            return Result<McpMetadataApplyProjection>.Failure(AppErrorCodes.InvalidArgument,
                "expected_updated_at must be an ISO 8601 timestamp.");
        }

        MetadataCandidateProposal proposal = new(parsed.Value, expected, ToCandidate(candidate),
            attempts.Select(attempt => new MetadataLookupAttempt(attempt.SourceId, attempt.ErrorCode,
                attempt.ErrorMessage)).ToArray());
        Result<MetadataLookupOutcome> outcome = await runs.ApplyAsync(proposal, cancellationToken)
            .ConfigureAwait(false);
        return outcome.IsFailure
            ? Result<McpMetadataApplyProjection>.Failure(outcome.ErrorCode!, outcome.ErrorMessage!)
            : Result<McpMetadataApplyProjection>.Success(Project(outcome.Value));
    }

    public async Task<Result<McpMetadataApplyProjection>> EnrichAsync(string itemId,
        CancellationToken cancellationToken = default)
    {
        Result<ItemId> parsed = ParseItemId(itemId);
        if (parsed.IsFailure)
        {
            return Result<McpMetadataApplyProjection>.Failure(parsed.ErrorCode!, parsed.ErrorMessage!);
        }

        Result<MetadataLookupOutcome> outcome = await runs.EnrichAsync(parsed.Value, cancellationToken)
            .ConfigureAwait(false);
        return outcome.IsFailure
            ? Result<McpMetadataApplyProjection>.Failure(outcome.ErrorCode!, outcome.ErrorMessage!)
            : Result<McpMetadataApplyProjection>.Success(Project(outcome.Value));
    }

    public async Task<Result<McpMetadataTaskStatusProjection>> StartBatchAsync(IReadOnlyList<string> itemIds,
        string? requestId, CancellationToken cancellationToken = default)
    {
        List<ItemId> parsed = [];
        foreach (string itemId in itemIds)
        {
            Result<ItemId> id = ParseItemId(itemId);
            if (id.IsFailure)
            {
                return Result<McpMetadataTaskStatusProjection>.Failure(id.ErrorCode!, id.ErrorMessage!);
            }

            parsed.Add(id.Value);
        }

        Result<MetadataBatchRunStatus> started =
            await runs.StartBatchAsync(parsed, requestId, cancellationToken).ConfigureAwait(false);
        return started.IsFailure
            ? Result<McpMetadataTaskStatusProjection>.Failure(started.ErrorCode!, started.ErrorMessage!)
            : Result<McpMetadataTaskStatusProjection>.Success(Project(started.Value));
    }

    public async Task<Result<IReadOnlyList<McpMetadataTaskStatusProjection>>> ListRunsAsync(
        CancellationToken cancellationToken = default)
    {
        Result<IReadOnlyList<MetadataBatchRunStatus>> listed = await runs.ListAsync(cancellationToken)
            .ConfigureAwait(false);
        return listed.IsFailure
            ? Result<IReadOnlyList<McpMetadataTaskStatusProjection>>.Failure(listed.ErrorCode!, listed.ErrorMessage!)
            : Result<IReadOnlyList<McpMetadataTaskStatusProjection>>.Success(
                listed.Value.Select(Project).ToArray());
    }

    public async Task<Result<McpMetadataTaskStatusProjection>> TryGetRunStatusAsync(string taskId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(taskId, out Guid id))
        {
            return Result<McpMetadataTaskStatusProjection>.Failure(AppErrorCodes.InvalidArgument,
                $"The metadata task id '{taskId}' must be a UUID.");
        }

        Result<MetadataBatchRunStatus> status = await runs.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return status.IsFailure
            ? Result<McpMetadataTaskStatusProjection>.Failure(status.ErrorCode!, status.ErrorMessage!)
            : Result<McpMetadataTaskStatusProjection>.Success(Project(status.Value));
    }

    public async Task<Result<McpMetadataTaskStatusProjection>> CancelAsync(string taskId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(taskId, out Guid id))
        {
            return Result<McpMetadataTaskStatusProjection>.Failure(AppErrorCodes.InvalidArgument,
                $"The metadata task id '{taskId}' must be a UUID.");
        }

        Result<MetadataBatchRunStatus> status = await runs.CancelAsync(id, cancellationToken)
            .ConfigureAwait(false);
        return status.IsFailure
            ? Result<McpMetadataTaskStatusProjection>.Failure(status.ErrorCode!, status.ErrorMessage!)
            : Result<McpMetadataTaskStatusProjection>.Success(Project(status.Value));
    }

    public async Task<Result<McpMetadataTaskStatusProjection>> RetryAsync(string taskId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(taskId, out Guid id))
        {
            return Result<McpMetadataTaskStatusProjection>.Failure(AppErrorCodes.InvalidArgument,
                $"The metadata task id '{taskId}' must be a UUID.");
        }

        Result<MetadataBatchRunStatus> status = await runs.RetryAsync(id, cancellationToken).ConfigureAwait(false);
        return status.IsFailure
            ? Result<McpMetadataTaskStatusProjection>.Failure(status.ErrorCode!, status.ErrorMessage!)
            : Result<McpMetadataTaskStatusProjection>.Success(Project(status.Value));
    }

    private static Result<ItemId> ParseItemId(string itemId)
    {
        return Guid.TryParse(itemId, out Guid id)
            ? Result<ItemId>.Success(new ItemId(id))
            : Result<ItemId>.Failure(AppErrorCodes.InvalidArgument,
                $"The item id '{itemId}' must be a UUID.");
    }

    private static McpMetadataProposalProjection Project(MetadataCandidateProposal proposal)
    {
        return new McpMetadataProposalProjection(
            proposal.ItemId.ToString(),
            proposal.ExpectedUpdatedAt.ToString("O"),
            ToDto(proposal.Candidate),
            proposal.Attempts.Select(attempt => new McpMetadataAttemptDto(attempt.SourceId, attempt.ErrorCode,
                attempt.ErrorMessage)).ToArray());
    }

    private static McpMetadataApplyProjection Project(MetadataLookupOutcome outcome)
    {
        return new McpMetadataApplyProjection(
            outcome.Item.ItemId.ToString(),
            outcome.Candidate.SourceId,
            outcome.AddedIdentifiers
                .Select(identifier => new McpMetadataIdentifierDto(identifier.Scheme, identifier.Value)).ToArray(),
            outcome.Attempts.Select(attempt => new McpMetadataAttemptDto(attempt.SourceId, attempt.ErrorCode,
                attempt.ErrorMessage)).ToArray());
    }

    private static McpMetadataTaskStatusProjection Project(MetadataBatchRunStatus status)
    {
        return new McpMetadataTaskStatusProjection(
            status.TaskId.ToString("D"),
            StateName(status.State),
            status.Total,
            status.Completed,
            status.Succeeded,
            status.Failed,
            status.Items.Select(item => new McpMetadataTaskItemProjection(
                item.ItemId.ToString(),
                ItemStateName(status.State, item),
                item.SourceId,
                item.ErrorCode,
                item.ErrorMessage)).ToArray(),
            status.ErrorCode,
            status.ErrorMessage,
            status.Reused);
    }

    private static string StateName(MetadataRunState state)
    {
        return state switch
        {
            MetadataRunState.Running => "running",
            MetadataRunState.Completed => "completed",
            MetadataRunState.Cancelled => "cancelled",
            MetadataRunState.Failed => "failed",
            _ => "unknown"
        };
    }

    private static string ItemStateName(MetadataRunState taskState, MetadataBatchItemResult item)
    {
        if (item.IsSuccess)
        {
            return "completed";
        }

        return item.ErrorCode == "cancelled" ? "cancelled"
            : taskState == MetadataRunState.Cancelled && item.ErrorCode is null ? "cancelled"
            : "failed";
    }

    private static McpMetadataCandidateDto ToDto(MetadataCandidate candidate)
    {
        return new McpMetadataCandidateDto(
            candidate.SourceId,
            candidate.Title ?? string.Empty,
            candidate.Subtitle,
            candidate.TitleShort,
            candidate.Creators?.Select(creator => new McpMetadataCreatorDto(creator.Role, creator.Family,
                creator.Given, creator.Literal, creator.Suffix, creator.Particles)).ToArray(),
            candidate.Dates?.Select(date => new McpMetadataDateDto(date.Role, date.DateParts, date.Literal,
                date.Circa, date.Season)).ToArray(),
            candidate.PublicationTitle,
            candidate.ContainerTitleShort,
            candidate.CollectionTitle,
            candidate.Publisher,
            candidate.Place,
            candidate.Edition,
            candidate.Genre,
            candidate.Number,
            candidate.ChapterNumber,
            candidate.Volume,
            candidate.Version,
            candidate.Issue,
            candidate.Pages,
            candidate.Language,
            candidate.Status,
            candidate.Note,
            candidate.Abstract,
            candidate.Tags,
            candidate.Identifiers?.Select(identifier =>
                new McpMetadataIdentifierDto(identifier.Scheme, identifier.Value)).ToArray(),
            candidate.SuggestedItemType,
            candidate.TypeConfidence);
    }

    private static MetadataCandidate ToCandidate(McpMetadataCandidateDto dto)
    {
        return new MetadataCandidate(
            dto.SourceId,
            dto.Title,
            dto.Subtitle,
            dto.TitleShort,
            dto.Creators?.Select(creator => new MetadataCreator(creator.Role, creator.Family, creator.Given,
                creator.Literal, creator.Suffix, creator.Particles)).ToArray(),
            dto.Dates?.Select(date => new MetadataDate(date.Role, date.DateParts, date.Literal, date.Circa,
                date.Season)).ToArray(),
            dto.PublicationTitle,
            dto.ContainerTitleShort,
            dto.CollectionTitle,
            dto.Publisher,
            dto.Place,
            dto.Edition,
            dto.Genre,
            dto.Number,
            dto.ChapterNumber,
            dto.Volume,
            dto.Version,
            dto.Issue,
            dto.Pages,
            dto.Language,
            dto.Status,
            dto.Note,
            dto.Abstract,
            dto.Tags,
            dto.Identifiers?.Select(identifier =>
                new MetadataIdentifier(identifier.Scheme, identifier.Value)).ToArray(),
            dto.SuggestedItemType,
            dto.TypeConfidence);
    }
}
