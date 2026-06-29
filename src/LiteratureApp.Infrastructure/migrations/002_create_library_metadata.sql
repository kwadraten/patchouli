create table if not exists library_metadata (
    library_id text primary key not null,
    display_name text not null,
    schema_version integer not null,
    created_at text not null,
    updated_at text not null
);
