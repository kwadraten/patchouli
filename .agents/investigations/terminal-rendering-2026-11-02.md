# 内置终端闪烁与缺字调查

日期：2026-11-02。范围：源码调查及实际 NuGet DLL 的独立探针；未操作用户的 Agent 会话。后续按用户要求将终端依赖升级至 4.1.3。

## 结论与证据边界

截图不足以确认唯一根因。已确认一个旧帧选择缺陷和两个上游已修复的绘制问题；尚未捕获截图场景的 PTY 数据，因此不能声称已经复现用户的完整操作过程。

初次调查时 Patchouli 使用 Iciclecreek.Avalonia.Terminal 4.1.0，传递依赖 XTerm.NET 3.0.0。现已升级至 4.1.3 / 3.0.2。EmbeddedTerminal 不自行绘制字符；UI 仅设置字号 13 和前景/背景色。用户已有的 OCR 等工作区改动未触碰。

## 已复现：同一 PTY chunk 的 ESU 后文字没有进入被选中的快照

使用实际 4.1.0 DLL，通过反射调用内部 FrameCapturePool，按生产代码顺序执行：

1. PublishThrottled 建立最近一次 chunk 发布时间。
2. 下一次写入打开 DEC 2026，清空输入行，关闭 DEC 2026（ESU），随后在同一次 Terminal.Write 中输出中文。
3. SynchronizedOutputChanged 的结束回调执行 Publish，快照是清空的行。
4. chunk 尾调用 PublishThrottled；在 1/30 秒间隔内被节流。
5. PinForRender 收到相同的 write generation，接受旧快照。

实测：live='有时候打着打着字前面的字就没了'，captured=''，CapturedFrame selected=True。

生产对应路径：TerminalView.Pty.cs 的 ConsumeOutputChunk 在每个 chunk 开始递增 _liveWriteGeneration；TerminalView.cs 的 OnSynchronizedOutputChanged 在 ESU 时发布；FrameCapture.cs 的 PinForRender 将相同 generation 视为当前帧。一个 chunk 内 ESU 后仍能继续写入，因此 generation 相同不能证明快照最新。这会使缓冲区内容与被绘制帧不同；之后有新输出可能恢复。

这是快照选择层面的确定复现，不是完整窗口/IME/OpenCode 场景复现。尚不能证明截图中的前缀消失必然由它造成。上游 HEAD bbee5ed 的 FrameCapture.cs 与 4.1.0 对应提交没有差异。

探针位于 `.tmp/terminal-investigation/Program.cs`，结果为同目录 `probe-output.txt`。执行 `dotnet run --project .tmp/terminal-investigation/Probe.csproj`。临时探针没有加入测试套件。

## 已排除的简单解释

同一探针通过实际 XTerm.NET DLL 验证：

- 中文 UTF-8 每次只写一个字节，最终文本完整。
- 15 个汉字的光标列为 30，基础双列宽度正确。
- 移动到行首、整行清除后重写该中文句子，缓冲区完整。

这只排除了这些简单用例的失败，不代表所有 IME、emoji、行边界、ConPTY 宽度协商均正确。

现有 EmbeddedTerminalHeadlessTests.Chinese_utf8_output_is_decoded_across_reads 仅断言 TextOutputReceived 文本；这条旁路解码测试不能发现屏幕快照缺字。

## 上游已修复的相关绘制问题

- [PR #203](https://github.com/tomlm/Iciclecreek.Avalonia.Terminal/pull/203)：按每个文本 run 的起点取整到设备像素，使改色/分段变化时字形左右抖动。4.1.0 含旧实现。该问题解释细小位置抖动，不足以解释整片清屏闪烁或中文前缀消失。
- [PR #189](https://github.com/tomlm/Iciclecreek.Avalonia.Terminal/pull/189)：主字体缺少某个字符时，整段回退到 FormattedText；回退字体的自然 advance/baseline 使后续字符错位。新版把该字符隔离到自己的 cell。它与混合符号显示相关，尚无证据证明截图中的汉字走了同一缺陷。

已成功从 NuGet 还原 4.1.3，传递依赖 XTerm.NET 3.0.2；项目编译通过，零警告、零错误。4.1.3 实际 DLL 上仍能复现旧快照选择缺陷。详细调用链、固定提交源码链接和已执行的独立最小复现见 [issue 草稿](terminal-missing-text-issue-draft.md)。

## 修复与验收方向

优先补快照回归：一个 chunk 内含 ESU 与后续输出，并覆盖 chunk 尾节流生效的情况；确保选择的帧包含最终内容。修复应保证 chunk 完成后发布最终状态，或使 ESU 后的写入让其快照失效，不能只提高帧率。

升级依赖可吸收已修复的字形定位问题，但不能代替快照修复。正式变更还需遵循仓库的 cleanup、编译、记录测试和必要 inspection 规则。

针对用户现象，还需在可控会话中记录 PTY 输出块边界和输入，回放至相同 emulator/renderer，并比较原始输出、CaptureViewportText 与实际渲染。记录可能含输入/会话内容，应仅在受控复现中临时采集。分别验证连续中文 IME 输入、编辑中间文本、窗口缩放/非整数 DPI、OpenCode 全屏重绘和同步更新边界。缺字发生时缩放或重绘是否恢复可作为定位线索，但不单独作为根因证据。


## 升级验证记录

- `Directory.Packages.props`：Iciclecreek.Avalonia.Terminal 从 4.1.0 升至 4.1.3；实际还原 XTerm.NET 3.0.2。
- `scripts/cleanup-code.ps1 -Include Directory.Packages.props -UseExistingBuild`：JetBrains 2026.1.4 固定 profile 通过；配置缩进也由该脚本规范化。
- 通过 `scripts/run-tests.ps1 -Suite All -Filter ... -NoBuild -SkipRestore` 分别运行 EmbeddedTerminalHeadlessTests 和 AgentWorkspaceTerminalTests；设置 `PATCHOULI_RUN_PTY_TESTS=1`，结果分别为 6/6 和 5/5，无跳过。
- 记录：`.tmp/test-runs/terminal-413-embedded-20261102/` 和 `.tmp/test-runs/terminal-413-workspace-20261102/`。
- issue 草稿内的固定时序最小复现已抽出执行，输出 `Live: 'HELLO'` 与 `Selected capture: ''`。临时项目为 `.tmp/terminal-investigation/issue-probe/`。
- 未对用户正在使用的实际窗口做升级前后视觉对比。此次升级吸收已知的上游字形定位修复，不能据此宣称截图中的全部闪烁和缺字已消失。
