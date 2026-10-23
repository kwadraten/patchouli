using System.Text.Json;
using System.Collections.Concurrent;
using Patchouli.Core.Bibliography;
using Patchouli.Core.Bibliography.MetadataLookup;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Bibliography.MetadataLookup;

public sealed class MetadataLookupService : IMetadataLookupService
{
    private static readonly ConcurrentDictionary<ItemId, SemaphoreSlim> ItemGates = new();
    private readonly IItemService _items;
    private readonly IMetadataSourceRegistry _sources;
    private readonly IItemTypeInferenceService? _typeInferences;
    private readonly Func<IReadOnlyList<MetadataSourcePreference>> _preferences;

    public MetadataLookupService(
        IItemService items,
        IMetadataSourceRegistry sources,
        IItemTypeInferenceService? typeInferences = null,
        Func<IReadOnlyList<MetadataSourcePreference>>? preferences = null)
    {
        _items = items;
        _sources = sources;
        _typeInferences = typeInferences;
        _preferences = preferences ?? (() => Array.Empty<MetadataSourcePreference>());
    }

    public bool CanLookup(string scheme)
    {
        return IdentifierNormalizer.TryCanonicalizeScheme(scheme, out string canonical)
               && _sources.Resolve(canonical, _preferences()).Count > 0;
    }

    public Task<Result<MetadataLookupOutcome>> LookupAndApplyAsync(
        ItemId itemId,
        ItemIdentifier identifier,
        CancellationToken cancellationToken = default)
    {
        return LookupAndMergeAsync(itemId, identifier.Scheme, identifier.Value, _preferences(), cancellationToken);
    }

    public async Task<Result<MetadataLookupOutcome>> LookupAndMergeAsync(
        ItemId itemId, string scheme, string value,
        IReadOnlyList<MetadataSourcePreference>? preferences = null,
        CancellationToken cancellationToken = default)
    {
        Result<MetadataCandidateProposal> proposal = await LookupCandidateAsync(itemId, scheme, value,
            preferences, cancellationToken);
        return proposal.IsFailure
            ? Result<MetadataLookupOutcome>.Failure(proposal.ErrorCode!, proposal.ErrorMessage!)
            : await ApplyCandidateAsync(proposal.Value, cancellationToken);
    }

