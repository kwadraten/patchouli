create table if not exists csl_styles (
    style_id text primary key not null,
    display_name text not null,
    default_locale text null,
    source_url text null,
    source_kind text not null,
    content_hash text not null,
    installed_at text not null,
    updated_at text not null,
    enabled integer not null default 1,
    deleted integer not null default 0
);

create table if not exists csl_settings (
    settings_id text primary key not null,
    default_style_id text null,
    locale text null,
    updated_at text not null,
    foreign key (default_style_id) references csl_styles(style_id)
);
