create index if not exists idx_document_boxes_revision_visible_payload
    on document_boxes(tree_revision_id)
    where suppressed = 0 and payload_json is not null;
