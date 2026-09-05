# 审计计划（v2.30 基线）

- 项目: LlamaHarness（`C:\project\lunch\LlamaHarness` + `LlamaHarness.Tests`）
- 语言/框架: C# / .NET 8.0-windows / WinForms
- 规模: 主工程 57 .cs 文件 / ~18,298 行；测试 34 .cs 文件；合计 91 文件 / ~23,000 行
- 代码基线: git `240b3b6`（v2.30 feat(slot): 主从槽位隔离架构）
- 前序审计基线: git `93d5432`（v2.26.4，2026-09-05 审计）
- 审计维度: 全量（正确性/安全/性能/并发/可维护性/依赖构建/测试/配置，C# WinForms 特定节全开）
- 决策: 用户要求"重新做一次完整代码评审" → **完整重跑**

## 分组与优先级

| 组 | 模块 | 文件 | 优先级 |
|----|------|------|--------|
| g1 | 调度核心 | SmartScheduler.cs + SmartScheduler.Gateway.cs + SmartScheduler.Http.cs + SmartScheduler.Lifecycle.cs + SmartScheduler.Pipeline.cs + SmartScheduler.Crash.cs + RequestProcessor.cs + ThinkingMode.cs + SchedulerUtils.cs + InFlightTracker.cs + CrashRecovery.cs | 高 |
| g2 | KV 缓存与槽位亲和 | KvCacheManager.cs + SlotAffinity.cs + AffinityRule.cs + AffinityRuleMatcher.cs + RestoreStats.cs | 高 |
| g3 | 后端契约与进程 | IBackendClient.cs + BackendClientFactory.cs + LlamaServerClient.cs + LlamaFinder.cs + LlamaServerProcess.cs + LlamaStatsParser.cs + OutputContinuer.cs + CpuAffinity.cs + SystemMetrics.cs + LlamaCppMonitor.cs + MonotonicClock.cs | 高 |
| g4 | UI 外壳与配置 | MainForm.cs + MainForm.Ui.cs + MainForm.Config.cs + MainFormPresenter.cs + UiTheme.cs + MarkdownRenderer.cs + LogView.cs + Program.cs + ControlExtensions.cs | 中 |
| g5 | UI 面板与监控页 | StatusPanelView.cs + StatsPanelView.cs + SlotPanelView.cs + MonitorPanelView.cs + PerfMonitorView.cs + PerfTrendChart.cs | 中 |
| g6 | 日志与配置模型 | LogFile.cs + LogPipeline.cs + AppConfig.cs + AppPaths.cs + TokenGuard.cs | 中 |
| g7 | 性能监控核心 | PerfAlarm.cs + PerfAnalyzer.cs + PerfEvent.cs + PerfEventTracker.cs + PerfLog.cs + MetricKeys.cs + PerfPoint.cs + PerfSampler.cs + PerfSeries.cs + PerfThresholdRule.cs + RequestTiming.cs + RequestTimingTracker.cs | 中 |
| g8 | 网关与错误码 | GatewayErrorCodes.cs + LlamaServerClient.cs（网关部分）+ SmartScheduler.Gateway.cs | 高（v2.30 新增，重点审） |

## 本轮重点

1. **主从槽位隔离架构（v2.30 新增）**：SmartScheduler.Pipeline.cs / SmartScheduler.Gateway.cs / SlotAffinity.cs 的新逻辑正确性
2. **v2.26→v2.30 期间修复项验证**：M1/M2/M3 控件资源泄漏、M7 单调时钟、D3 NowMonotonic 实际使用
3. **并发安全**：槽位亲和状态、请求管道状态、Gateway 重写逻辑
4. **WinForms UI 规范**：跨线程访问、控件生命周期、GDI 资源、UI 线程阻塞
5. **异步模式**：async void 使用、CancellationToken 传播、ConfigureAwait、.Result/.Wait()
6. **安全面**：路径校验、CLI 参数、日志敏感信息