    public async Task<Result<MetadataCandidateProposal>> LookupCandidateAsync(ItemId itemId, string scheme,
        string value, IReadOnlyList<MetadataSourcePreference>? preferences = null,
        CancellationToken cancellationToken = default)
    {
        Result<NormalizedIdentifier> identifier = IdentifierNormalizer.Normalize(scheme, value);
        if (identifier.IsFailure)
        {
            return Result<MetadataCandidateProposal>.Failure(identifier.ErrorCode!, identifier.ErrorMessage!);
        }

        Result<ItemMetadata> original = await _items.GetItemAsync(itemId, cancellationToken);
        if (original.IsFailure)
        {
            return Result<MetadataCandidateProposal>.Failure(original.ErrorCode!, original.ErrorMessage!);
        }

        IReadOnlyList<IMetadataSource> providers = _sources.Resolve(identifier.Value.Scheme, preferences);
        if (providers.Count == 0)
        {
            return Result<MetadataCandidateProposal>.Failure(
                MetadataLookupErrorCodes.UnsupportedIdentifier,
                $"No enabled metadata source supports {identifier.Value.Scheme}.");
        }

        List<MetadataLookupAttempt> attempts = new();
        Queue<NormalizedIdentifier> pendingIdentifiers = new();
        HashSet<string> seenLookupIdentifiers = new(StringComparer.OrdinalIgnoreCase);
        List<MetadataIdentifier> discoveredIdentifiers = new();
        pendingIdentifiers.Enqueue(identifier.Value);
        MetadataCandidate? candidate = null;
        while (pendingIdentifiers.Count > 0 && candidate is null)
        {
            NormalizedIdentifier lookupIdentifier = pendingIdentifiers.Dequeue();
            if (!seenLookupIdentifiers.Add(Key(lookupIdentifier)))
            {
                continue;
            }

            providers = _sources.Resolve(lookupIdentifier.Scheme, preferences);
            if (providers.Count == 0)
            {
                continue;
            }

            // Query every enabled provider for this identifier concurrently. The resolved order still
            // decides which success wins, so priority and fallback semantics stay deterministic while
            // network-bound extensions overlap instead of running one after another.
            Result<MetadataCandidate>[] results = await Task.WhenAll(providers.Select(provider =>
                provider.LookupAsync(lookupIdentifier, cancellationToken))).ConfigureAwait(false);
            for (int providerIndex = 0; providerIndex < providers.Count; providerIndex++)
            {
                IMetadataSource provider = providers[providerIndex];
                Result<MetadataCandidate> result = results[providerIndex];
                attempts.Add(result.IsFailure
                    ? new MetadataLookupAttempt(provider.Definition.Id, result.ErrorCode, result.ErrorMessage)
                    : new MetadataLookupAttempt(provider.Definition.Id, null, null));
            }

            for (int providerIndex = 0; providerIndex < providers.Count; providerIndex++)
            {
                Result<MetadataCandidate> result = results[providerIndex];
                if (result.IsFailure)
                {
                    continue;
                }

                foreach (MetadataIdentifier raw in result.Value.Identifiers ?? Array.Empty<MetadataIdentifier>())
                {
                    Result<NormalizedIdentifier> normalized = IdentifierNormalizer.Normalize(raw.Scheme, raw.Value);
                    if (normalized.IsFailure || seenLookupIdentifiers.Contains(Key(normalized.Value)))
                    {
                        continue;
                    }

                    discoveredIdentifiers.Add(new MetadataIdentifier(normalized.Value.Scheme, normalized.Value.Value));
                    pendingIdentifiers.Enqueue(normalized.Value);
                }

                if (!string.IsNullOrWhiteSpace(result.Value.Title))
                {
                    candidate = result.Value with
                    {
                        Identifiers = (result.Value.Identifiers ?? Array.Empty<MetadataIdentifier>())
                        .Concat(discoveredIdentifiers)
                        .ToArray()
                    };
                    break;
                }
            }
        }

        if (candidate is null)
        {
            MetadataLookupAttempt significant =
                attempts.LastOrDefault(attempt => attempt.ErrorCode is not MetadataLookupErrorCodes.NotFound)
                ?? attempts[^1];
            return Result<MetadataCandidateProposal>.Failure(
                significant.ErrorCode ?? MetadataLookupErrorCodes.NotFound,
                significant.ErrorMessage ?? "No metadata source found a matching record.");
        }

        // Edits made while remote sources were being queried must also conflict on application.
        return Result<MetadataCandidateProposal>.Success(new MetadataCandidateProposal(itemId,
            original.Value.UpdatedAt, candidate, attempts.ToArray()));
    }

