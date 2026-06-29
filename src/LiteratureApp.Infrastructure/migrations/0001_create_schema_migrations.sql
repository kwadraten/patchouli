create table if not exists schema_migrations (
    id text primary key not null,
    name text not null,
    applied_at text not null
);
