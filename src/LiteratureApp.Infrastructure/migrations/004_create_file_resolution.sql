create table if not exists file_search_roots (
    root_id text primary key not null,
    library_id text not null,
    root_path text not null,
    is_available integer not null default 1,
    created_at text not null,
    updated_at text not null,
    foreign key (library_id) references library_metadata(library_id),
    unique (library_id, root_path),
    check (is_available in (0, 1))
);

create table if not exists known_file_locations (
    location_id text primary key not null,
    file_asset_id text not null,
    path text not null,
    last_seen_at text not null,
    status text not null,
    foreign key (file_asset_id) references file_assets(file_asset_id) on delete cascade,
    unique (file_asset_id, path),
    check (status in ('available', 'moved_candidate', 'missing', 'conflict', 'changed'))
);

create index if not exists idx_file_search_roots_library_id on file_search_roots(library_id);
create index if not exists idx_known_file_locations_file_asset_id on known_file_locations(file_asset_id);
