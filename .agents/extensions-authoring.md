# FSX 扩展作者指南

本指南面向编写 Patchouli FSX 扩展的 Agent。ADR 0049 已决定将可信 F# 脚本执行移到宿主管理的独立 worker 进程；迁移验收状态见 [验证记录](extension-validation.md)。FSI worker 不是安全沙箱。请保持脚本来源和网络授权清晰，避免把凭据放进源码、日志或普通配置字段。

## 最小翻译扩展示例

扩展通过顶层 `extension` 值导出定义。配置键是持久化身份；显示标签和 F# 变量名可以修改，键名应保持稳定。下面的 `main.fsx` 声明一个随宿主翻译模型默认值运行的 LLM capability、一个普通指令字段，并为每个输入 box 产生一个有身份的结果：

```fsharp
open System
open System.Threading
open System.Threading.Tasks
open Patchouli.Extensions.Contracts
open Patchouli.Extensions.Sdk

let modelBinding =
    Config.bind "llm.primary"
        (Capability.llm "Translation model override"
         |> Capability.followDefault Domains.translation)
let promptBinding = Config.bind "prompt" (Field.multiline "Extra instructions" "")
let configFields = Config.fields [ Config.asField modelBinding; Config.asField promptBinding ]

let descriptor =
    ExtensionDescriptor("translation.example", "Example", "1.0.0", "Example FSX translation extension.")

let translateBox (box: ExtensionBox, context: TranslationExtensionContext,
                  capabilities: IExtensionCapabilities) (token: CancellationToken) = task {
    match Option.ofObj box.Text with
    | None -> return ExtensionBoxOutcome(box.Id, ExtensionBoxOutcomeKind.Skipped)
    | Some text when String.IsNullOrWhiteSpace text ->
        return ExtensionBoxOutcome(box.Id, ExtensionBoxOutcomeKind.Skipped)
    | Some text ->
        let llm = Capability.resolveLlm modelBinding context.Config capabilities
        let prompt = Binding.get promptBinding context.Config
        let request =
            LlmRequest([| LlmMessage("user", prompt + "\nTranslate into " + context.TargetLanguage + ":\n" + text) |],
                       null, Nullable<decimal>(), Nullable<int>(), null)
        let! response = llm.CompleteAsync(request, token)
        return ExtensionBoxOutcome(box.Id, ExtensionBoxOutcomeKind.Translated, response.Text)
}

let pipeline = Step.sync id |>> Step.async translateBox

let translate (context: TranslationExtensionContext)
              (capabilities: IExtensionCapabilities)
              (token: CancellationToken) = task {
    let runBox box cancellationToken =
        Pipeline.run context.Progress "example-translation" pipeline
            (box, context, capabilities) cancellationToken
    let! outcomes = Parallel.map 2 runBox context.Boxes token
    return TranslationExtensionResult(outcomes)
}

let extension : ITranslationExtension =
    { new ITranslationExtension with
        member _.Descriptor = descriptor
        member _.ConfigFields = configFields
        member _.TranslateAsync(context, capabilities, token) = translate context capabilities token }
```

这个例子展示 SDK 的类型和入口，不代表生产翻译质量策略。实际扩展还应处理模型拒答、超时、部分失败、结构化 payload、目标语言、取消和请求预算，并严格保留每个输入的 `DocumentBoxId`。不要在脚本里自行提交文档数据；脚本只返回候选结果，宿主和 Core 负责身份、revision 与提交边界。

宿主输入的 `ExtensionBox.Payload` 提供共享的强类型 `DocumentBoxPayload` 快照；返回结构化内容时使用 `ExtensionBoxOutcome.FromPayload(box.Id, payload)`。表格直接返回 `TableBoxPayload`，无需自行序列化 JSON。原有 `PayloadJson` / `TranslatedPayloadJson` 保留为兼容适配入口；同一个结果不能同时提供强类型和 JSON 两份候选权威。宿主仍会验证 payload 与源 Box 类型是否匹配，并检查 HTML 表格结构和受保护公式。

