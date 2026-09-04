# LlamaHarness 完整代码审计报告（2026-09-05）

## 一、概述

- **审计范围**：`C:\project\lunch` 主工程 `LlamaHarness\`（57 文件 / 13,532 行）+ 测试工程 `LlamaHarness.Tests\`（34 文件 / 4,944 行），合计 91 文件 / 18,476 行，估算 ~129K token
- **代码基线**：git `93d5432`（v2.26.4，290/290 测试全绿，build 0 警告）
- **审计方式**：code-audit-pipeline 全流程（L0 计划 → L1 八组逐片落盘 → L2 汇总去重 → L2.5 完整性校验 → L3 报告）；每组独立窗口，全部发现"文件:行号"回源核对
- **审计维度**：全量（正确性/安全/性能/并发/可维护性/依赖构建/测试/配置，C# 特定检查全开）
- **重点复查项**：B4（BackendClientFactory BackendKind 抽象）→ 仍缺失（观察）；B8（GatewayErrorCode 统一）→ **已解决**（字符串 code + 统一错误格式）；D6（样本数下限）→ 仍缺失（低）；C1（Font 释放）→ PerfTrendChart 已修复，其余 5 View + UiTheme 仍延续（低/系统性）

### 总体健康度：★★★★☆（良好）

v2.26.4 前序迭代的 P0/P1 高风险项全部修复且本轮未复现；本轮 **0 高、7 中、46 低、15 观察**。
无当前可触发的崩溃、数据损坏、安全漏洞。主要风险集中在 **UI 资源治理（GDI/控件无界泄漏）** 与 **会话级状态容器无界增长**——均为"长期运行后缓慢劣化"型，非即发故障。

## 二、核心发现（按严重度）

### 高（0 条）
无。真实性命门（async void 崩溃、共享 CTS OOM、无超时挂起）已在 v2.26.4 前修复并经测试回归锁定。

### 中（7 条，按影响排序）

| # | 发现 | 位置 | 类型 | 触发 | 后果 |
|---|------|------|------|------|------|
| M1 | UpdatePropsCard 控件 + RowStyles 无界增长 | MonitorPanelView.cs:400-447 | 资源泄漏 | 手动刷新多次 | ~70 控件/次 + RowStyles 只增不减 → 布局错乱 + GDI 耗尽 |
| M2 | RefreshInFlightTasks 每次重建 Label 不 Dispose | StatusPanelView.cs:251-266 | 资源泄漏 | 请求进出频繁 | 每事件 N Label+Font 累积，长会话 GDI 耗尽 |
| M3 | ShowDocInPanel 每次点击泄漏 GDI | MainForm.Ui.cs:319-348 | 资源泄漏 | 帮助面板反复点击 | RichTextBox + Font 无界累积 |
| M4 | 前缀哈希/漂移字典未随唤醒清空 | SmartScheduler.Lifecycle.cs:202 | 无界增长 | 多次唤醒 + unknown 会话 | _prefixHashes/_lastDriftDiff 条目持续累积 |
| M5 | 绑定表无界增长，StaleBindingDays 未启用 | SlotAffinity.cs（_bindings） | 无界增长 | 长期运行 + 会话 UUID key | slot_bindings.json 持续膨胀 |
| M6 | 告警状态机死代码（冷却/升级/恢复未接入） | PerfAnalyzer.cs:102-160 | 设计未落地 | 任何告警路径 | AlarmRaised/AlarmRecovered 永不触发 |
| M7 | 采样 Ts 用 DateTime.Now，单调时钟未生效 | PerfSampler.cs:153 | 时钟回拨 | NTP 校准/系统改时 | 采样点倒挂 → 趋势图错乱 |

### 低（46 条，摘要）
按簇归类（完整清单见 `findings_summary.md`）：
- **C1 Font/控件泄漏族**：UiTheme/MainForm.Ui/LogView/5 个 View 构建期 `new Font` 不 Dispose（有界；PerfTrendChart 已修复）
- **会话状态无界族**：RestoreStats._byKey 无淘汰
- **并发/线程族**：rtId 跨线程读、RoundStats 无锁读、TrimToQuota 无互斥（注释不符）
- **异步/阻塞族**：HandleHealth 同步阻塞、LlamaServerClient 60s 不可取消 delay、PerfMonitorView UI 线程聚合/读文件
- **硬编码/魔法值残余族**：AppPaths 硬编码候选路径、页签索引 3/6/7、列号 3/4、5 分钟魔法值、测试 g:/temp、双套数值兜底
- **契约/文档族**：JsonDocument 所有权未声明、MetricKeys 双轨、4 处注释与实现不符、死代码（TryGetHeader/0x20000 分支/冗余判断/死参数）
- **测试族**：LogFile 落盘测试退化（自证）、SlotAffinity 死字段、TokenGuardTrimTests 排版

### 观察（15 条）
孤儿任务、warming restore 不取消、OutputContinuer finally Close 续接语义（建议优先验证）、B4 BackendKind、FIFO TTL 60s、tg_tps=0 误报等——见 `findings_summary.md` 观察清单，不进待修复清单。

## 三、模块分布

| 模块 | 中 | 低 | 观察 | 健康度 |
|------|----|----|------|--------|
| 调度核心（SmartScheduler 六文件 + RequestProcessor 等） | 1 | 8 | 3 | ★★★★☆ |
| KV 缓存与槽位亲和 | 1 | 7 | 2 | ★★★★☆ |
| 后端契约与进程 | 0 | 7 | 4 | ★★★★☆ |
| UI 外壳与配置 | 1 | 6 | 0 | ★★★☆☆ |
| UI 面板与监控页 | 2 | 7 | 1 | ★★★☆☆ |
| 日志与配置模型 | 0 | 4 | 2 | ★★★★☆ |
| 性能监控核心 | 2 | 3 | 2 | ★★★☆☆ |
| 测试工程 | 0 | 4 | 1 | ★★★★☆ |

风险最集中处：**UI 面板/监控页（g5，2 中）** 与 **性能监控核心（g7，2 中）**——均为近期新增模块，资源治理与设计落地未跟上主链路标准。

## 四、系统性问题（根因归纳）

1. **WinForms 控件/GDI 生命周期治理不统一**：`Controls.Clear()` 只移除不 Dispose + `new Font` 不释放，在 5+ 处重复出现（无界 3 处 + 有界 1 族）。根因：无统一控件清理工具 + 无字体工厂复用。
2. **会话 UUID key 驱动的字典无界增长是模式性风险**：_prefixHashes/_lastDriftDiff、SlotAffinity._bindings、RestoreStats._byKey 三处同源（unknown/会话 key 每新会话 +1 且无淘汰）。根因：会话级状态容器缺乏统一的上限/淘汰策略。
3. **"注释声称的设计"与实现脱节（5 处）**：告警状态机（死代码）、D3 NowMonotonic（定义未用）、StaleBindingDays（定义未用）、TrimToQuota 单线程（注释不符）、_wake 信号（定义未用）。根因：v2.26 迭代后期存在未落地的设计残留，说明"设计-实现-测试"三同步在收尾阶段松动。
4. **硬编码/魔法值以"残余"形式回流**：集中治理后仍有零散回归（候选路径、页签索引、列号、测试盘符）。根因：治理靠一次性审计而非持续门禁。

## 五、修复建议（按优先级）

### P1（建议下一迭代，6 项）
1. **M1/M2/M3 统一控件资源治理**：建 `ControlExtensions.DisposeChildren(this Control)` + UiTheme 字体工厂（静态缓存 + 引用计数）。一处实现，5 处受益（含 C1 族）。
2. **M4/M5 会话级状态容器加淘汰**：唤醒时 Clear _prefixHashes/_lastDriftDiff（1 行）；SlotAffinity.Load/启动时按 LastActive 清理 > 30 天绑定（保留 Preemptive）或设条目上限。
3. **M6 告警状态机二选一**：接入（60s 冷却 + Warn→Crit 升级 + AlarmRecovered 通知）或删除死代码保留直发路径——当前是"注释声称有、实际没有"的最差状态。
4. **M7 采样点启用单调时钟**：PerfSampler 采样 Ts 改用 D3 已有的 NowMonotonic()（已定义未调用）。
5. **测试退修复**：PrefixFingerprintAndLogFileTests 改为真正覆盖 LogPipeline 落盘（或并入 LogPipelineTests），删除自证用例。
6. **B8 错误码字符串集中为常量**（可选）。

### P2（结构性，1 项）
7. **B4 BackendKind 抽象**：接第二后端（vLLM）时引入；当前单后端可挂账（观察）。

### Quick Wins（低项批量，可按文件收拾）
- 删死代码：TryGetHeader、0x20000 分支、DrawPolyline 死参数、RefreshInFlightTasks 的 Controls.Clear 冗余、SlotAffinityConcurrencyTests 死字段
- 魔法值提常量：页签索引、列号、5 分钟、AppPaths 候选路径、测试 g:/temp
- 注释修正 4 处（SetPhase/StripThinkingTags/TrimToQuota/_wake）
- LlamaCppMonitor 三个 CTS 用 using

### 未来改进（观察项）
OutputContinuer 续接 Close 语义验证、孤儿任务取消、warming restore 取消、tg_tps 误报防护、FIFO TTL 评估。

## 六、附：本次审计过程性产出

```
audit_output/LlamaHarness_20260905_full/
├── audit_plan.md        # L0 计划（8 组分组 + 本轮重点）
├── findings/
│   ├── g1.md ~ g8.md    # L1 八组逐片落盘（68 条，含 B8 补记）
├── findings_summary.md  # L2 汇总去重 + 系统性问题归纳
└── audit_report.md      # 本报告（L3）
```

**覆盖范围声明**：g1~g8 全部按 audit_plan 分组完成，无 [INCOMPLETE] 组；High 复核结论为"无 High 需回源"；全部 7 条中项具备触发条件 + 行号 + 后果三要素。
**验证方式**：每条发现均已回源核对行号与逻辑；汇总基于磁盘 findings 而非记忆；完整性校验对照 audit_plan 逐组确认非空。
