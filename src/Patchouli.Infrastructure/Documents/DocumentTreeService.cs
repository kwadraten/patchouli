using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Documents;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Time;
using Patchouli.Infrastructure.Database;

namespace Patchouli.Infrastructure.Documents;

public sealed class DocumentTreeService : IDocumentTreeService, IDocumentTreeEditor
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly DocumentTreeValidator _validator;

    public DocumentTreeService(
        SqliteConnectionFactory connectionFactory,
        IClock clock,
        IMarkdownEngine markdownEngine)
    {
        _connectionFactory = connectionFactory;
        _clock = clock;
        _validator = new DocumentTreeValidator(markdownEngine);
    }

    public async Task<Result> ValidateStoredTreesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            IEnumerable<DocumentTreeRevisionRow> revisions = await connection.QueryAsync<DocumentTreeRevisionRow>(
                SelectRevisionSql + " order by tree_revision_id;");
            foreach (DocumentTreeRevisionRow revisionRow in revisions)
            {
                DocumentTreeRevision revision = revisionRow.ToRevision();
                Result validation = _validator.Validate(
                    revision,
                    await GetBoxesAsync(connection, null, revision.TreeRevisionId),
                    revision.Status != DocumentTreeRevisionStatus.Staging);
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
                        confidence, suppressed)
                    select @DraftRevisionId, box_id, document_instance_id, page_id, parent_box_id,
                        next_sibling_box_id, box_type, sub_type, base_type, bbox_x, bbox_y,
                        bbox_width, bbox_height, payload_json, heading_level, code_language,
                        confidence, suppressed
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
                    seed.Suppressed);
            }).ToArray();
            // Provider output remains a candidate until adoption resolves any geometry conflicts.
            Result validation = _validator.Validate(revision, staged, false);
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
            List<(DocumentTreeRevision Revision, DocumentBox[] Boxes)> staging = [];
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

                staging.Add((revision, boxes));
            }

            List<DocumentTreeRevision> committedRevisions = [];
            foreach ((DocumentTreeRevision stagingRevision, DocumentBox[] stagingBoxes) in staging)
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
                    _clock.UtcNow.ToUniversalTime());

                await ClearCurrentAsync(connection, transaction, stagingRevision.DocumentInstanceId,
                    stagingRevision.PageId);
                await InsertRevisionAsync(connection, transaction, committed, null);
                await ReplaceBoxesAsync(
                    connection,
                    transaction,
                    committed.TreeRevisionId,
                    stagingBoxes.Select(box => box with { TreeRevisionId = committed.TreeRevisionId }).ToArray());
                await connection.ExecuteAsync(
                    "update document_tree_revisions set status = 'discarded' where tree_revision_id = @RevisionId;",
                    new { RevisionId = stagingRevision.TreeRevisionId.ToString() },
                    transaction);
                await MarkSearchStaleAsync(connection, transaction, committed.DocumentInstanceId, committed.PageId);
                committedRevisions.Add(committed);
            }

            return Result<IReadOnlyList<DocumentTreeRevision>>.Success(committedRevisions);
        }, cancellationToken);
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
        }, cancellationToken);
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

            DocumentBox updated = boxes[index] with
            {
                BoxType = command.BoxType.Trim(),
                Payload = command.Payload,
                HeadingLevel = command.HeadingLevel,
                CodeLanguage = NullIfWhiteSpace(command.CodeLanguage),
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
                NextSiblingBoxId = original.NextSiblingBoxId
            };
            first = first with { NextSiblingBoxId = second.BoxId };
            ReplacePredecessor(boxes, original, first.BoxId);
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
                    : null
            };
            ReplacePredecessor(boxes, first, merged.BoxId);
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
            if (parent is null || parent.BoxType != DocumentBoxType.LogicalPage)
            {
                return Result<DocumentBoxId?>.Failure(
                    AppErrorCodes.ValidationFailed,
                    "The insertion parent must be an existing logical_page in the draft.");
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
        CancellationToken cancellationToken)
    {
        try
        {
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            Result<T> result = await action(connection, transaction);
            if (result.IsFailure)
            {
                await transaction.RollbackAsync(cancellationToken);
                return result;
            }

            await transaction.CommitAsync(cancellationToken);
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
            await using SqliteConnection connection = _connectionFactory.CreateConnection();
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
            SelectBoxesSql + " where tree_revision_id = @RevisionId order by box_id;",
            new { RevisionId = revisionId.ToString() },
            transaction);
        return rows.Select(row => row.ToBox()).ToArray();
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
        foreach (DocumentBox box in boxes)
        {
            await connection.ExecuteAsync(
                """
                insert into document_boxes (
                    tree_revision_id, box_id, document_instance_id, page_id, parent_box_id,
                    next_sibling_box_id, box_type, sub_type, base_type, bbox_x, bbox_y,
                    bbox_width, bbox_height, payload_json, heading_level, code_language,
                    confidence, suppressed)
                values (@TreeRevisionId, @BoxId, @DocumentInstanceId, @PageId, @ParentBoxId,
                    @NextSiblingBoxId, @BoxType, @SubType, @BaseType, @BBoxX, @BBoxY,
                    @BBoxWidth, @BBoxHeight, @PayloadJson, @HeadingLevel, @CodeLanguage,
                    @Confidence, @Suppressed);
                """,
                new
                {
                    TreeRevisionId = box.TreeRevisionId.ToString(),
                    BoxId = box.BoxId.ToString(),
                    DocumentInstanceId = box.DocumentInstanceId.ToString(),
                    PageId = box.PageId.ToString(),
                    ParentBoxId = box.ParentBoxId?.ToString(),
                    NextSiblingBoxId = box.NextSiblingBoxId?.ToString(),
                    box.BoxType,
                    box.SubType,
                    box.BaseType,
                    BBoxX = box.BBox.X,
                    BBoxY = box.BBox.Y,
                    BBoxWidth = box.BBox.Width,
                    BBoxHeight = box.BBox.Height,
                    PayloadJson = DocumentBoxPayloadSerializer.Serialize(box.Payload),
                    box.HeadingLevel,
                    box.CodeLanguage,
                    box.Confidence,
                    Suppressed = box.Suppressed ? 1 : 0
                },
                transaction);
        }
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
            suppressed as Suppressed
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
                Suppressed == 1);
        }
    }
}
