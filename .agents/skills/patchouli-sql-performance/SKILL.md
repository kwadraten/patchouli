---
name: patchouli-sql-performance
description: 本仓库 SQLite 查询与迁移的性能纪律：编写或审查 SQL（Dapper 查询、迁移 .sql、FTS 查询）时使用，防止全表扫描、N+1 探测、OFFSET 分页、错误的 FTS 驱动计划等已被历史修复否定的写法。
---

# Patchouli SQL 性能纪律

这些规则全部来自本仓库已经修过的真实性能回归（参见每条末尾的 commit）。不要重新引入它们。

## 禁止清单（反模式 → 正确做法）

### 1. 禁止用应用层逐行探测判断行是否被引用（N+1）

**错误**：`select * from T where status = ...` 取出全部候选行，然后在 C# 循环里逐行执行 `IsReferencedAsync` 一类的探测查询 —— 表多大就发多少次查询，且候选集本身就是一次全表扫描。

**正确**：

- 引用判断放进 SQL：一条 `not exists (...)` 或 anti-join 把候选集在数据库内收窄；
- 已知候选 ID 时把集合传进 `in (select value from json_each(@Ids))`（Dapper 对 `in @Ids` 的展开在参数多时也能用，但 json_each 是仓库现行惯例），只扫描需要的行；
- 引用来源本身是慢的外部状态（如远端 snapshot 清单）时，用有界 cache（见 `FileAssetGcService._snapshotShardCache`，LRU 上限 256）而不是每次全量重查。

参考：`FileAssetGcService`（9cdd2a3）、`LibraryItemQueryService.GetDocumentItemIdsAsync`（db0c1eb —— 批量 `json_each` 关联查询替代逐文档查询）。

### 2. 禁止一次性把整库加载进 UI / 内存

**错误**：列表页一次性 `select * from items order by created_at desc` 然后绑定到 DataGrid，或先取全量再在 C# 里 filter/skip。

**正确**：keyset（游标）分页，固定页大小（本仓库 `LibraryPageSize = 100`）：

```sql
where i.deleted_at is null and i.merged_into_item_id is null
  and (i.created_at, i.item_id) < (@AfterCreatedAt, @AfterItemId)  -- keyset
order by i.created_at desc, i.item_id desc
limit @Limit;
```

- 游标必须含稳定 tie-breaker（`created_at, item_id` 元组），禁止 OFFSET；
- 排序键上的 WHERE 子句需要配套索引，如 `idx_items_library_trash_created on items(library_id, created_at desc, item_id desc) where deleted_at is not null ...`（部分索引 + 排序方向一致）；
- 翻页必须可取消（`CancellationToken` + generation 检查，见 `LibraryShellViewModel`），且 UI 端要防并发翻页（`_pageLoadInFlight`）。

参考：8c43c8b（keyset 分页）、`PageCursor`（src/Patchouli.Core/Pagination/PageCursor.cs）。

### 3. 禁止把可下推的过滤留在 C# 层

**错误**：先取回一页/全量 item，再在 ViewModel 里按 collection、untagged 等条件过滤 —— 结果页被过滤掏空、翻页错位、统计数不准。

**正确**：所有生命周期/归属条件都下推到同一条分页查询的 WHERE：

```sql
and i.item_id in (select ic.item_id from item_collections ic where ic.collection_id = @RequiredCollectionId)
and not exists (select 1 from item_tag_memberships utm where utm.item_id = i.item_id)  -- untagged
```

`count` 查询与分页查询必须使用同一套谓词，否则"总数"和"页"不一致。

参考：3cd3265（collection/untagged 下推）。

### 4. 禁止在热路径上对 JSON 列做行内解析

**错误**：`where json_valid(i.tags_json) and json_array_length(i.tags_json) = 0`、按 `json_extract` 过滤/排序 —— 每行都解析 JSON，无法走索引。

**正确**：触发器维护的**投影表**（projection），查询只查投影：

