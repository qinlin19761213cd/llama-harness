# 审计计划

- 项目: LlamaHarness（`C:\project\lunch\LlamaHarness` + `LlamaHarness.Tests`）
- 语言/框架: C# / .NET 8.0-windows / WinForms
- 规模: 91 文件 / 18476 行 / 估算 ~129K token（主 57 文件 13532 行 + 测试 34 文件 4944 行）
- 代码基线: git `93d5432`（v2.26.4，290/290 全绿）
- 审计维度: 全量（正确性/安全/性能/并发/可维护性/依赖构建/测试/配置，C# 特定节全开）
- 决策: 用户要求"重新做一次完整代码评审" → **完整重跑**，新建 `audit_output/LlamaHarness_20260905_full/`，不复用任何旧产物

## 分组与优先级

| 组 | 模块 | 文件数 | 估算token | 优先级 | 顺序 |
|----|------|--------|-----------|--------|------|
| g1 | 调度核心（SmartScheduler partial×6 + RequestProcessor/ThinkingMode/SchedulerUtils/InFlightTracker/CrashRecovery） | 11 | ~22.4K | 高 | 1 |
| g2 | KV 缓存与槽位亲和（KvCacheManager/SlotAffinity/AffinityRule/AffinityRuleMatcher/RestoreStats） | 5 | ~11.7K | 高 | 2 |
| g3 | 后端契约与进程（IBackendClient/BackendClientFactory/LlamaServerClient/LlamaFinder/LlamaServerProcess/LlamaStatsParser/OutputContinuer/CpuAffinity/SystemMetrics/LlamaCppMonitor） | 10 | ~12.0K | 高 | 3 |
| g4 | UI 外壳与配置（MainForm/MainForm.Ui/MainForm.Config/MainFormPresenter/UiTheme/MarkdownRenderer/LogView/Program） | 8 | ~13.6K | 中 | 4 |
| g5 | UI 面板与监控页（StatusPanelView/StatsPanelView/SlotPanelView/MonitorPanelView/PerfMonitorView/PerfTrendChart） | 6 | ~13.8K | 中 | 5 |
| g6 | 日志与配置模型（LogFile/LogPipeline/AppConfig/AppPaths/TokenGuard） | 5 | ~9.1K | 中 | 6 |
| g7 | 性能监控核心（PerfAlarm/PerfAnalyzer/PerfEvent/PerfEventTracker/PerfLog/MetricKeys/PerfPoint/PerfSampler/PerfSeries/PerfThresholdRule/RequestTiming/RequestTimingTracker） | 12 | ~12.2K | 中 | 7 |
| g8 | 测试工程（LlamaHarness.Tests 全 34 文件） | 34 | ~34.6K | 低 | 8 |

## 审计顺序

1. g1 → g2 → g3（调度/KV/后端契约，高优先级，串行或并行）
2. g4 → g5 → g6 → g7（UI/日志/性能）
3. g8（测试工程）

每组独立窗口：只读本组文件 → 逐文件审计 → 落盘 `findings/<group>.md`，不保留跨组记忆。

## 本轮重点（承接 v2.26.4 已知遗留 + 前轮历史问题）

- 复查已知遗留是否仍存在：B4（BackendClientFactory 无 BackendKind）、B8（GatewayErrorCode 未统一）、D6（PerfAnalyzer 样本数下限）、C1（Font 释放）
- 并发：槽位/在途/事件总线共享状态、async void 处理器、CancellationToken 传播
- 资源：HttpClient/Stream/CTS/事件订阅泄漏
- 安全：CLI 注入面（LlamaFinder）、路径校验（KV 快照/日志路径）、请求体明文日志
- UI：跨线程访问、UI 线程阻塞、Timer/订阅生命周期
- 测试：断言质量、偶发失败、关键路径覆盖
