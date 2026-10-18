-- Issue #11: durable recovery records for local file imports. File moves and DB
-- commits are not one transaction; when an import fails after a move the service
-- attempts to restore the original position, and every restore attempt (successful
-- or not) is recorded here. This table lives in the runtime database — a
-- repo/runtime-agreed durable location, never temp cache.
create table if not exists file_import_recovery_records (
    record_id text primary key not null,
    created_at text not null,
    trigger text not null,
    kind text not null,
    original_path text not null,
    moved_path text null,
    restored_path text null,
    status text not null,
    diagnosis text not null,
    check (status in (
        'move_failed',
        'import_failed_restored',
        'import_failed_restore_failed',
        'import_failed_no_move'
    ))
);

create index if not exists idx_file_import_recovery_records_created
    on file_import_recovery_records(created_at);
