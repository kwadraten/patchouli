-- Nonunique: manual records may deliberately share a file, and resolved/changed assets retain IDs.
create index if not exists idx_file_assets_library_full_blake3
    on file_assets(library_id, full_blake3)
    where full_blake3 is not null;
