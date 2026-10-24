-- Issue #29: 接续（continues_prev）兼容状态列。
-- 标准接续（v4 显式关系或工作台人工建立）continuation_state 为 null；
-- 旧版 MinerU 空框推断产生、本框文字未恢复的接续标记为 'legacy_unrecovered'。
-- 分类与诊断写入由 ContinuationStateMigration 在同一事务中完成（见 MigrationRunner 的 054 特例），
-- 本文件只负责结构：加列 + 迁移诊断留痕表。
alter table document_boxes add column continuation_state text null;

create table if not exists continuation_migration_diagnostics (
    migration_id text not null,
    box_id text not null,
    tree_revision_id text not null,
    action text not null,
    detail text not null,
    created_at text not null
);

create index if not exists idx_continuation_migration_diagnostics_box
    on continuation_migration_diagnostics(box_id);
