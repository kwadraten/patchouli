using System.Data.Common;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Library;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.Documents;

public sealed class DocumentTreeService : IDocumentTreeService, IDocumentTreeEditor
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly DocumentTreeValidator _validator;
    private readonly ILibraryRevisionService? _revisions;

    public DocumentTreeService(
        SqliteConnectionFactory connectionFactory,
        IClock clock,
        IMarkdownEngine markdownEngine,
        ILibraryRevisionService? revisions = null)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _validator = new DocumentTreeValidator(markdownEngine);
        _revisions = revisions;
    }

    public async Task<Result> ValidateStoredTreesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            IEnumerable<DocumentTreeRevisionRow> revisions = await connection.QueryAsync<DocumentTreeRevisionRow>(
                SelectRevisionSql + " order by tree_revision_id;");
            foreach (DocumentTreeRevisionRow revisionRow in revisions)
            {
                DocumentTreeRevision revision = revisionRow.ToRevision();
                Result validation = _validator.Validate(
                    revision,
                    await GetBoxesAsync(connection, null, revision.TreeRevisionId));
                if (validation.IsFailure)
                {
                    return validation;
                }
            }

            return Result.Success();
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.document-tree-service"))
        {
            return Result.Failure(AppErrorCodes.ValidationFailed, exception.Message);
        }
    }

    public async Task<Result<DocumentTreeRevision>> CreateStagingRevisionAsync(
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        string source,
        DocumentTreeRevisionId? parentTreeRevisionId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source) || !DocumentTreeRevisionSource.IsKnown(source.Trim()))
        {
            return Failure<DocumentTreeRevision>("Document tree revision source is invalid.");
        }

        return await InTransactionAsync(async (connection, transaction) =>
        {
            Result page = await ValidatePageAsync(connection, transaction, documentInstanceId, pageId);
            if (page.IsFailure)
            {
                return Result<DocumentTreeRevision>.Failure(page.ErrorCode!, page.ErrorMessage!);
            }

            Result parent = await ValidateParentRevisionAsync(
                connection, transaction, documentInstanceId, pageId, parentTreeRevisionId);
            if (parent.IsFailure)
            {
                return Result<DocumentTreeRevision>.Failure(parent.ErrorCode!, parent.ErrorMessage!);
            }

            DocumentTreeRevision revision = NewRevision(
                documentInstanceId,
                pageId,
                parentTreeRevisionId,
                source.Trim(),
                DocumentTreeRevisionStatus.Staging,
                false,
                null);
            await InsertRevisionAsync(connection, transaction, revision, null);
            return Result<DocumentTreeRevision>.Success(revision);
        }, cancellationToken);
    }

    public async Task<Result<PageEditSession>> BeginPageEditAsync(
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        CancellationToken cancellationToken = default)
    {
        return await InTransactionAsync(async (connection, transaction) =>
        {
            Result page = await ValidatePageAsync(connection, transaction, documentInstanceId, pageId);
            if (page.IsFailure)
            {
                return Result<PageEditSession>.Failure(page.ErrorCode!, page.ErrorMessage!);
            }

            int activeDrafts = await connection.ExecuteScalarAsync<int>(
                """
                select count(1) from document_tree_revisions
                where document_instance_id = @DocumentInstanceId and page_id = @PageId and status = 'draft';
                """,
                new { DocumentInstanceId = documentInstanceId.ToString(), PageId = pageId.ToString() },
                transaction);
            if (activeDrafts > 0)
            {
                return Result<PageEditSession>.Failure(
                    AppErrorCodes.InvalidState,
                    "This physical page already has an active edit session.");
            }

            DocumentTreeRevisionRow? current = await GetCurrentRevisionRowAsync(
                connection, transaction, documentInstanceId, pageId);
            PageEditSessionId sessionId = PageEditSessionId.New();
            DocumentTreeRevision draft = NewRevision(
                documentInstanceId,
                pageId,
                current is null ? null : DocumentTreeRevisionId.Parse(current.TreeRevisionId),
                DocumentTreeRevisionSource.ManualEdit,
                DocumentTreeRevisionStatus.Draft,
                false,
                null);
            await InsertRevisionAsync(connection, transaction, draft, sessionId);

            if (current is not null)
            {
                await connection.ExecuteAsync(
                    """
                    insert into document_boxes (
                        tree_revision_id, box_id, document_instance_id, page_id, parent_box_id,
                        next_sibling_box_id, box_type, sub_type, base_type, bbox_x, bbox_y,
                        bbox_width, bbox_height, payload_json, heading_level, code_language,
                        confidence, suppressed, continues_from_box_id)
                    select @DraftRevisionId, box_id, document_instance_id, page_id, parent_box_id,
                        next_sibling_box_id, box_type, sub_type, base_type, bbox_x, bbox_y,
                        bbox_width, bbox_height, payload_json, heading_level, code_language,
                        confidence, suppressed, continues_from_box_id
                    from document_boxes where tree_revision_id = @CurrentRevisionId;
                    """,
                    new
                    {
                        DraftRevisionId = draft.TreeRevisionId.ToString(), CurrentRevisionId = current.TreeRevisionId
                    },
                    transaction);
            }

            return Result<PageEditSession>.Success(
                new PageEditSession(sessionId, draft.TreeRevisionId, documentInstanceId, pageId));
        }, cancellationToken);
    }

    public async Task<Result<DocumentTreeRevision>> StagePageAsync(
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        IReadOnlyList<DocumentBoxSeed> boxes,
        string source = DocumentTreeRevisionSource.Import,
        DocumentTreeRevisionId? parentTreeRevisionId = null,
        CancellationToken cancellationToken = default)
    {
        if (!DocumentTreeRevisionSource.IsKnown(source))
        {
            return Failure<DocumentTreeRevision>("Document tree revision source is invalid.");
        }

        return await InTransactionAsync(async (connection, transaction) =>
        {
            Result page = await ValidatePageAsync(connection, transaction, documentInstanceId, pageId);
            if (page.IsFailure)
            {
                return Result<DocumentTreeRevision>.Failure(page.ErrorCode!, page.ErrorMessage!);
            }

            Result parent = await ValidateParentRevisionAsync(
                connection, transaction, documentInstanceId, pageId, parentTreeRevisionId);
            if (parent.IsFailure)
            {
                return Result<DocumentTreeRevision>.Failure(parent.ErrorCode!, parent.ErrorMessage!);
            }

            DocumentTreeRevision revision = NewRevision(
                documentInstanceId,
                pageId,
                parentTreeRevisionId,
                source,
                DocumentTreeRevisionStatus.Staging,
                false,
                null);
            IndexedSeed[] indexed = boxes.Select((seed, index) =>
                new IndexedSeed(index, seed, seed.BoxId ?? DocumentBoxId.New())).ToArray();
            indexed = NormalizeContainedBoxes(indexed);
            Dictionary<string, IndexedSeed[]> groups = indexed
                .GroupBy(value => BoxKey(value.Seed.ParentBoxId))
                .ToDictionary(group => group.Key, group => group.OrderBy(value => value.Seed.SourceOrder).ToArray());
            DocumentBox[] staged = indexed.Select(value =>
            {
                DocumentBoxSeed seed = value.Seed;
                IndexedSeed[] siblings = groups[BoxKey(seed.ParentBoxId)];
                int siblingIndex = Array.FindIndex(siblings, sibling => sibling.Index == value.Index);
                return new DocumentBox(
                    revision.TreeRevisionId,
                    value.BoxId,
                    documentInstanceId,
                    pageId,
                    seed.ParentBoxId,
                    siblingIndex == siblings.Length - 1 ? null : siblings[siblingIndex + 1].BoxId,
                    seed.BoxType,
                    seed.SubType,
                    seed.BaseType,
                    seed.BBox,
                    seed.Payload,
                    seed.HeadingLevel,
                    seed.CodeLanguage,
                    seed.Confidence,
                    seed.Suppressed,
                    seed.ContinuesFromBoxId);
            }).ToArray();
            // Overlaps no longer block staging or adoption; they surface as workspace warnings.
            Result validation = _validator.Validate(revision, staged);
            if (validation.IsFailure)
            {
                return Result<DocumentTreeRevision>.Failure(
                    validation.ErrorCode!, validation.ErrorMessage!, validation.Conflicts);
            }

            await InsertRevisionAsync(connection, transaction, revision, null);
            await ReplaceBoxesAsync(connection, transaction, revision.TreeRevisionId, staged);
            return Result<DocumentTreeRevision>.Success(revision);
        }, cancellationToken);
    }

    public async Task<Result<DocumentTreeRevision>> GetCurrentRevisionAsync(
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        CancellationToken cancellationToken = default)
    {
        return await WithConnectionAsync(async connection =>
        {
            DocumentTreeRevisionRow? row = await GetCurrentRevisionRowAsync(
                connection, null, documentInstanceId, pageId);
            return row is null
                ? Result<DocumentTreeRevision>.Failure(AppErrorCodes.NotFound,
                    "Current committed document tree revision was not found for the physical page.")
                : Result<DocumentTreeRevision>.Success(row.ToRevision());
        }, cancellationToken);
    }

    public async Task<Result<IReadOnlyList<DocumentBox>>> ListBoxesAsync(
        DocumentTreeRevisionId treeRevisionId,
        CancellationToken cancellationToken = default)
    {
        return await WithConnectionAsync(async connection =>
        {
            DocumentTreeRevisionRow? revision = await GetRevisionRowAsync(connection, null, treeRevisionId);
            if (revision is null)
            {
                return Result<IReadOnlyList<DocumentBox>>.Failure(
                    AppErrorCodes.NotFound,
                    "Document tree revision was not found.");
            }

            DocumentBox[] boxes = await GetBoxesAsync(connection, null, treeRevisionId);
            return Result<IReadOnlyList<DocumentBox>>.Success(boxes);
        }, cancellationToken);
    }

    public async Task<Result<DocumentTreeRevision>> AdoptStagingRevisionAsync(
        DocumentTreeRevisionId stagingRevisionId,
        CancellationToken cancellationToken = default)
    {
        Result<IReadOnlyList<DocumentTreeRevision>> result = await AdoptStagingRevisionsAsync(
            [stagingRevisionId], cancellationToken);
        return result.IsSuccess
            ? Result<DocumentTreeRevision>.Success(result.Value.Single())
            : Result<DocumentTreeRevision>.Failure(result.ErrorCode!, result.ErrorMessage!, result.Conflicts);
    }

    public async Task<Result<IReadOnlyList<DocumentTreeRevision>>> AdoptStagingRevisionsAsync(
        IReadOnlyList<DocumentTreeRevisionId> stagingRevisionIds,
        CancellationToken cancellationToken = default)
    {
        return await InTransactionAsync(async (connection, transaction) =>
        {
            List<DocumentTreeRevision> staging = [];
            HashSet<(DocumentInstanceId DocumentInstanceId, PageId PageId)> pages = [];
            foreach (DocumentTreeRevisionId stagingRevisionId in stagingRevisionIds)
            {
                DocumentTreeRevisionRow? stagingRow = await GetRevisionRowAsync(
                    connection, transaction, stagingRevisionId);
                if (stagingRow is null || stagingRow.Status != DocumentTreeRevisionStatus.Staging)
                {
                    return Result<IReadOnlyList<DocumentTreeRevision>>.Failure(
                        AppErrorCodes.InvalidState,
                        "Only existing staging document tree revisions can be adopted.");
                }

                DocumentTreeRevision revision = stagingRow.ToRevision();
                if (!pages.Add((revision.DocumentInstanceId, revision.PageId)))
                {
                    return Result<IReadOnlyList<DocumentTreeRevision>>.Failure(
                        AppErrorCodes.InvalidState,
                        "A physical page may only have one staging revision in an adoption batch.");
                }

                DocumentBox[] boxes = await GetBoxesAsync(connection, transaction, stagingRevisionId);
                Result valid = _validator.Validate(revision, boxes);
                if (valid.IsFailure)
                {
                    return Result<IReadOnlyList<DocumentTreeRevision>>.Failure(
                        valid.ErrorCode!, valid.ErrorMessage!, valid.Conflicts);
                }

                staging.Add(revision);
            }

            // Pre-generate the staging -> committed revision mapping, then copy boxes within
            // SQLite via INSERT ... SELECT so a large staging tree is never read back into
            // .NET just to re-serialize and insert one row at a time.
            DateTimeOffset committedAt = _clock.UtcNow.ToUniversalTime();
            List<DocumentTreeRevision> committedRevisions = [];
            foreach (DocumentTreeRevision stagingRevision in staging)
            {
                DocumentTreeRevisionRow? current = await GetCurrentRevisionRowAsync(
                    connection, transaction, stagingRevision.DocumentInstanceId, stagingRevision.PageId);
                DocumentTreeRevision committed = NewRevision(
                    stagingRevision.DocumentInstanceId,
                    stagingRevision.PageId,
                    current is null ? null : DocumentTreeRevisionId.Parse(current.TreeRevisionId),
                    DocumentTreeRevisionSource.OcrAdopted,
                    DocumentTreeRevisionStatus.Committed,
                    true,
                    committedAt);

                await ClearCurrentAsync(connection, transaction, stagingRevision.DocumentInstanceId,
                    stagingRevision.PageId);
                await InsertRevisionAsync(connection, transaction, committed, null);
                await CopyStagingBoxesAsync(
                    connection, transaction, stagingRevision.TreeRevisionId, committed.TreeRevisionId);
                committedRevisions.Add(committed);
            }

            await connection.ExecuteAsync(
                """
                update document_tree_revisions set status = 'discarded'
                where tree_revision_id in @StagingRevisionIds;
                """,
                new { StagingRevisionIds = stagingRevisionIds.Select(id => id.ToString()).ToArray() },
                transaction);
            foreach (IGrouping<DocumentInstanceId, (DocumentInstanceId DocumentInstanceId, PageId PageId)> group
                     in pages.GroupBy(page => page.DocumentInstanceId))
            {
                await connection.ExecuteAsync(
                    """
                    update search_units set status = 'stale', updated_at = @Now
                    where document_instance_id = @DocumentInstanceId
                      and page_id in @PageIds and status = 'current';
                    """,
                    new
                    {
                        DocumentInstanceId = group.Key.ToString(),
                        PageIds = group.Select(page => page.PageId.ToString()).ToArray(),
                        Now = FormatUtc(committedAt)
                    },
                    transaction);
            }

            return Result<IReadOnlyList<DocumentTreeRevision>>.Success(committedRevisions);
        }, cancellationToken, revisions => LibraryChangeSet.Empty with
        {
            DocumentInstanceIds = revisions.Select(revision => revision.DocumentInstanceId).Distinct().ToArray(),
            PageIds = revisions.Select(revision => revision.PageId).Distinct().ToArray()
        });
    }

    public async Task<Result<DocumentTreeRevision>> CommitPageEditAsync(
        PageEditSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        return await InTransactionAsync(async (connection, transaction) =>
        {
            DocumentTreeRevisionRow? row = await GetSessionRevisionRowAsync(connection, transaction, sessionId);
            if (row is null)
            {
                return Result<DocumentTreeRevision>.Failure(AppErrorCodes.NotFound, "Page edit session was not found.");
            }

            DocumentTreeRevision draft = row.ToRevision();
            DocumentBox[] boxes = await GetBoxesAsync(connection, transaction, draft.TreeRevisionId);
            Result validation = _validator.Validate(draft, boxes);
            if (validation.IsFailure)
            {
                return Result<DocumentTreeRevision>.Failure(
                    validation.ErrorCode!, validation.ErrorMessage!, validation.Conflicts);
            }

            DateTimeOffset committedAt = _clock.UtcNow.ToUniversalTime();
            await ClearCurrentAsync(connection, transaction, draft.DocumentInstanceId, draft.PageId);
            await connection.ExecuteAsync(
                """
                update document_tree_revisions
                set status = 'committed', is_current = 1, committed_at = @CommittedAt, edit_session_id = null
                where tree_revision_id = @RevisionId and status = 'draft';
                """,
                new { RevisionId = draft.TreeRevisionId.ToString(), CommittedAt = FormatUtc(committedAt) },
                transaction);
            await MarkSearchStaleAsync(connection, transaction, draft.DocumentInstanceId, draft.PageId);
            return Result<DocumentTreeRevision>.Success(draft with
            {
                Status = DocumentTreeRevisionStatus.Committed,
                IsCurrent = true,
                CommittedAt = committedAt
            });
        }, cancellationToken, revision => LibraryChangeSet.Empty with
        {
            DocumentInstanceIds = [revision.DocumentInstanceId],
            PageIds = [revision.PageId]
        });
    }

    public async Task<Result> DiscardPageEditAsync(
        PageEditSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        Result<DocumentTreeRevision> result = await InTransactionAsync(async (connection, transaction) =>
        {
            DocumentTreeRevisionRow? row = await GetSessionRevisionRowAsync(connection, transaction, sessionId);
            if (row is null)
            {
                return Result<DocumentTreeRevision>.Failure(AppErrorCodes.NotFound, "Page edit session was not found.");
            }

            await connection.ExecuteAsync(
                "delete from document_boxes where tree_revision_id = @RevisionId;",
                new { RevisionId = row.TreeRevisionId },
                transaction);
            await connection.ExecuteAsync(
                """
                update document_tree_revisions
                set status = 'discarded', edit_session_id = null
                where tree_revision_id = @RevisionId;
                """,
                new { RevisionId = row.TreeRevisionId },
                transaction);
            return Result<DocumentTreeRevision>.Success(row.ToRevision() with
            {
                Status = DocumentTreeRevisionStatus.Discarded
            });
        }, cancellationToken);
        return result.IsSuccess
            ? Result.Success()
            : Result.Failure(result.ErrorCode!, result.ErrorMessage!, result.Conflicts);
    }

    public Task<Result<DocumentBox>> DrawAndInsertLeafAsync(
        PageEditSessionId sessionId,
        InsertLeafCommand command,
        CancellationToken cancellationToken = default)
    {
        return MutateDraftAsync(sessionId, (revision, boxes) =>
        {
            DocumentBoxId id = command.BoxId ?? DocumentBoxId.New();
            if (boxes.Any(box => box.BoxId == id))
            {
                return Mutation<DocumentBox>.Failure("A document box with this id already exists in the draft.");
            }

            Result<DocumentBoxId?> next = ResolveInsertion(boxes, command.ParentBoxId, command.InsertAfterBoxId);
            if (next.IsFailure)
            {
                return Mutation<DocumentBox>.Failure(next.ErrorMessage!);
            }

            DocumentBox box = new(
                revision.TreeRevisionId,
                id,
                revision.DocumentInstanceId,
                revision.PageId,
                command.ParentBoxId,
                next.Value,
                command.BoxType.Trim(),
                NullIfWhiteSpace(command.SubType),
                NullIfWhiteSpace(command.BaseType),
                command.BBox,
                command.Payload,
                command.HeadingLevel,
                NullIfWhiteSpace(command.CodeLanguage),
                command.Confidence,
                command.Suppressed);
            LinkPredecessor(boxes, command.ParentBoxId, command.InsertAfterBoxId, id);
            boxes.Add(box);
            return Mutation<DocumentBox>.Success(box);
        }, cancellationToken);
    }

    public Task<Result<DocumentBox>> InsertLogicalPageAsync(
        PageEditSessionId sessionId,
        DocumentBoxId? insertAfterBoxId,
        NormalizedBBox bbox,
        CancellationToken cancellationToken = default)
    {
        return MutateDraftAsync(sessionId, (revision, boxes) =>
        {
            Result<DocumentBoxId?> next = ResolveInsertion(boxes, null, insertAfterBoxId);
            if (next.IsFailure)
            {
                return Mutation<DocumentBox>.Failure(next.ErrorMessage!);
            }

            DocumentBox logicalPage = new(
                revision.TreeRevisionId,
                DocumentBoxId.New(),
                revision.DocumentInstanceId,
                revision.PageId,
                null,
                next.Value,
                DocumentBoxType.LogicalPage,
                null,
                null,
                bbox,
                null,
                null,
                null,
                null,
                false);
            LinkPredecessor(boxes, null, insertAfterBoxId, logicalPage.BoxId);
            boxes.Add(logicalPage);
            return Mutation<DocumentBox>.Success(logicalPage);
        }, cancellationToken);
    }

    public async Task<Result> UpdateLeafAsync(
        PageEditSessionId sessionId,
        UpdateLeafCommand command,
        CancellationToken cancellationToken = default)
    {
        Result<DocumentBox> result = await MutateDraftAsync(sessionId, (_, boxes) =>
        {
            int index = boxes.FindIndex(box => box.BoxId == command.BoxId);
            if (index < 0 || boxes.Any(box => box.ParentBoxId == command.BoxId))
            {
                return Mutation<DocumentBox>.Failure("Only an existing leaf document box can be edited.");
            }

            string boxType = command.BoxType.Trim();
            DocumentBox updated = boxes[index] with
            {
                BoxType = boxType,
                Payload = command.Payload,
                HeadingLevel = boxType == DocumentBoxType.Title
                    ? command.HeadingLevel ?? boxes[index].HeadingLevel ?? 1
                    : null,
                CodeLanguage = boxType is DocumentBoxType.Code or DocumentBoxType.Algorithm
                    ? NullIfWhiteSpace(command.CodeLanguage)
                    : null,
                SubType = NullIfWhiteSpace(command.SubType),
                BaseType = NullIfWhiteSpace(command.BaseType)
            };
            boxes[index] = updated;
            return Mutation<DocumentBox>.Success(updated);
        }, cancellationToken);
        return ToResult(result);
    }

    public async Task<Result> UpdateBBoxAsync(
        PageEditSessionId sessionId,
        DocumentBoxId boxId,
        NormalizedBBox bbox,
        CancellationToken cancellationToken = default)
    {
        Result<DocumentBox> result = await MutateDraftAsync(sessionId, (_, boxes) =>
        {
            int index = boxes.FindIndex(box => box.BoxId == boxId);
            if (index < 0)
            {
                return Mutation<DocumentBox>.Failure("Document box was not found in the page draft.");
            }

            boxes[index] = boxes[index] with { BBox = bbox };
            return Mutation<DocumentBox>.Success(boxes[index]);
        }, cancellationToken);
        return ToResult(result);
    }

    public async Task<Result> MoveBoxAsync(
        PageEditSessionId sessionId,
        MoveBoxCommand command,
        CancellationToken cancellationToken = default)
    {
        Result<DocumentBox> result = await MutateDraftAsync(sessionId, (_, boxes) =>
        {
            int index = boxes.FindIndex(box => box.BoxId == command.BoxId);
            if (index < 0)
            {
                return Mutation<DocumentBox>.Failure("Document box was not found in the page draft.");
            }

            DocumentBox moving = boxes[index];
            Unlink(boxes, moving);
            moving = moving with { ParentBoxId = command.NewParentBoxId, NextSiblingBoxId = null };
            boxes[index] = moving;

            Result<DocumentBoxId?> next = ResolveInsertion(
                boxes.Where(box => box.BoxId != command.BoxId).ToList(),
                command.NewParentBoxId,
                command.InsertAfterBoxId);
            if (next.IsFailure || command.InsertAfterBoxId == command.BoxId)
            {
                return Mutation<DocumentBox>.Failure(next.ErrorMessage ?? "A box cannot be inserted after itself.");
            }

            LinkPredecessor(boxes, command.NewParentBoxId, command.InsertAfterBoxId, command.BoxId);
            moving = moving with { NextSiblingBoxId = next.Value };
            boxes[index] = moving;
            return Mutation<DocumentBox>.Success(moving);
        }, cancellationToken);
        return ToResult(result);
    }

    public Task<Result<IReadOnlyList<DocumentBox>>> SplitLeafAsync(
        PageEditSessionId sessionId,
        SplitLeafCommand command,
        CancellationToken cancellationToken = default)
    {
        return MutateDraftAsync(sessionId, (_, boxes) =>
        {
            int index = boxes.FindIndex(box => box.BoxId == command.BoxId);
            if (index < 0 || boxes.Any(box => box.ParentBoxId == command.BoxId))
            {
                return Mutation<IReadOnlyList<DocumentBox>>.Failure("Only an existing leaf box can be split.");
            }

            if (!HasPayloadContent(command.FirstPayload) || !HasPayloadContent(command.SecondPayload))
            {
                return Mutation<IReadOnlyList<DocumentBox>>.Failure(
                    "Both split leaves must provide non-empty content.");
            }

            DocumentBox original = boxes[index];
            DocumentBox first = original with
            {
                BoxId = DocumentBoxId.New(),
                BBox = command.FirstBBox,
                Payload = command.FirstPayload,
                NextSiblingBoxId = null
            };
            DocumentBox second = original with
            {
                BoxId = DocumentBoxId.New(),
                BBox = command.SecondBBox,
                Payload = command.SecondPayload,
                NextSiblingBoxId = original.NextSiblingBoxId,
                // The tail fragment carries its own text, so it is no longer a visual continuation.
                ContinuesFromBoxId = null
            };
            first = first with { NextSiblingBoxId = second.BoxId };
            ReplacePredecessor(boxes, original, first.BoxId);
            // Continuation regions that pointed at the original now follow the tail fragment.
            RepointContinuation(boxes, [original.BoxId], second.BoxId);
            boxes.RemoveAt(index);
            boxes.Add(first);
            boxes.Add(second);
            return Mutation<IReadOnlyList<DocumentBox>>.Success([first, second]);
        }, cancellationToken);
    }

    public Task<Result<DocumentBox>> MergeLeavesAsync(
        PageEditSessionId sessionId,
        MergeLeavesCommand command,
        CancellationToken cancellationToken = default)
    {
        return MutateDraftAsync(sessionId, (_, boxes) =>
        {
            if (command.BoxIds.Count < 2 || command.BoxIds.Distinct().Count() != command.BoxIds.Count)
            {
                return Mutation<DocumentBox>.Failure("Merge requires at least two distinct leaf boxes.");
            }

            if (!HasPayloadContent(command.Payload))
            {
                return Mutation<DocumentBox>.Failure("A merged leaf must provide non-empty content.");
            }

            DocumentBox[] selected = command.BoxIds
                .Select(id => boxes.SingleOrDefault(box => box.BoxId == id))
                .Where(box => box is not null)
                .Cast<DocumentBox>()
                .ToArray();
            if (selected.Length != command.BoxIds.Count ||
                selected.Any(box => boxes.Any(b => b.ParentBoxId == box.BoxId)))
            {
                return Mutation<DocumentBox>.Failure("Every merged document box must be an existing leaf.");
            }

            DocumentBox first = selected[0];
            if (selected.Any(box => box.ParentBoxId != first.ParentBoxId || box.BoxType != first.BoxType ||
                                    box.HeadingLevel != first.HeadingLevel))
            {
                return Mutation<DocumentBox>.Failure(
                    "Merged leaves must have the same parent, type, and heading level.");
            }

            DocumentBoxId[] ordered = OrderSiblings(boxes, first.ParentBoxId).ToArray();
            int start = Array.IndexOf(ordered, command.BoxIds[0]);
            if (start < 0 || !ordered.Skip(start).Take(command.BoxIds.Count).SequenceEqual(command.BoxIds))
            {
                return Mutation<DocumentBox>.Failure("Merged leaves must be consecutive in reading order.");
            }

            DocumentBox last = selected[^1];
            DocumentBox merged = first with
            {
                BoxId = DocumentBoxId.New(),
                BBox = Union(selected.Select(box => box.BBox)),
                Payload = command.Payload,
                NextSiblingBoxId = last.NextSiblingBoxId,
                Confidence = selected.All(box => box.Confidence is not null)
                    ? selected.Average(box => box.Confidence!.Value)
                    : null,
                // The merged leaf holds real text, so it is no longer a visual continuation.
                ContinuesFromBoxId = null
            };
            ReplacePredecessor(boxes, first, merged.BoxId);
            RepointContinuation(boxes, command.BoxIds, merged.BoxId);
            boxes.RemoveAll(box => command.BoxIds.Contains(box.BoxId));
            boxes.Add(merged);
            return Mutation<DocumentBox>.Success(merged);
        }, cancellationToken);
    }

    private static bool HasPayloadContent(DocumentBoxPayload payload)
    {
        return payload switch
        {
            TextBoxPayload value => !string.IsNullOrWhiteSpace(value.Markdown),
            EquationBoxPayload value => !string.IsNullOrWhiteSpace(value.Latex),
            ListBoxPayload value => !string.IsNullOrWhiteSpace(value.Markdown),
            TableBoxPayload value => !string.IsNullOrWhiteSpace(value.Markdown),
            CodeBoxPayload value => !string.IsNullOrWhiteSpace(value.Code),
            MediaBoxPayload value => !string.IsNullOrWhiteSpace(value.AssetId) ||
                                     !string.IsNullOrWhiteSpace(value.Description),
            _ => false
        };
    }

    public async Task<Result> SetSuppressedAsync(
        PageEditSessionId sessionId,
        DocumentBoxId boxId,
        bool suppressed,
        CancellationToken cancellationToken = default)
    {
        Result<DocumentBox> result = await MutateDraftAsync(sessionId, (_, boxes) =>
        {
            int index = boxes.FindIndex(box => box.BoxId == boxId);
            if (index < 0 || boxes[index].BoxType == DocumentBoxType.LogicalPage)
            {
                return Mutation<DocumentBox>.Failure("Only an existing leaf box can change suppression.");
            }

            boxes[index] = boxes[index] with { Suppressed = suppressed };
            return Mutation<DocumentBox>.Success(boxes[index]);
        }, cancellationToken);
        return ToResult(result);
    }

    public async Task<Result> DeleteBoxAsync(
        PageEditSessionId sessionId,
        DocumentBoxId boxId,
        CancellationToken cancellationToken = default)
    {
        Result<DocumentBox> result = await MutateDraftAsync(sessionId, (_, boxes) =>
        {
            int index = boxes.FindIndex(box => box.BoxId == boxId);
            if (index < 0 || boxes.Any(box => box.ParentBoxId == boxId))
            {
                return Mutation<DocumentBox>.Failure("Only an existing box without children can be deleted.");
            }

            DocumentBox deleted = boxes[index];
            Unlink(boxes, deleted);
            RepointContinuation(boxes, [boxId], null);
            boxes.RemoveAt(index);
            return Mutation<DocumentBox>.Success(deleted);
        }, cancellationToken);
        return ToResult(result);
    }

    public async Task<Result> AcceptLocalOcrCandidateAsync(
        PageEditSessionId sessionId,
        DocumentBoxId boxId,
        LocalOcrCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        Result<DocumentBox> result = await MutateDraftAsync(sessionId, (_, boxes) =>
        {
            int index = boxes.FindIndex(box => box.BoxId == boxId);
            if (index < 0 || boxes.Any(box => box.ParentBoxId == boxId))
            {
                return Mutation<DocumentBox>.Failure("Local OCR can only update an existing leaf box.");
            }

            DocumentBox original = boxes[index];
            DocumentBox updated = original with
            {
                BoxType = candidate.BoxType,
                Payload = candidate.Payload,
                HeadingLevel = candidate.HeadingLevel,
                CodeLanguage = candidate.BoxType is DocumentBoxType.Code or DocumentBoxType.Algorithm
                    ? original.CodeLanguage
                    : null
            };
            boxes[index] = updated;
            return Mutation<DocumentBox>.Success(updated);
        }, cancellationToken);
        return ToResult(result);
    }

    private async Task<Result<T>> MutateDraftAsync<T>(
        PageEditSessionId sessionId,
        Func<DocumentTreeRevision, List<DocumentBox>, Mutation<T>> mutate,
        CancellationToken cancellationToken)
    {
        return await InTransactionAsync(async (connection, transaction) =>
        {
            DocumentTreeRevisionRow? row = await GetSessionRevisionRowAsync(connection, transaction, sessionId);
            if (row is null)
            {
                return Result<T>.Failure(AppErrorCodes.NotFound, "Page edit session was not found.");
            }

            DocumentTreeRevision revision = row.ToRevision();
            List<DocumentBox> boxes = (await GetBoxesAsync(connection, transaction, revision.TreeRevisionId)).ToList();
            Mutation<T> mutation = mutate(revision, boxes);
            if (!mutation.IsSuccess)
            {
                return Result<T>.Failure(AppErrorCodes.ValidationFailed, mutation.ErrorMessage!);
            }

            Result validation = _validator.Validate(revision, boxes);
            if (validation.IsFailure)
            {
                return Result<T>.Failure(validation.ErrorCode!, validation.ErrorMessage!, validation.Conflicts);
            }

            await ReplaceBoxesAsync(connection, transaction, revision.TreeRevisionId, boxes);
            return Result<T>.Success(mutation.Value!);
        }, cancellationToken);
    }

    private static Result<DocumentBoxId?> ResolveInsertion(
        IReadOnlyList<DocumentBox> boxes,
        DocumentBoxId? parentId,
        DocumentBoxId? insertAfterId)
    {
        if (parentId is not null)
        {
            DocumentBox? parent = boxes.SingleOrDefault(box => box.BoxId == parentId.Value);
            if (parent is null)
            {
                return Result<DocumentBoxId?>.Failure(
                    AppErrorCodes.ValidationFailed,
                    "The insertion parent must be an existing box in the draft.");
            }
        }

        DocumentBox[] siblings = boxes.Where(box => box.ParentBoxId == parentId).ToArray();
        if (insertAfterId is null)
        {
            HashSet<DocumentBoxId> referenced = siblings
                .Where(box => box.NextSiblingBoxId is not null)
                .Select(box => box.NextSiblingBoxId!.Value)
                .ToHashSet();
            return Result<DocumentBoxId?>.Success(
                siblings.SingleOrDefault(box => !referenced.Contains(box.BoxId))?.BoxId);
        }

        DocumentBox? previous = siblings.SingleOrDefault(box => box.BoxId == insertAfterId.Value);
        return previous is null
            ? Result<DocumentBoxId?>.Failure(
                AppErrorCodes.ValidationFailed,
                "The insertion predecessor must exist under the requested parent.")
            : Result<DocumentBoxId?>.Success(previous.NextSiblingBoxId);
    }

    private static void LinkPredecessor(
        List<DocumentBox> boxes,
        DocumentBoxId? parentId,
        DocumentBoxId? insertAfterId,
        DocumentBoxId insertedId)
    {
        if (insertAfterId is null)
        {
            return;
        }

        int index = boxes.FindIndex(box => box.BoxId == insertAfterId.Value && box.ParentBoxId == parentId);
        boxes[index] = boxes[index] with { NextSiblingBoxId = insertedId };
    }

    private static void Unlink(List<DocumentBox> boxes, DocumentBox box)
    {
        int predecessor = boxes.FindIndex(candidate => candidate.ParentBoxId == box.ParentBoxId &&
                                                       candidate.NextSiblingBoxId == box.BoxId);
        if (predecessor >= 0)
        {
            boxes[predecessor] = boxes[predecessor] with { NextSiblingBoxId = box.NextSiblingBoxId };
        }
    }

    private static void ReplacePredecessor(List<DocumentBox> boxes, DocumentBox oldBox, DocumentBoxId replacementId)
    {
        int predecessor = boxes.FindIndex(candidate => candidate.ParentBoxId == oldBox.ParentBoxId &&
                                                       candidate.NextSiblingBoxId == oldBox.BoxId);
        if (predecessor >= 0)
        {
            boxes[predecessor] = boxes[predecessor] with { NextSiblingBoxId = replacementId };
        }
    }

    private static void RepointContinuation(
        List<DocumentBox> boxes,
        IReadOnlyCollection<DocumentBoxId> replacedIds,
        DocumentBoxId? targetId)
    {
        for (int i = 0; i < boxes.Count; i++)
        {
            if (boxes[i].ContinuesFromBoxId is { } link && replacedIds.Contains(link))
            {
                boxes[i] = boxes[i] with { ContinuesFromBoxId = targetId };
            }
        }
    }

    private static IEnumerable<DocumentBoxId> OrderSiblings(
        IReadOnlyList<DocumentBox> boxes,
        DocumentBoxId? parentId)
    {
        DocumentBox[] siblings = boxes.Where(box => box.ParentBoxId == parentId).ToArray();
        HashSet<DocumentBoxId> referenced = siblings
            .Where(box => box.NextSiblingBoxId is not null)
            .Select(box => box.NextSiblingBoxId!.Value)
            .ToHashSet();
        DocumentBox? current = siblings.SingleOrDefault(box => !referenced.Contains(box.BoxId));
        while (current is not null)
        {
            yield return current.BoxId;
            current = current.NextSiblingBoxId is null
                ? null
                : siblings.Single(box => box.BoxId == current.NextSiblingBoxId.Value);
        }
    }

    private static NormalizedBBox Union(IEnumerable<NormalizedBBox> boxes)
    {
        NormalizedBBox[] values = boxes.ToArray();
        double x = values.Min(box => box.X);
        double y = values.Min(box => box.Y);
        double right = values.Max(box => box.X + box.Width);
        double bottom = values.Max(box => box.Y + box.Height);
        return new NormalizedBBox(x, y, right - x, bottom - y);
    }

    private DocumentTreeRevision NewRevision(
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        DocumentTreeRevisionId? parentId,
        string source,
        string status,
        bool current,
        DateTimeOffset? committedAt)
    {
        return new DocumentTreeRevision(
            DocumentTreeRevisionId.New(),
            documentInstanceId,
            pageId,
            parentId,
            source,
            status,
            current,
            _clock.UtcNow.ToUniversalTime(),
            committedAt);
    }

    private async Task<Result<T>> InTransactionAsync<T>(
        Func<SqliteConnection, DbTransaction, Task<Result<T>>> action,
        CancellationToken cancellationToken,
        Func<T, LibraryChangeSet>? revisionFactory = null)
    {
        try
        {
            using IDisposable writeLease = await _connectionFactory.EnterWriteAsync(cancellationToken);
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            Result<T> result = await action(connection, transaction);
            if (result.IsFailure)
            {
                await transaction.RollbackAsync(cancellationToken);
                return result;
            }

            LibraryChangeSet? changeSet = null;
            if (_revisions is not null && revisionFactory is not null)
            {
                Result<LibraryChangeSet> incremented = await _revisions.IncrementInTransactionAsync(
                    connection, transaction, revisionFactory(result.Value), cancellationToken);
                if (incremented.IsFailure)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Result<T>.Failure(incremented.ErrorCode!, incremented.ErrorMessage!);
                }

                changeSet = incremented.Value;
            }

            await transaction.CommitAsync(cancellationToken);
            if (changeSet is not null)
            {
                _revisions!.PublishCommitted(changeSet);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.document-tree"))
        {
            return Result<T>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    private async Task<Result<T>> WithConnectionAsync<T>(
        Func<SqliteConnection, Task<Result<T>>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateReadConnection();
            await connection.OpenAsync(cancellationToken);
            return await action(connection);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.document-tree"))
        {
            return Result<T>.Failure(AppErrorCodes.DatabaseError, $"Database operation failed: {exception.Message}");
        }
    }

    private static async Task<Result> ValidatePageAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        DocumentInstanceId documentInstanceId,
        PageId pageId)
    {
        int count = await connection.ExecuteScalarAsync<int>(
            """
            select count(1) from pages
            where page_id = @PageId and document_instance_id = @DocumentInstanceId;
            """,
            new { PageId = pageId.ToString(), DocumentInstanceId = documentInstanceId.ToString() },
            transaction);
        return count == 1
            ? Result.Success()
            : Result.Failure(AppErrorCodes.NotFound,
                "Physical page was not found for the document instance.");
    }

    private static async Task<Result> ValidateParentRevisionAsync(
        SqliteConnection connection,
        DbTransaction? transaction,
        DocumentInstanceId documentInstanceId,
        PageId pageId,
        DocumentTreeRevisionId? parentTreeRevisionId)
    {
        if (parentTreeRevisionId is null)
        {
            return Result.Success();
        }

        DocumentTreeRevisionRow? parent = await GetRevisionRowAsync(
            connection, transaction, parentTreeRevisionId.Value);
        return parent is not null && parent.DocumentInstanceId == documentInstanceId.ToString() &&
               parent.PageId == pageId.ToString()
            ? Result.Success()
            : Result.Failure(AppErrorCodes.ValidationFailed,
                "Parent tree revision must belong to the same physical page.");
    }

    private static string BoxKey(DocumentBoxId? boxId)
    {
        return boxId?.ToString() ?? string.Empty;
    }

    private static Task<DocumentTreeRevisionRow?> GetRevisionRowAsync(
        SqliteConnection connection,
        DbTransaction? transaction,
        DocumentTreeRevisionId revisionId)
    {
        return connection.QuerySingleOrDefaultAsync<DocumentTreeRevisionRow>(
            SelectRevisionSql + " where tree_revision_id = @RevisionId;",
            new { RevisionId = revisionId.ToString() },
            transaction);
    }

    private static Task<DocumentTreeRevisionRow?> GetSessionRevisionRowAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        PageEditSessionId sessionId)
    {
        return connection.QuerySingleOrDefaultAsync<DocumentTreeRevisionRow>(
            SelectRevisionSql + " where edit_session_id = @SessionId and status = 'draft';",
            new { SessionId = sessionId.ToString() },
            transaction);
    }

    private static Task<DocumentTreeRevisionRow?> GetCurrentRevisionRowAsync(
        SqliteConnection connection,
        DbTransaction? transaction,
        DocumentInstanceId documentInstanceId,
        PageId pageId)
    {
        return connection.QuerySingleOrDefaultAsync<DocumentTreeRevisionRow>(
            SelectRevisionSql +
            " where document_instance_id = @DocumentInstanceId and page_id = @PageId and is_current = 1;",
            new { DocumentInstanceId = documentInstanceId.ToString(), PageId = pageId.ToString() },
            transaction);
    }

    private static async Task<DocumentBox[]> GetBoxesAsync(
        SqliteConnection connection,
        DbTransaction? transaction,
        DocumentTreeRevisionId revisionId)
    {
        IEnumerable<DocumentBoxRow> rows = await connection.QueryAsync<DocumentBoxRow>(
            SelectBoxesSql + " where tree_revision_id = @RevisionId;",
            new { RevisionId = revisionId.ToString() },
            transaction);
        return InDocumentOrder(rows.Select(row => row.ToBox()).ToArray());
    }

    private static DocumentBox[] InDocumentOrder(IReadOnlyList<DocumentBox> boxes)
    {
        if (boxes.Count <= 1)
        {
            return boxes.ToArray();
        }

        List<DocumentBox> ordered = new(boxes.Count);
        HashSet<DocumentBoxId> visited = [];
        foreach (DocumentBox root in DocumentBoxProjection.Siblings(boxes, null))
        {
            AppendSubtree(boxes, root, ordered, visited);
        }

        if (ordered.Count < boxes.Count)
        {
            ordered.AddRange(boxes.Where(box => !visited.Contains(box.BoxId)));
        }

        return ordered.ToArray();
    }

    private static void AppendSubtree(
        IReadOnlyList<DocumentBox> boxes,
        DocumentBox box,
        List<DocumentBox> ordered,
        HashSet<DocumentBoxId> visited)
    {
        if (!visited.Add(box.BoxId))
        {
            return;
        }

        ordered.Add(box);
        foreach (DocumentBox child in DocumentBoxProjection.Siblings(boxes, box.BoxId))
        {
            AppendSubtree(boxes, child, ordered, visited);
        }
    }

    private static Task InsertRevisionAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        DocumentTreeRevision revision,
        PageEditSessionId? sessionId)
    {
        return connection.ExecuteAsync(
            """
            insert into document_tree_revisions (
                tree_revision_id, document_instance_id, page_id, parent_tree_revision_id,
                source, status, is_current, edit_session_id, created_at, committed_at)
            values (@TreeRevisionId, @DocumentInstanceId, @PageId, @ParentTreeRevisionId,
                @Source, @Status, @IsCurrent, @EditSessionId, @CreatedAt, @CommittedAt);
            """,
            new
            {
                TreeRevisionId = revision.TreeRevisionId.ToString(),
                DocumentInstanceId = revision.DocumentInstanceId.ToString(),
                PageId = revision.PageId.ToString(),
                ParentTreeRevisionId = revision.ParentTreeRevisionId?.ToString(),
                revision.Source,
                revision.Status,
                IsCurrent = revision.IsCurrent ? 1 : 0,
                EditSessionId = sessionId?.ToString(),
                CreatedAt = FormatUtc(revision.CreatedAt),
                CommittedAt = revision.CommittedAt is null ? null : FormatUtc(revision.CommittedAt.Value)
            },
            transaction);
    }

    private const int BoxWriteBatchSize = 500;

    private static async Task ReplaceBoxesAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        DocumentTreeRevisionId revisionId,
        IReadOnlyList<DocumentBox> boxes)
    {
        await connection.ExecuteAsync(
            "delete from document_boxes where tree_revision_id = @RevisionId;",
            new { RevisionId = revisionId.ToString() },
            transaction);
        foreach (DocumentBox[] chunk in boxes.Chunk(BoxWriteBatchSize))
        {
            await InsertBoxesAsync(connection, transaction, chunk);
        }
    }

    private static Task InsertBoxesAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        IReadOnlyList<DocumentBox> boxes)
    {
        StringBuilder values = new();
        Dictionary<string, object?> parameters = new();
        for (int i = 0; i < boxes.Count; i++)
        {
            DocumentBox box = boxes[i];
            if (i > 0)
            {
                values.Append(',');
            }

            values.Append("(@p").Append(i).Append("_TreeRevisionId,@p").Append(i).Append("_BoxId,@p").Append(i)
                .Append("_DocumentInstanceId,@p").Append(i).Append("_PageId,@p").Append(i).Append("_ParentBoxId,@p")
                .Append(i).Append("_NextSiblingBoxId,@p").Append(i).Append("_BoxType,@p").Append(i)
                .Append("_SubType,@p").Append(i).Append("_BaseType,@p").Append(i).Append("_BBoxX,@p").Append(i)
                .Append("_BBoxY,@p").Append(i).Append("_BBoxWidth,@p").Append(i).Append("_BBoxHeight,@p").Append(i)
                .Append("_PayloadJson,@p").Append(i).Append("_HeadingLevel,@p").Append(i).Append("_CodeLanguage,@p")
                .Append(i).Append("_Confidence,@p").Append(i).Append("_Suppressed,@p").Append(i)
                .Append("_ContinuesFromBoxId)");
            string prefix = "p" + i + "_";
            parameters[prefix + "TreeRevisionId"] = box.TreeRevisionId.ToString();
            parameters[prefix + "BoxId"] = box.BoxId.ToString();
            parameters[prefix + "DocumentInstanceId"] = box.DocumentInstanceId.ToString();
            parameters[prefix + "PageId"] = box.PageId.ToString();
            parameters[prefix + "ParentBoxId"] = box.ParentBoxId?.ToString();
            parameters[prefix + "NextSiblingBoxId"] = box.NextSiblingBoxId?.ToString();
            parameters[prefix + "BoxType"] = box.BoxType;
            parameters[prefix + "SubType"] = box.SubType;
            parameters[prefix + "BaseType"] = box.BaseType;
            parameters[prefix + "BBoxX"] = box.BBox.X;
            parameters[prefix + "BBoxY"] = box.BBox.Y;
            parameters[prefix + "BBoxWidth"] = box.BBox.Width;
            parameters[prefix + "BBoxHeight"] = box.BBox.Height;
            parameters[prefix + "PayloadJson"] = DocumentBoxPayloadSerializer.Serialize(box.Payload);
            parameters[prefix + "HeadingLevel"] = box.HeadingLevel;
            parameters[prefix + "CodeLanguage"] = box.CodeLanguage;
            parameters[prefix + "Confidence"] = box.Confidence;
            parameters[prefix + "Suppressed"] = box.Suppressed ? 1 : 0;
            parameters[prefix + "ContinuesFromBoxId"] = box.ContinuesFromBoxId?.ToString();
        }

        return connection.ExecuteAsync(
            "insert into document_boxes (" +
            "tree_revision_id, box_id, document_instance_id, page_id, parent_box_id, next_sibling_box_id, " +
            "box_type, sub_type, base_type, bbox_x, bbox_y, bbox_width, bbox_height, payload_json, " +
            "heading_level, code_language, confidence, suppressed, continues_from_box_id) values " + values,
            parameters,
            transaction);
    }

    private static Task CopyStagingBoxesAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        DocumentTreeRevisionId stagingRevisionId,
        DocumentTreeRevisionId committedRevisionId)
    {
        return connection.ExecuteAsync(
            """
            insert into document_boxes (
                tree_revision_id, box_id, document_instance_id, page_id, parent_box_id,
                next_sibling_box_id, box_type, sub_type, base_type, bbox_x, bbox_y,
                bbox_width, bbox_height, payload_json, heading_level, code_language,
                confidence, suppressed, continues_from_box_id)
            select @CommittedRevisionId, box_id, document_instance_id, page_id, parent_box_id,
                next_sibling_box_id, box_type, sub_type, base_type, bbox_x, bbox_y,
                bbox_width, bbox_height, payload_json, heading_level, code_language,
                confidence, suppressed, continues_from_box_id
            from document_boxes where tree_revision_id = @StagingRevisionId;
            """,
            new
            {
                CommittedRevisionId = committedRevisionId.ToString(),
                StagingRevisionId = stagingRevisionId.ToString()
            },
            transaction);
    }

    private static Task ClearCurrentAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        DocumentInstanceId documentInstanceId,
        PageId pageId)
    {
        return connection.ExecuteAsync(
            """
            update document_tree_revisions set is_current = 0
            where document_instance_id = @DocumentInstanceId and page_id = @PageId and is_current = 1;
            """,
            new { DocumentInstanceId = documentInstanceId.ToString(), PageId = pageId.ToString() },
            transaction);
    }

    private static Task MarkSearchStaleAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        DocumentInstanceId documentInstanceId,
        PageId pageId)
    {
        return connection.ExecuteAsync(
            """
            update search_units set status = 'stale', updated_at = @Now
            where document_instance_id = @DocumentInstanceId and page_id = @PageId and status = 'current';
            """,
            new
            {
                DocumentInstanceId = documentInstanceId.ToString(),
                PageId = pageId.ToString(),
                Now = FormatUtc(DateTimeOffset.UtcNow)
            },
            transaction);
    }

    private const string SelectRevisionSql =
        """
        select tree_revision_id as TreeRevisionId,
            document_instance_id as DocumentInstanceId,
            page_id as PageId,
            parent_tree_revision_id as ParentTreeRevisionId,
            source as Source,
            status as Status,
            is_current as IsCurrent,
            created_at as CreatedAt,
            committed_at as CommittedAt
        from document_tree_revisions
        """;

    private const string SelectBoxesSql =
        """
        select tree_revision_id as TreeRevisionId,
            box_id as BoxId,
            document_instance_id as DocumentInstanceId,
            page_id as PageId,
            parent_box_id as ParentBoxId,
            next_sibling_box_id as NextSiblingBoxId,
            box_type as BoxType,
            sub_type as SubType,
            base_type as BaseType,
            bbox_x as BBoxX,
            bbox_y as BBoxY,
            bbox_width as BBoxWidth,
            bbox_height as BBoxHeight,
            payload_json as PayloadJson,
            heading_level as HeadingLevel,
            code_language as CodeLanguage,
            confidence as Confidence,
            suppressed as Suppressed,
            continues_from_box_id as ContinuesFromBoxId
        from document_boxes
        """;

    private static Result ToResult<T>(Result<T> result)
    {
        return result.IsSuccess
            ? Result.Success()
            : Result.Failure(result.ErrorCode!, result.ErrorMessage!, result.Conflicts);
    }

    private static Result<T> Failure<T>(string message)
    {
        return Result<T>.Failure(AppErrorCodes.ValidationFailed, message);
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O");
    }

    private sealed record Mutation<T>(bool IsSuccess, T? Value, string? ErrorMessage)
    {
        public static Mutation<T> Success(T value)
        {
            return new Mutation<T>(true, value, null);
        }

        public static Mutation<T> Failure(string message)
        {
            return new Mutation<T>(false, default, message);
        }
    }

    private sealed record IndexedSeed(int Index, DocumentBoxSeed Seed, DocumentBoxId BoxId);

    private static IndexedSeed[] NormalizeContainedBoxes(IReadOnlyList<IndexedSeed> indexed)
    {
        IndexedSeed[] normalized = indexed.ToArray();
        foreach (IGrouping<DocumentBoxId?, IndexedSeed> group in indexed.GroupBy(value => value.Seed.ParentBoxId))
        {
            IndexedSeed[] siblings = group.ToArray();
            foreach (IndexedSeed child in siblings)
            {
                if (child.Seed.BoxType == DocumentBoxType.LogicalPage || child.Seed.Suppressed ||
                    child.Seed.ContinuesFromBoxId is not null ||
                    DocumentBoxType.AllowsOverlap(child.Seed.BoxType))
                {
                    continue;
                }

                double childArea = Area(child.Seed.BBox);
                // The immediate container (smallest containing box) becomes the parent, so
                // multi-level nesting chains resolve into a proper hierarchy.
                IndexedSeed? parent = siblings
                    .Where(candidate => candidate.Index != child.Index &&
                                        candidate.Seed.BoxType != DocumentBoxType.LogicalPage &&
                                        !candidate.Seed.Suppressed &&
                                        candidate.Seed.ContinuesFromBoxId is null &&
                                        !DocumentBoxType.AllowsOverlap(candidate.Seed.BoxType) &&
                                        Area(candidate.Seed.BBox) > childArea &&
                                        Contains(candidate.Seed.BBox, child.Seed.BBox))
                    .OrderBy(candidate => Area(candidate.Seed.BBox))
                    .ThenBy(candidate => candidate.Index)
                    .FirstOrDefault();
                if (parent is not null)
                {
                    normalized[child.Index] = child with { Seed = child.Seed with { ParentBoxId = parent.BoxId } };
                }
            }
        }

        return normalized;
    }

    private static double Area(NormalizedBBox bbox)
    {
        return bbox.Width * bbox.Height;
    }

    // Ratio-based containment: OCR coordinates are noisy, so a box that protrudes slightly
    // outside its container still counts as contained.
    private static bool Contains(NormalizedBBox container, NormalizedBBox contained)
    {
        const double containedRatio = 0.98;
        double width = Math.Min(container.X + container.Width, contained.X + contained.Width) -
                       Math.Max(container.X, contained.X);
        double height = Math.Min(container.Y + container.Height, contained.Y + contained.Height) -
                        Math.Max(container.Y, contained.Y);
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        return width * height / Area(contained) >= containedRatio;
    }

    private sealed class DocumentTreeRevisionRow
    {
        public string TreeRevisionId { get; set; } = string.Empty;
        public string DocumentInstanceId { get; set; } = string.Empty;
        public string PageId { get; set; } = string.Empty;
        public string? ParentTreeRevisionId { get; set; }
        public string Source { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int IsCurrent { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string? CommittedAt { get; set; }

        public DocumentTreeRevision ToRevision()
        {
            return new DocumentTreeRevision(
                DocumentTreeRevisionId.Parse(TreeRevisionId),
                Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId),
                Patchouli.Core.Ids.PageId.Parse(PageId),
                ParentTreeRevisionId is null ? null : DocumentTreeRevisionId.Parse(ParentTreeRevisionId),
                Source,
                Status,
                IsCurrent == 1,
                DateTimeOffset.Parse(CreatedAt),
                CommittedAt is null ? null : DateTimeOffset.Parse(CommittedAt));
        }
    }

    private sealed class DocumentBoxRow
    {
        public string TreeRevisionId { get; set; } = string.Empty;
        public string BoxId { get; set; } = string.Empty;
        public string DocumentInstanceId { get; set; } = string.Empty;
        public string PageId { get; set; } = string.Empty;
        public string? ParentBoxId { get; set; }
        public string? NextSiblingBoxId { get; set; }
        public string BoxType { get; set; } = string.Empty;
        public string? SubType { get; set; }
        public string? BaseType { get; set; }
        public double BBoxX { get; set; }
        public double BBoxY { get; set; }
        public double BBoxWidth { get; set; }
        public double BBoxHeight { get; set; }
        public string? PayloadJson { get; set; }
        public int? HeadingLevel { get; set; }
        public string? CodeLanguage { get; set; }
        public double? Confidence { get; set; }
        public int Suppressed { get; set; }
        public string? ContinuesFromBoxId { get; set; }

        public DocumentBox ToBox()
        {
            return new DocumentBox(
                DocumentTreeRevisionId.Parse(TreeRevisionId),
                DocumentBoxId.Parse(BoxId),
                Patchouli.Core.Ids.DocumentInstanceId.Parse(DocumentInstanceId),
                Patchouli.Core.Ids.PageId.Parse(PageId),
                ParentBoxId is null ? null : DocumentBoxId.Parse(ParentBoxId),
                NextSiblingBoxId is null ? null : DocumentBoxId.Parse(NextSiblingBoxId),
                BoxType,
                SubType,
                BaseType,
                new NormalizedBBox(BBoxX, BBoxY, BBoxWidth, BBoxHeight),
                DocumentBoxPayloadSerializer.Deserialize(BoxType, BaseType, PayloadJson),
                HeadingLevel,
                CodeLanguage,
                Confidence,
                Suppressed == 1,
                ContinuesFromBoxId is null ? null : DocumentBoxId.Parse(ContinuesFromBoxId));
        }
    }
}
