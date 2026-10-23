# 引用、题录与页码映射

## 生成引用

```text
patchouli-cli cite <REF>... [--style <csl-style-uri>] [--locale <locale>] [--bibliography] [--html]
```

- REF 可以是题录、文献实例、页面或证据 URI。宿主只沿持久化关系解析到所属 Item，不会凭标题或文件名猜测。要确认某个 URI 能否引用，用 `find --long` 查看 `citable`，或用 `--where citable=true` 筛选。
- 全文检索返回的证据 URI（`…page-N.xml?rev=…&box=…`）可以直接作为 REF。
- 省略 `--style` 时使用用户设定的默认样式；默认样式不可用时会回退，并在 warning 中给出实际使用的样式（`meta.effective_style_uri`）。
- 多个 REF 逐项返回结果：可渲染的与失败的可以同时存在。`--bibliography` 会对解析到同一 Item 的 REF 去重。
- `general` 类型的题录只能通过显式的 `@misc` 回退参与引用，并带 `general_as_misc` warning；字段不足时返回 `NOT_CITABLE`。`runs/` 下的状态 URI 永远不可引用。

## CSL 样式

- `add <path.csl>` 安装本地样式：新 ID 安装，同 ID 同内容复用，同 ID 不同内容报冲突（之后用 `put` 更新）。缺少父样式时会报告依赖，不会自动下载，也不会改变默认样式。
- `put patchouli://csl-styles/{id}.csl` 以完整 CSL XML 原子替换。

## 修改题录

- `fetch patchouli://items/{item-id}.yml` 得到 YAML（profile `patchouli-item-1`）：原生 CSL 字段位于 `csl`，应用数据位于 `extensions.patchouli`。
- `put` 以完整 YAML 替换。URI 决定身份：Item 身份和 citation key 不能通过替换修改；集合关系只读；用户设置不暴露标签时，传入的标签会被忽略。
- YAML 只能有一个文档，键不能重复，不支持自定义 tag 或锚点。日期使用 CSL 嵌套的 `date-parts` 数组，可以表示区间；纯文本日期可以不写 `date-parts`。
- 把 CSL `type` 改成受支持的其他类型属于显式类型修正，宿主会按新类型保存。
- 回收站中的题录返回 `ITEM_IN_TRASH`；已合并的题录返回 `ITEM_MERGED`，诊断中给出合并目标。

## 在线补全元数据：`runs/metadata/control`

| action | target | args | 结果 |
|---|---|---|---|
| `lookup` | `items/{id}.yml` | `{scheme, value}` | 同步返回只读候选 `{item_id, expected_updated_at, candidate, attempts}`，不写入 |
| `apply` | `items/{id}.yml` | `{expected_updated_at, candidate, attempts}` | 校验候选结构与题录版本后原子写入；版本冲突返回冲突错误 |
| `enrich` | `items/{id}.yml` | `{}` | 用题录已有的标识符按来源优先级查找，并应用第一个成功的候选 |
| `enrich_batch` | `items/` | `{item_ids: [...]}` | 后台受理；逐项结果通过 `runs/metadata/{task-id}/status` 查询 |
| `cancel` / `retry` | `runs/metadata/{task-id}/` | `{}` | 取消时保留已提交的项；`retry` 只重试失败的项，并返回新任务 |

- `lookup` 会把 DOI、ISBN 等标识符发送给外部来源（Crossref、DataCite、OpenAlex、Open Library、国家图书馆目录等），涉及隐私时先征得用户同意。
- 推荐流程：先 `lookup`，把候选展示给用户；用户确认后，再用返回的 `expected_updated_at` 调用 `apply`。只有在用户已明确授权自动写入时，才直接使用 `enrich`。
- 来源的启用与顺序由用户在“元数据来源”设置中决定；每个来源都是一个 FSX 扩展（见 [EXTENDING](EXTENDING.md)）。

## 章节与印刷页码：`map.yml`

`patchouli://texts/{document-id}/map.yml`（profile `patchouli-page-map-1`）保存一份文献的章节树与印刷页码规则。即使还没有 map，它也能被发现：`fetch` 返回 `map_present: false` 的空映射，可以直接首次 `put`。

- 导航位置是 `pdf_page`（一基物理页）加可选的 `logical_page`（逻辑页的 Box ID）。
- 分页规则：
  - `physical_page` 规则按 `printed = printed_start + pdf_page - start` 推算（两端都包含）。
  - `logical_page` 规则在范围内按物理页顺序和给定的阅读顺序逐个加 1。
  - `page_labels` 用显式字符串覆盖某个目标的计算值，例如罗马数字页码。
- 规则不会向范围外推算，步长固定为 1。插页、缺页或重新编号时，拆成多个范围来表达；同一物理页不能混用两种规则。
- `put` 提交完整 YAML，宿主做全量校验后原子替换；提交空定义即清空映射。需要 `texts` 域的 put 授权。
- 逻辑页被删除、移动或重排后，相关规则会被标记为待修复并暂停推算，但不会删除定义；map 的其余部分照常可用。
- 没有有效 map 时，印刷页码未知：不要用物理页码冒充印刷页码。