    public async Task<Result<MetadataLookupOutcome>> ApplyCandidateAsync(MetadataCandidateProposal proposal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        SemaphoreSlim itemGate = ItemGates.GetOrAdd(proposal.ItemId, _ => new SemaphoreSlim(1, 1));
        await itemGate.WaitAsync(cancellationToken);
        try
        {
            Result<ItemMetadata> currentResult = await _items.GetItemAsync(proposal.ItemId, cancellationToken);
            if (currentResult.IsFailure)
            {
                return Result<MetadataLookupOutcome>.Failure(currentResult.ErrorCode!, currentResult.ErrorMessage!);
            }

            ItemMetadata current = currentResult.Value;
            if (current.UpdatedAt != proposal.ExpectedUpdatedAt)
            {
                return Result<MetadataLookupOutcome>.Failure(AppErrorCodes.Conflict,
                    "Item metadata changed after the candidate was fetched. Fetch a new proposal.");
            }

            MetadataCandidate candidate = proposal.Candidate;
            if (string.IsNullOrWhiteSpace(candidate.SourceId) || string.IsNullOrWhiteSpace(candidate.Title))
            {
                return Result<MetadataLookupOutcome>.Failure(AppErrorCodes.ValidationFailed,
                    "A metadata candidate requires its source and title.");
            }

            List<ItemIdentifierInput> identifiers = new();
            HashSet<NormalizedIdentifier> seenIdentifiers = new();
            foreach (MetadataIdentifier raw in candidate.Identifiers ?? Array.Empty<MetadataIdentifier>())
            {
                Result<NormalizedIdentifier> normalized = IdentifierNormalizer.Normalize(raw.Scheme, raw.Value);
                if (normalized.IsFailure)
                {
                    return Result<MetadataLookupOutcome>.Failure(normalized.ErrorCode!, normalized.ErrorMessage!);
                }

                if (seenIdentifiers.Add(normalized.Value))
                {
                    identifiers.Add(new ItemIdentifierInput(normalized.Value.Scheme, normalized.Value.Value,
                        $"Metadata source: {candidate.SourceId}"));
                }
            }

            UpdateItemRequest update = CreateUpdate(current, candidate) with
            {
                ExpectedUpdatedAt = proposal.ExpectedUpdatedAt
            };
            cancellationToken.ThrowIfCancellationRequested();
            Result<ItemMetadata> applied = await _items.UpdateAndAddIdentifiersAsync(proposal.ItemId, update,
                identifiers, CancellationToken.None);
            if (applied.IsFailure)
            {
                return Result<MetadataLookupOutcome>.Failure(applied.ErrorCode!, applied.ErrorMessage!);
            }

            List<MetadataLookupAttempt> attempts = proposal.Attempts.ToList();
            if (_typeInferences is not null && candidate.TypeConfidence >= 0.9 &&
                !string.IsNullOrWhiteSpace(candidate.SuggestedItemType) &&
                !string.Equals(candidate.SuggestedItemType, current.ItemType, StringComparison.Ordinal))
            {
                Result<ItemTypeInference> suggestion = await _typeInferences.SuggestAsync(proposal.ItemId,
                    candidate.SuggestedItemType, candidate.TypeConfidence, ItemTypeInferenceSources.IdentifierLookup,
                    $"Suggested by {candidate.SourceId}.", CancellationToken.None);
                if (suggestion.IsFailure)
                {
                    attempts.Add(new MetadataLookupAttempt(candidate.SourceId, suggestion.ErrorCode,
                        $"Metadata was committed, but the separate type suggestion failed: {suggestion.ErrorMessage}"));
                }
            }

            HashSet<IdentifierId> previousIdentifiers =
                current.Identifiers.Select(item => item.IdentifierId).ToHashSet();
            ItemIdentifier[] added = applied.Value.Identifiers
                .Where(item => !previousIdentifiers.Contains(item.IdentifierId)).ToArray();
            return Result<MetadataLookupOutcome>.Success(new MetadataLookupOutcome(applied.Value, candidate,
                attempts, added));
        }
        finally
        {
            itemGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Result<MetadataLookupOutcome>> EnrichAsync(ItemId itemId,
        CancellationToken cancellationToken = default)
    {
        Result<ItemMetadata> item = await _items.GetItemAsync(itemId, cancellationToken);
        return item.IsFailure
            ? Result<MetadataLookupOutcome>.Failure(item.ErrorCode!, item.ErrorMessage!)
            : await LookupAndApplyBestAsync(item.Value, cancellationToken);
    }

    public async Task<Result<MetadataBatchResult>> LookupAndApplyBatchAsync(
        IReadOnlyList<ItemId> itemIds,
        IProgress<MetadataBatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ItemId[] unique = itemIds.Distinct().ToArray();
        MetadataBatchItemResult[] results = new MetadataBatchItemResult[unique.Length];
        int completed = 0;
        int succeeded = 0;
        int failed = 0;
        using SemaphoreSlim gate = new(3, 3);

        Task[] tasks = unique.Select((itemId, index) => Task.Run(async () =>
        {
            // Task.WhenAll below observes every worker before the semaphore leaves its using scope.
            // ReSharper disable once AccessToDisposedClosure
            await gate.WaitAsync(cancellationToken);
            try
            {
                Result<ItemMetadata> item = await _items.GetItemAsync(itemId, cancellationToken);
                if (item.IsFailure)
                {
                    results[index] =
                        new MetadataBatchItemResult(itemId, false, null, item.ErrorCode, item.ErrorMessage);
                }
                else
                {
                    Result<MetadataLookupOutcome> outcome =
                        await LookupAndApplyBestAsync(item.Value, cancellationToken);
                    results[index] = outcome.IsSuccess
                        ? new MetadataBatchItemResult(itemId, true, outcome.Value.Candidate.SourceId, null, null)
                        : new MetadataBatchItemResult(itemId, false, null, outcome.ErrorCode, outcome.ErrorMessage);
                }
            }
            finally
            {
                // Task.WhenAll below observes every worker before the semaphore leaves its using scope.
                // ReSharper disable once AccessToDisposedClosure
                gate.Release();
                int done = Interlocked.Increment(ref completed);
                if (results[index]?.IsSuccess == true)
                {
                    Interlocked.Increment(ref succeeded);
                }
                else
                {
                    Interlocked.Increment(ref failed);
                }

                progress?.Report(new MetadataBatchProgress(done, unique.Length, succeeded, failed,
                    results[index]?.ErrorMessage));
            }
        }, cancellationToken)).ToArray();

        await Task.WhenAll(tasks);
        return Result<MetadataBatchResult>.Success(new MetadataBatchResult(succeeded, failed, results));
    }

    internal static UpdateItemRequest CreateUpdate(ItemMetadata item, MetadataCandidate source)
    {
        MetadataCreator[] sourceCreators = (source.Creators ?? Array.Empty<MetadataCreator>())
            .Where(creator => ItemCreatorRoles.Supported.Contains(creator.Role) && HasCreatorName(creator))
            .ToArray();
        HashSet<string> sourceCreatorRoles =
            sourceCreators.Select(creator => creator.Role).ToHashSet(StringComparer.Ordinal);
        ItemCreatorInput[] creators = item.Creators
            .Where(creator => !sourceCreatorRoles.Contains(creator.Role))
            .Select(ToInput)
            .Concat(sourceCreators.Select(creator => new ItemCreatorInput(
                creator.Role, Clean(creator.Family), Clean(creator.Given), Clean(creator.Literal),
                Clean(creator.Suffix), Clean(creator.Particles))))
            .ToArray();

        MetadataDate[] sourceDates = (source.Dates ?? Array.Empty<MetadataDate>())
            .Where(date => ItemDateRoles.Supported.Contains(date.Role) && HasDate(date))
            .ToArray();
        HashSet<string> sourceDateRoles = sourceDates.Select(date => date.Role).ToHashSet(StringComparer.Ordinal);
        ItemDateInput[] dates = item.Dates
            .Where(date => !sourceDateRoles.Contains(date.Role))
            .Select(date => new ItemDateInput(date.Role, date.DatePartsJson, date.Circa, date.Season, date.Literal))
            .Concat(sourceDates.Select(date => new ItemDateInput(
                date.Role,
                date.DateParts is { Count: > 0 } ? JsonSerializer.Serialize(new[] { date.DateParts }) : "[]",
                date.Circa,
                Clean(date.Season),
                Clean(date.Literal))))
            .ToArray();

        return new UpdateItemRequest(
            source.TypeConfidence >= 0.9 && IsSupportedItemType(source.SuggestedItemType)
                ? source.SuggestedItemType!
                : item.ItemType,
            Pick(source.Title, item.Title)!,
            Pick(source.Subtitle, item.Subtitle),
            Pick(source.TitleShort, item.TitleShort),
            Date: item.Date,
            PublicationTitle: Pick(source.PublicationTitle, item.PublicationTitle),
            ContainerTitleShort: Pick(source.ContainerTitleShort, item.ContainerTitleShort),
            CollectionTitle: Pick(source.CollectionTitle, item.CollectionTitle),
            Publisher: Pick(source.Publisher, item.Publisher),
            Place: Pick(source.Place, item.Place),
            Edition: Pick(source.Edition, item.Edition),
            Genre: Pick(source.Genre, item.Genre),
            Number: Pick(source.Number, item.Number),
            ChapterNumber: Pick(source.ChapterNumber, item.ChapterNumber),
            Volume: Pick(source.Volume, item.Volume),
            Version: Pick(source.Version, item.Version),
            Issue: Pick(source.Issue, item.Issue),
            Pages: Pick(source.Pages, item.Pages),
            Language: Pick(source.Language, item.Language),
            Status: Pick(source.Status, item.Status),
            Note: Pick(source.Note, item.Note),
            AbstractText: Pick(source.Abstract, item.Abstract),
            TagsJson: MergeTags(item.TagsJson, source.Tags),
            CollectionsJson: item.CollectionsJson,
            CustomFieldsJson: item.CustomFieldsJson,
            Creators: creators,
            Dates: dates,
            ExpectedUpdatedAt: item.UpdatedAt);
    }

    private static string MergeTags(string currentJson, IReadOnlyList<string>? incoming)
    {
        HashSet<string> tags = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string tag in JsonSerializer.Deserialize<string[]>(currentJson) ?? [])
            {
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    tags.Add(tag.Trim());
                }
            }
        }
        catch (JsonException)
        {
            return currentJson;
        }

