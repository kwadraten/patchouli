-- Recreate the committed-revision immutability guard after the table rebuild in
-- 033_unified_working_revisions.sql dropped the previous trigger.
create trigger if not exists document_boxes_committed_update_guard
before update on document_boxes
when exists (
    select 1 from document_tree_revisions r
    where r.tree_revision_id = old.tree_revision_id and r.status = 'committed'
)
begin
    select raise(abort, 'committed document tree revisions are immutable');
end;