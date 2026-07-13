create table if not exists library_setting_records (
    setting_key text primary key not null,
    schema_version integer not null,
    value_json text not null,
    revision integer not null,
    updated_at text not null,
    updated_by_device_id text not null,
    merge_policy text not null,
    check (schema_version >= 1),
    check (revision >= 1)
);