## 配置与 capability

用 `Config.bind key field` 在脚本顶层声明配置。支持的字段构造器包括 `Field.text`、`multiline`、`language`、`integer`、`decimal`、`boolean`、`choice`、`model`、`defaultModel`、`documents`、`pageRange` 和 `credentialReference`；`Field.describe`、`range`、`decimalRange`、`required`、`optional` 可补充约束。通过 `Config.fields` 和 `Config.asField` 导出 schema；`Binding.get binding context.Config` 从本次运行的配置快照读取值。

配置声明由 FCS 从源码静态提取，不执行脚本来发现字段。请把绑定作为普通的顶层 `let` 声明，避免按运行时条件动态生成 schema。`Config.fields` 会隐藏 capability 类型的字段；模型选择仍是可配置字段，所以 `llm.primary` 会出现在设置表单中。

`Capability.llm` 和 `Capability.http` 表示扩展请求托管能力，并不授予权限。宿主授权独立保存，并在一次 pinned run 开始时冻结；以后更改授权或配置只影响新 run。模型调用用 `Capability.resolveLlm binding config capabilities`，HTTP 调用用 `Capability.resolveHttp binding config capabilities`。凭据字段只保存不透明的凭据引用，由宿主管理真实密钥。不要把密钥写入脚本、提示词、异常文本或进度消息。

翻译使用完整响应请求，不提供流式调用或未提交译文预览。模型通过 `CompleteAsync` 返回完整结果；扩展验证身份集合和结构后才返回候选，宿主继续执行版本检查和逐页原子提交。管道进度与任务暂停、取消独立于正文传输，不需要流式响应。

## Pipeline、批处理和进度

`Step.sync` 与 `Step.async` 创建 pipeline，`|>>` 按类型串接步骤。Pipeline 是惰性组合；调用 `Pipeline.run progress phase pipeline input token` 才会执行。`Parallel.map maxConcurrency work items token` 保留输入顺序，并为集合及每个 item 自动发布进度。`Batch.map` 同样会跟踪 batch/item；正常执行时不需要手工伪造百分比。进度的 completed/total 只描述当前步骤或集合，不表示耗时比例；默认事件不包含请求正文或凭据。

`Batch.packWithBudget budget measure items` 按 item 数、字符和可选 token 估算进行分包。超过单项上限时会抛出错误，调用方必须先安全分段；不能静默截断或把超预算内容继续发送。`Batch.map` 的 callback 必须为每个输入按原顺序返回一个 `Result`。`Batch.collect` 遇到任意错误会以 `AggregateException` 失败；`Batch.collectPartial` 返回成功项与失败项两组，适合需要保留部分成功结果的场景。取消应继续传给 capability、pipeline、retry 和 batch API。

## 复制、编辑、检查、试跑和激活

设置页的扩展面板可以复制现有扩展为新 ID、编辑源码和配置、检查草稿、试跑草稿、试跑已选扩展、保存源码以及激活。复制只替换主脚本中显式写出的旧扩展 ID 和可选 manifest 的 `id` 行；检查复制结果并确认 descriptor 的 ID 与新包 ID 一致。草稿试跑使用隔离的临时 manager，不切换 Active、LKG，也不写入书库。检查只做静态检查；试跑才执行一条样例请求，但它仍不能证明生产服务可用或覆盖所有输入。

保存工作源码不会切换正在运行的 generation。激活会检查并原子切换到新 Active generation；已开始的任务继续持有原来的 lease。LKG（last-known-good）是单独的恢复快照，只有显式执行“提升为可用恢复版本”才会更新。回滚从 LKG 恢复运行版本；源码、LKG 和已提交的书库内容是独立状态。

跨进程边界只传 SDK 约定的数据，不把脚本类型放入宿主全局缓存。序列化脚本定义的记录或匿名记录时，继续使用每代独立的 `JsonSerializerOptions` 和 `DefaultJsonTypeInfoResolver`；随包的 `Support.fsx` 中 `jsonBody` 已采用这个方式。扩展持有的订阅、定时器和其他资源应在 `IDisposable` 或 `IAsyncDisposable` 中释放。最后一个 lease 退出后，宿主请求清理并确认 worker 进程退出；不把 GC 或 collectible 标记当作回收证据。

