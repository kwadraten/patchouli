alter table layout_revisions add column source_full_blake3 text null;
alter table layout_revisions add column source_basis_status text not null default 'current';

update layout_revisions
set source_full_blake3 = (
    select f.full_blake3
    from document_instances d
    join file_assets f on f.file_asset_id = d.file_asset_id
    where d.document_instance_id = layout_revisions.document_instance_id
)
where source_full_blake3 is null;

create index if not exists idx_layout_revisions_source_basis
    on layout_revisions(document_instance_id, source_full_blake3, source_basis_status);
