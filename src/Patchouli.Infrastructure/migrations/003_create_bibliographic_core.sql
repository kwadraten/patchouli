create table if not exists items (
    item_id text primary key not null,
    library_id text not null,
    item_type text not null,
    title text not null,
    subtitle text null,
    creators_json text not null default '[]',
    date text null,
    publication_title text null,
    publisher text null,
    place text null,
    volume text null,
    issue text null,
    pages text null,
    language text null,
    abstract text null,
    tags_json text not null default '[]',
    collections_json text not null default '[]',
    custom_fields_json text not null default '{}',
    created_at text not null,
    updated_at text not null,
    foreign key (library_id) references library_metadata(library_id),
    check (length(trim(title)) > 0),
    check (length(trim(item_type)) > 0)
);

create table if not exists item_identifiers (
    identifier_id text primary key not null,
    item_id text not null,
    scheme text not null,
    value text not null,
    note text null,
    created_at text not null,
    foreign key (item_id) references items(item_id) on delete cascade,
    unique (item_id, scheme, value),
    check (length(trim(scheme)) > 0),
    check (length(trim(value)) > 0)
);

create table if not exists file_assets (
    file_asset_id text primary key not null,
    library_id text not null,
    original_path text not null,
    file_name text not null,
    size_bytes integer not null,
    mtime_utc text null,
    quick_hash text null,
    full_blake3 text null,
    page_count integer null,
    pdf_trailer_id text null,
    status text not null,
    created_at text not null,
    updated_at text not null,
    foreign key (library_id) references library_metadata(library_id),
    check (status in ('available', 'moved_candidate', 'missing', 'offline_root', 'conflict', 'changed'))
);

create table if not exists document_instances (
    document_instance_id text primary key not null,
    item_id text not null,
    file_asset_id text null,
    title text null,
    instance_type text not null,
    is_primary integer not null default 0,
    status text not null,
    created_at text not null,
    updated_at text not null,
    foreign key (item_id) references items(item_id) on delete cascade,
    foreign key (file_asset_id) references file_assets(file_asset_id),
    check (length(trim(instance_type)) > 0),
    check (is_primary in (0, 1)),
    check (status in ('active', 'deprecated', 'partial', 'missing_source'))
);

create index if not exists idx_items_library_id on items(library_id);
create index if not exists idx_file_assets_library_id on file_assets(library_id);
create index if not exists idx_document_instances_item_id on document_instances(item_id);
create index if not exists idx_document_instances_file_asset_id on document_instances(file_asset_id);
