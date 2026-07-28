alter table document_boxes add column continues_from_box_id text null;

create index if not exists idx_document_boxes_continues_from on document_boxes(continues_from_box_id);