- 迁移 `045_project_item_tags.sql`：`item_tag_memberships(item_id, ordinal, tag, library_id, is_active)` + `items` 上的 insert/update/delete 触发器保持同步；注意 update 触发器用 `after update of <列>` 限定列；
- 投影表要把过滤要用的派生列物化（`is_active`、`library_id`），索引建在 `(tag, is_active, item_id)` 这类复合键上；
- 投影是可重建的派生状态：初始 `insert ... select ... from json_each(...)`，并防御 `json_valid`/`json_type` 脏数据。

同类先例：`file_asset_payload_refs`（044）、`fts_row_map`（043）。参考：9cdd2a3。

### 5. 禁止让 FTS 查询被错误的关系子句"抢驱动"

**错误**（这是最难自查的一类）：FTS MATCH 查询里用

```sql
where search_units_fts match @Match
  and search_units_fts.rowid in (select fts_row_id from fts_row_map where document_instance_id = @DocId)
```

SQLite 会把这个 `IN` 子查询选为驱动循环：对文档内每一行逐行验证 MATCH，整书搜索比全库搜索还慢。

**正确**：让 MATCH 的 postings 枚举保持驱动，范围条件加在 map 表上（`fm.document_instance_id = @DocId`），越界命中由 `fts_row_map` 主键探测丢弃。

规则：**写 FTS 组合查询时必须 `EXPLAIN QUERY PLAN` 验证 MATCH 是驱动项**；不要把 FTS rowid 包在 `IN (子查询)` 里当范围条件。

参考：6fa1623。

### 6. 禁止缺少外键/局部作用域索引导致的全表遍历

任何按 `document_instance_id`、`page_id`、父修订、retry 链等局部范围做的读写，都必须有对应索引——否则局部操作会遍历无关文档的 payload 与修订历史。迁移 `042_index_scoped_reads_and_foreign_keys.sql` 是一次集中补课，新增表/新查询面时按同样思路补齐：

- 覆盖型复合索引：如 `search_units(page_id, tree_revision_id, status, ordinal, unit_id)`，列序 = 等值谓词在前、排序键在后；
- 部分索引（`where deleted_at is not null ...`）匹配特定列表视图，比全索引便宜；
- 新的反向引用列（`supersedes_unit_id` / `superseded_by_unit_id`、`parent_commit_id`、`retry_of_run_id`）一引入就要建索引。

参考：9cdd2a3。

### 7. 快照/分片导出的纪律

- 先 `checkpoint` + `BackupDatabase` 到临时副本，再在副本上校验 schema、算 hash、切 shard —— 不在活动库上边读边写；
- 派生/本地状态表（`search_units_fts`、`fts_row_map`、`item_tag_memberships`、`file_asset_payload_refs`、`local_maintenance_state`）不进 snapshot shard，恢复后由 `fts_cache_state`/受控重建恢复；
- 复制 schema 时用 `pragma_table_list ... type='shadow'` 跳过 FTS5 shadow 表，避免 DDL 冲突。

参考：9cdd2a3 `SnapshotServices.cs`。

## 索引不是默认答案：索引 vs 内存/投影的决策

上面的规则**不**意味着"每个过滤列都建索引"。索引用写延迟、存储、page-cache 竞争和迁移复杂度换读性能；本仓库是单文件 SQLite + WAL，写放大尤其真实（每个索引 insert 多一棵 B-tree 的页写入；随机键索引如 UUID/文本 ID 会把每次 insert 打到随机页，page cache 溢出即 spill）。建索引前过一遍这个决策：

**先问三个问题（GitLab 索引指南的经典三问）：**
1. 能否改写查询复用现有索引？—— `(a, b)` 复合索引已覆盖 `a` 的查询（最左前缀），再给 `a` 单列建索引是纯写成本；反之只过滤 `b` 的查询用不上它，不要假设能用。
2. 数据量是否大到索引真的比顺序扫描快？—— 表只有几百~几千行时 SQLite 常常直接忽略索引走全扫，索引只剩写开销。
3. 省的读时间值不值得付的写维护费？—— 看读写比：OCR 结果、search_units 这类批量写表上的冷门过滤列不要建索引。

