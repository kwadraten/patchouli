create table if not exists pages (
    page_id text primary key not null,
    document_instance_id text not null,
    page_index integer not null,
    page_label text null,
    width real null,
    height real null,
    rotation integer not null default 0,
    coordinate_basis text not null,
    basis_width real null,
    basis_height real null,
    renderer_basis_version text not null,
    source_file_hash text null,
    created_at text not null,
    updated_at text not null,
    foreign key (document_instance_id) references document_instances(document_instance_id) on delete cascade,
    unique (document_instance_id, page_index),
    check (page_index >= 0),
    check (rotation in (0, 90, 180, 270))
);

create table if not exists document_tree_revisions (
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
    foreign key (document_instance_id) references document_instances(document_instance_id) on delete cascade,
    foreign key (page_id) references pages(page_id) on delete cascade,
    foreign key (parent_tree_revision_id) references document_tree_revisions(tree_revision_id),
    check (source in ('import', 'manual_edit', 'ocr_adopted', 'migration')),
    check (status in ('staging', 'draft', 'committed', 'discarded')),
    check (is_current in (0, 1)),
    check ((status = 'committed' and committed_at is not null) or status <> 'committed'),
    check (is_current = 0 or status = 'committed')
);

create table if not exists document_boxes (
    tree_revision_id text not null,
    box_id text not null,
    document_instance_id text not null,
    page_id text not null,
    parent_box_id text null,
    next_sibling_box_id text null,
    box_type text not null,
    sub_type text null,
    base_type text null,
    bbox_x real not null,
    bbox_y real not null,
    bbox_width real not null,
    bbox_height real not null,
    payload_json text null,
    heading_level integer null,
    code_language text null,
    confidence real null,
    suppressed integer not null default 0,
    primary key (tree_revision_id, box_id),
    foreign key (tree_revision_id) references document_tree_revisions(tree_revision_id) on delete cascade,
    foreign key (document_instance_id) references document_instances(document_instance_id) on delete cascade,
    foreign key (page_id) references pages(page_id) on delete cascade,
    foreign key (tree_revision_id, parent_box_id)
        references document_boxes(tree_revision_id, box_id) deferrable initially deferred,
    foreign key (tree_revision_id, next_sibling_box_id)
        references document_boxes(tree_revision_id, box_id) deferrable initially deferred,
    check (length(trim(box_type)) > 0),
    check (base_type is null or base_type in ('text', 'image', 'table', 'code', 'unknown')),
    check (bbox_x >= 0 and bbox_x <= 1 and bbox_y >= 0 and bbox_y <= 1),
    check (bbox_width > 0 and bbox_width <= 1 and bbox_height > 0 and bbox_height <= 1),
    check (bbox_x + bbox_width <= 1 and bbox_y + bbox_height <= 1),
    check (heading_level is null or heading_level between 1 and 6),
    check (confidence is null or confidence between 0 and 1),
    check (suppressed in (0, 1)),
    check (box_id <> coalesce(parent_box_id, '')),
    check (box_id <> coalesce(next_sibling_box_id, ''))
);

create index if not exists idx_pages_document_instance_id on pages(document_instance_id);
create index if not exists idx_document_tree_revisions_page on document_tree_revisions(document_instance_id, page_id);
create unique index if not exists uq_document_tree_current_page
    on document_tree_revisions(document_instance_id, page_id) where is_current = 1;
create index if not exists idx_document_tree_revisions_source_basis
    on document_tree_revisions(document_instance_id, source_full_blake3, source_basis_status);
create index if not exists idx_document_boxes_page_revision
    on document_boxes(page_id, tree_revision_id);
create index if not exists idx_document_boxes_parent
    on document_boxes(tree_revision_id, parent_box_id);
create unique index if not exists uq_document_boxes_sibling_predecessor
    on document_boxes(tree_revision_id, next_sibling_box_id) where next_sibling_box_id is not null;

create trigger if not exists document_boxes_committed_update_guard
before update on document_boxes
when exists (
    select 1 from document_tree_revisions r
    where r.tree_revision_id = old.tree_revision_id and r.status = 'committed'
)
begin
    select raise(abort, 'committed document tree revisions are immutable');
end;
