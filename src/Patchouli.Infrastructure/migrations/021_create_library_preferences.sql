create table if not exists library_preferences (
    library_id text not null,
    scope text not null,
    columns_json text not null,
    updated_at text not null,
    primary key (library_id, scope),
    foreign key (library_id) references library_metadata(library_id) on delete cascade
);
