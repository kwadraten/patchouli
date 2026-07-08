create table if not exists mcp_server_settings (
    settings_id text primary key not null,
    port integer not null,
    bind_address text not null,
    cors_enabled integer not null default 0,
    allowed_origins_json text not null default '[]',
    auth_required integer not null default 1,
    token text null,
    updated_at text not null
);

create table if not exists mcp_tool_overrides (
    tool_name text primary key not null,
    enabled integer not null,
    disabled_reason text null
);