        foreach (string tag in incoming ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(tag))
            {
                tags.Add(tag.Trim());
            }
        }

        return JsonSerializer.Serialize(tags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase));
    }

    private static ItemCreatorInput ToInput(ItemCreator creator)
    {
        return new ItemCreatorInput(creator.Role, creator.Family, creator.Given, creator.Literal, creator.Suffix,
            creator.Particles);
    }

    private static bool HasCreatorName(MetadataCreator creator)
    {
        return new[] { creator.Family, creator.Given, creator.Literal }.Any(value => !string.IsNullOrWhiteSpace(value));
    }

    private static bool HasDate(MetadataDate date)
    {
        return date.DateParts is { Count: > 0 } || !string.IsNullOrWhiteSpace(date.Literal) ||
               !string.IsNullOrWhiteSpace(date.Season);
    }

    private static string Key(NormalizedIdentifier identifier)
    {
        return $"{identifier.Scheme}\0{identifier.Value}";
    }

    /// <summary>
    ///     Tries the item's identifiers in source-preference order and applies the first successful
    ///     candidate. Shared by the batch loop and the single-item enrich endpoint message so both
    ///     select identifiers identically.
    /// </summary>
    private async Task<Result<MetadataLookupOutcome>> LookupAndApplyBestAsync(ItemMetadata item,
        CancellationToken cancellationToken)
    {
        Result<MetadataLookupOutcome>? outcome = null;
        foreach (ItemIdentifier identifier in OrderIdentifiers(item.Identifiers))
        {
            outcome = await LookupAndApplyAsync(item.ItemId, identifier, cancellationToken);
            if (outcome.IsSuccess)
            {
                return outcome;
            }
        }

        return Result<MetadataLookupOutcome>.Failure(
            outcome?.ErrorCode ?? MetadataLookupErrorCodes.UnsupportedIdentifier,
            outcome?.ErrorMessage ?? "The item has no enabled supported identifier.");
    }

    private IEnumerable<ItemIdentifier> OrderIdentifiers(IReadOnlyList<ItemIdentifier> identifiers)
    {
        Dictionary<string, int> effectivePriorities = _preferences()
            .Select((preference, index) => (preference.SourceId,
                Priority: preference.Priority >= 0 ? preference.Priority : index))
            .ToDictionary(value => value.SourceId, value => value.Priority, StringComparer.OrdinalIgnoreCase);
        return identifiers
            .Select(identifier => (Identifier: identifier,
                Normalized: IdentifierNormalizer.Normalize(identifier.Scheme, identifier.Value)))
            .Where(value => value.Normalized.IsSuccess)
            .Select(value => (value.Identifier,
                Providers: _sources.Resolve(value.Normalized.Value.Scheme, _preferences())))
            .Where(value => value.Providers.Count > 0)
            .OrderBy(value => effectivePriorities.TryGetValue(value.Providers[0].Definition.Id, out int priority)
                ? priority
                : value.Providers[0].Definition.DefaultPriority)
            .Select(value => value.Identifier);
    }

    private static string? Pick(string? candidate, string? current)
    {
        return Clean(candidate) ?? current;
    }

    private static string? Clean(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool IsSupportedItemType(string? value)
    {
        return value is not null &&
               !string.Equals(value, "general", StringComparison.Ordinal) &&
               CslItemTypeDisplayNames.Names.ContainsKey(value);
    }
}