同一 worker 可以执行多个调用。取消首先针对当前调用；强制重启会使该 worker 中所有未完成调用失败，不自动重放请求。已完整返回的候选由宿主校验和提交，已提交页面保留。脚本不能依赖重启后保留内存状态；需要重新执行时由用户明确启动新任务。模型和 HTTP 句柄经专用管道请求宿主管理的能力，凭据仍由宿主保管。使用绑定槽位解析能力，不自行用配置中的模型名称替代宿主解析。

## 随包引擎与证据边界

当前 Infrastructure 带有四个翻译包：

| 包 ID | 当前实现 | 需要注意 |
| --- | --- | --- |
| `translation.llm` | 托管 LLM，提示词和参考上下文、分段与批处理、术语表、代码/公式/标记占位保护与恢复、逐段身份及结构校验。`window-radius`、`backfill`、`glossary` 是 LLM 专属设置。 | 需要可用的模型配置和独立 LLM 授权。模型响应仍需按扩展逻辑校验；prompt 本身不是结构安全边界。 |
| `translation.google-free` | 无凭据 Google GTX 请求，GET 查询有字符上限，使用 source/target language 与文本参数。 | 属于未承诺稳定性的网页协议；URL 长度、节流与服务端策略都会影响请求。 |
| `translation.deepl-free` | 无密钥 DeepL 网页 JSON-RPC 协议。 | 这不是官方稳定的公开 API；服务端可限流或改变协议。 |
| `translation.deeplx` | 向用户配置的 DeepLX endpoint 发送托管 HTTP 请求，可配置 endpoint 和不透明凭据引用。 | 没有 endpoint 时只能做静态/fixture 验证，不能宣称 live 成功。 |

静态 FCS 检查只证明脚本能通过当前编译器检查；fixture 只证明在模拟响应下请求与解析逻辑符合测试；两者都不等于线上连通。一次 live Hello 探测只证明该时间、网络、端点和请求的一次结果，不保证后续可用、额度、完整文档质量或服务条款。记录证据时注明是否 live、时间、状态码、解析结果和是否使用凭据，绝不输出凭据本身。当前工作区记录的无凭据 Hello 探测是：Google GTX HTTP 200，721 ms，解析到 `Bonjour`；DeepL 网页协议 HTTP 429，1332 ms，响应不是可解析 JSON，且没有重试。这不是持续可用性承诺。DeepLX 未提供 endpoint，未做 live 验证。

元数据已有 `metadata.crossref` FSX 扩展入口。Host 通过 `HostServices.ExtensionDomains` 提供显式 OCR 与检索入口：`RecognizeAndStageAsync` 将指定页（或区域）渲染为受控 PNG 字节，固定 OCR 扩展代，验证并映射候选，再以调用前捕获的树修订作为父版本创建 working revisions；返回值供人工审核，提交仍由调用方显式调用 `IOcrDocumentTreeImporter.CommitAsync`。`RankSearchAsync` 对现有 `ISearchService` 结果中的有界候选池调用固定检索扩展，并将返回 UnitId 映射回原始 `SearchMatchedUnit`，保留其 page、box 和 tree revision 证据。

OCR 结果遵守 [MinerU 归一化边界](ocr-normalization.md)。结构化扩展复用 Core 的 `DocumentBoxSeed` 内容与字段；文字预填用的 `LocalOcrCandidate` 不是另一套完整 OCR 模型。其他引擎只识别出文字时，只生成文字块，不猜造 MinerU 表格或公式结构。

这些是可供应用层明确调用的工作流入口；默认 OCR 队列和默认搜索排序仍使用现有 backend。不要将此集成描述为已替换默认 OCR 或检索系统，也不要把源文件路径、数据库、容器或提交权限传给扩展。
