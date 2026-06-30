create table if not exists search_units (
    unit_id text primary key not null,
    document_instance_id text not null,
    page_id text not null,
    root_node_id text not null,
    text_revision_id text not null,
    bbox_revision_id text not null,
    layout_revision_id text not null,
    resolved_text text not null,
    bbox_union_json text null,
    node_type text not null,
    reading_order integer not null,
    status text not null,
    supersedes_unit_id text null,
    superseded_by_unit_id text null,
    created_at text not null,
    updated_at text not null,
    foreign key (document_instance_id) references document_instances(document_instance_id) on delete cascade,
    foreign key (page_id) references pages(page_id) on delete cascade,
    foreign key (root_node_id) references layout_nodes(node_id) on delete cascade,
    foreign key (layout_revision_id) references layout_revisions(layout_revision_id) on delete cascade,
    foreign key (supersedes_unit_id) references search_units(unit_id),
    foreign key (superseded_by_unit_id) references search_units(unit_id)
);

create table if not exists search_index_status (
    scope_type text not null,
    scope_id text not null,
    status text not null,
    pending_document_count integer not null default 0,
    pending_unit_count integer not null default 0,
    progress_percent real null,
    affected_scopes_summary text null,
    reason text null,
    updated_at text not null,
    primary key (scope_type, scope_id)
);

create virtual table if not exists search_units_fts using fts5(
    unit_id unindexed,
    document_instance_id unindexed,
    page_id unindexed,
    resolved_text,
    tokenize = 'unicode61'
);

create index if not exists idx_search_units_document_instance_id on search_units(document_instance_id);
create index if not exists idx_search_units_page_id on search_units(page_id);
create index if not exists idx_search_units_layout_revision_id on search_units(layout_revision_id);
create index if not exists idx_search_units_root_node_id on search_units(root_node_id);
create index if not exists idx_search_units_status on search_units(status);
