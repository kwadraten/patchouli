create table if not exists file_search_root_definitions (
    root_id text primary key not null,
    library_id text not null,
    display_name text not null,
    purpose text not null,
    is_enabled integer not null default 1,
    created_at text not null,
    updated_at text not null,
    foreign key (library_id) references library_metadata(library_id),
    check (is_enabled in (0, 1))
);

create table if not exists file_search_root_bindings (
    library_id text not null,
    root_kind text not null default 'file_search_root',
    root_id text not null,
    device_id text not null,
    provider_identity text not null default 'sqlite_migration',
    root_path text not null,
    is_available integer not null default 1,
    authorization_kind text null,
    authorization_payload blob null,
    authorization_payload_version integer null,
    authorization_updated_at text null,
    updated_at text not null,
    primary key (library_id, root_kind, root_id, device_id),
    foreign key (root_id) references file_search_root_definitions(root_id),
    check (is_available in (0, 1))
);

insert or ignore into file_search_root_definitions (
    root_id, library_id, display_name, purpose, is_enabled, created_at, updated_at)
select root_id, library_id, root_id, 'file_resolution', 1, created_at, updated_at
from file_search_roots;

insert or ignore into file_search_root_bindings (
    library_id, root_kind, root_id, device_id, provider_identity,
    root_path, is_available, authorization_kind, authorization_payload,
    authorization_payload_version, authorization_updated_at, updated_at)
select library_id, 'file_search_root', root_id, 'legacy-device', 'sqlite_migration',
       root_path, is_available, authorization_kind, authorization_payload,
       authorization_payload_version, authorization_updated_at, updated_at
from file_search_roots;

delete from file_search_roots;

create index if not exists idx_file_search_root_definitions_library_id
    on file_search_root_definitions(library_id);

create index if not exists idx_file_search_root_bindings_lookup
    on file_search_root_bindings(library_id, root_kind, device_id, root_path);
