create table search_profiles (
    profile_id text primary key not null,
    library_id text not null,
    name text not null,
    description text null,
    is_system integer not null default 0 check (is_system in (0,1)),
    is_default integer not null default 0 check (is_default in (0,1)),
    archived integer not null default 0 check (archived in (0,1)),
    created_at text not null,
    updated_at text not null,
    foreign key (library_id) references library_metadata(library_id),
    unique (library_id, name)
);

create table search_rewrite_rules (
    rule_id text primary key not null,
    library_id text not null,
    profile_id text null,
    rule_type text not null,
    pattern text not null,
    replacement text not null,
    direction text not null,
    enabled integer not null default 1 check (enabled in (0,1)),
    priority integer not null default 0,
    note text null,
    created_at text not null,
    updated_at text not null,
    foreign key (library_id) references library_metadata(library_id),
    foreign key (profile_id) references search_profiles(profile_id) on delete cascade
);

create table search_settings (
    library_id text primary key not null,
    default_profile_id text null,
    last_used_profile_id text null,
    preview_before_execute integer not null default 0 check (preview_before_execute in (0,1)),
    created_at text not null,
    updated_at text not null,
    foreign key (library_id) references library_metadata(library_id),
    foreign key (default_profile_id) references search_profiles(profile_id),
    foreign key (last_used_profile_id) references search_profiles(profile_id)
);

create index idx_search_profiles_library_id on search_profiles(library_id);
create index idx_search_rewrite_rules_library_profile on search_rewrite_rules(library_id, profile_id, priority);