**倾向建索引/投影的场景：**
- 过滤会随整库增长、且是热路径（UI 首屏、分页、MCP find）：必须有索引，不接受全扫——这正是第 2/3/6 条禁止项。
- 等值谓词选择性高（能砍掉大部分行）；低基数列（bool、三值 status）单独建索引基本无用，要用就用部分索引把谓词写进索引定义。
- 谓词表达式无法被索引（JSON 解析、函数包裹列如 `lower(x)`）→ 走投影表/生成列，而不是在热路径行内计算。
- 外键与反向引用列：关系遍历用得上才建；只为"将来可能"建是投机。

**倾向内存筛选/不建索引的场景：**
- 数据已按上一条谓词收窄到小集合（几百行内）——后续条件在 C# 过滤比加索引便宜且灵活。
- 一次性/低频操作（迁移期检查、维护任务、诊断导出）：跑一次的全扫可以接受，为它留一个永久索引不划算；必要时用临时表/`WITHOUT ROWID` 的"伪索引"临时结构。
- 候选集由外部状态决定（snapshot 清单）——已有第 1 条的有界 cache 方案。
- 写多读少：索引只应服务已知的热查询，每个新索引都要能回答"哪条查询因为它变快"。

**验证而非假设：**
- 任何新索引落地前 `EXPLAIN QUERY PLAN` 确认计划从 `SCAN` 变 `SEARCH`，并确认部分索引的谓词与查询谓词**逐字匹配**（不匹配 planner 不会用）；
- 写放大可用 `PRAGMA stats`/行写入计数验证：UPDATE 只重写覆盖到被改列的索引，部分索引只在行匹配谓词时付出写成本——利用这两点可以让索引在热写路径上接近免费；
- 定期审计：`select name, sql from sqlite_schema where type='index' and tbl_name=...`，删掉无热查询背书的冗余索引（如被复合索引覆盖的单列索引）。

参考：GitLab "Adding database indexes" 三问框架；Lumenize/mehdi.cz 的 SQLite 写放大与部分索引成本实测；sqlite.org 论坛关于查询期临时索引的讨论。

## 审查检查单

改动 `src/Patchouli.Infrastructure/**` 中的 SQL 或 `migrations/*.sql` 时逐条自问：

1. 这条查询的扫描范围是否被谓词/索引限定在"该文档/该页/该库"，还是会随整库增长？
2. 是否有行外查询（循环里的 `QueryAsync`/`ExecuteScalarAsync`）？能否改 `json_each(@Ids)` 批量或 `exists` 子查询？
3. 是否有 `select *` 或全量拉回内存后 C# 过滤？能否 keyset 分页 + 谓词下推？
4. 是否有 `json_extract`/`json_valid` 出现在 WHERE/ORDER BY？应走投影表。
5. FTS 组合查询是否 `EXPLAIN QUERY PLAN` 验证过 MATCH 驱动？
6. 新增外键/反向引用是否配套索引？部分索引是否更省？反过来：这个新索引服务的是哪条已确认的热查询？有没有现有复合索引的最左前缀已覆盖？数据量/读写比是否值得？
7. 分页 count 与分页 select 谓词是否一致？
8. UI 侧：翻页/刷新是否可取消、防重入，长事务/重 IO 是否在后台线程而非 Dispatcher（cdba9c2、622dfff）？

## 验证

- 测试侧有现成模式：`BoxTreeReadSurfaceTests`、`DocumentTreeValidationTests`、`LibraryItemQueryServiceTests`、`SearchUnitFtsBoxTreeTests` 用大数据量夹具断言扫描范围与计划行为；
- 性能回归跑 `dotnet fsi scripts/run-tests.fsx -- --suite Performance`（不入常规门禁）；
- 对可疑计划直接在测试库里 `EXPLAIN QUERY PLAN` 对照。
