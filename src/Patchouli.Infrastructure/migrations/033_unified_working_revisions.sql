pragma foreign_keys = off;

-- The trigger is dropped here and recreated in the following migration so that the
-- table rebuild can drop document_tree_revisions without leaving a trigger body
-- referencing the table being rebuilt.
drop trigger if exists document_boxes_committed_update_guard;

create table document_tree_revisions_new (
    tree_revision_id text primary key not null,
    document_instance_id text not null,
    page_id text not null,
    parent_tree_revision_id text null,
    source text not null,
    status text not null,
    is_current integer not null default 0,
    source_full_blake3 text null,
    source_basis_status text not null default 'current',
    edit_session_id text null unique,
    created_at text not null,
    committed_at text null,
    reverted_from_tree_revision_id text null,
    foreign key (document_instance_id) references document_instances(document_instance_id) on delete cascade,
    foreign key (page_id) references pages(page_id) on delete cascade,
    foreign key (parent_tree_revision_id) references document_tree_revisions_new(tree_revision_id),
    foreign key (reverted_from_tree_revision_id) references document_tree_revisions_new(tree_revision_id),
    check (source in ('import', 'manual_edit', 'ocr_adopted', 'migration', 'revert')),
    check (status in ('working', 'committed', 'staging', 'draft', 'discarded')),
    check (is_current in (0, 1)),
    check ((status = 'committed' and committed_at is not null) or status <> 'committed'),
    check (is_current = 0 or status = 'committed')
);

insert into document_tree_revisions_new (
    tree_revision_id, document_instance_id, page_id, parent_tree_revision_id,
    source, status, is_current, source_full_blake3, source_basis_status,
    edit_session_id, created_at, committed_at, reverted_from_tree_revision_id)
select
    tree_revision_id, document_instance_id, page_id, parent_tree_revision_id,
    source, status, is_current, source_full_blake3, source_basis_status,
    edit_session_id, created_at, committed_at, null
from document_tree_revisions;

drop table document_tree_revisions;

alter table document_tree_revisions_new rename to document_tree_revisions;

create index idx_document_tree_revisions_page on document_tree_revisions(document_instance_id, page_id);
create unique index uq_document_tree_current_page
    on document_tree_revisions(document_instance_id, page_id) where is_current = 1;
create index idx_document_tree_revisions_source_basis
    on document_tree_revisions(document_instance_id, source_full_blake3, source_basis_status);

pragma foreign_keys = on;