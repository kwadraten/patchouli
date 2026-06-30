create table if not exists provider_credentials (
    credential_id text primary key not null,
    library_id text not null,
    provider_id text not null,
    display_name text not null,
    secret_value text not null,
    status text not null,
    created_at text not null,
    updated_at text not null,
    foreign key (library_id) references library_metadata(library_id)
);
create table if not exists provider_credential_bindings (
    binding_id text primary key not null,
    credential_id text not null,
    preset_id text null,
    provider_id text not null,
    status text not null,
    created_at text not null,
    updated_at text not null,
    foreign key (credential_id) references provider_credentials(credential_id),
    foreign key (preset_id) references ocr_presets(preset_id)
);
create index if not exists idx_provider_credentials_library_id on provider_credentials(library_id);
create index if not exists idx_provider_credentials_provider_id on provider_credentials(provider_id);
create index if not exists idx_credential_bindings_credential_id on provider_credential_bindings(credential_id);
create index if not exists idx_credential_bindings_preset_id on provider_credential_bindings(preset_id);
