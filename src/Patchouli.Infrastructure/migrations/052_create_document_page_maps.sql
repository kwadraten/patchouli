-- Chapter structure and printed page-number maps (issue #4).
-- One row per document instance holds the authoritative structured page-map definition
-- (pagination rules, explicit page labels, chapter tree, and the write-time structural
-- signatures of logical_page rules). The MCP map.yml resource is a virtual-file projection
-- of this row; full per-page maps are never persisted. Deleting a document instance
-- cascades to its map so the map lifecycle follows the document lifecycle.
create table if not exists document_page_maps (
    document_instance_id text primary key,
    definition_json text not null,
    created_at text not null,
    updated_at text not null,
    foreign key (document_instance_id) references document_instances(document_instance_id) on delete cascade
);

create index if not exists idx_document_page_maps_updated_at
    on document_page_maps(updated_at);
