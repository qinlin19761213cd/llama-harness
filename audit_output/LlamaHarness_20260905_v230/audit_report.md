# LlamaHarness 项目代码评审报告（v2.30）

**代码基线**: git `240b3b6`（feat(slot): v2.30 主从槽位隔离架构）  
**前序基线**: git `93d5432`（v2.26.4，2026-09-05 审计）  
**项目规模**: 主工程 57 文件 / ~18,298 行 + 测试 34 文件 / ~4,944 行，合计 91 文件 / ~23K 行  
**测试状态**: 290/290 全绿，build 0 警告  
**评审维度**: 全量（正确性 / 安全 / 性能 / 并发 / 可维护性 / 依赖构建 / 测试 / 配置）  
**评审方式**: code-audit-pipeline 全流程（L0 计划 → L1 八组并行逐片落盘 → L2 汇总去重 + High 逐条回源复核 → L3 报告）

---

## 一、总体评价

### 总体健康度：★★★★☆（良好，稳步提升中）

从 v2.26.4 到 v2.30 的迭代，项目在架构演进的同时保持了测试全绿、build 零警告的健康状态。本轮审计发现 **3 High / 15 Medium / 18 Low / 12 Observation**——相比前次审计（0 High / 7 Medium / 46 Low），问题总数下降但 High 从 0 回升到 3 条。

这个变化不完全是"退步"：其一，v2.30 新增的主从槽位隔离、MetricKeys 注册表、PerfAnalyzer 告警状态机等新模块引入了新缺陷；其二，本轮审计对 C# WinForms 特定风险（异步取消贯穿、CancellationTokenSource 泄漏、跨模块契约一致性）的检查更深入。

**核心观察**：项目的并发模型基础（锁体系、原子操作、事件路由 UI 线程切换）已经相当成熟；剩余的主要是"新引入模块的收尾问题"和"跨模块契约漂移"。修复量上，3 条 High 的修复量都很小（<5 行 / <20 行），主要是遗漏的补齐和声明对齐。

---

## 二、做得好的地方

在挑问题之前，必须先肯定项目做得好的方面——这些是多轮审计迭代积累下来的成果，值得延续：

### 并发模型基础扎实
- **锁粒度和设计意图清晰**：`_kvStateGate`（KvCacheManager，串 5 个集合）、`_thinkingGate`（ThinkingMode）、`_sleepGate`/`_wakeGate`（Lifecycle）、`_alarmLock`（PerfAnalyzer）——每组共享状态都有独立锁，没有一把大锁覆盖全项目
- **热点计数用 Interlocked**：`_keepAliveFailures`、`_ioFailCount` 等跨线程计数器用 `Interlocked.Increment` 避免锁竞争
- **fire-and-forget 有兜底**：AcceptLoopAsync、后台 save、Warming 等不受控的 Task 都有 try-catch + 日志兜底，异常不会静默丢失也不会炸进程
- **事件锁外触发**：LlamaStatsParser.RoundUpdated/RoundRemoved 事件在锁外 Invoke，避免订阅方回调阻塞生产者

### HTTP/进程管理模式正确
- **LlamaServerClient 共享 HttpClient**：`Lazy<HttpClient>` + `_ownsHttp` 标志，完全符合 .NET 最佳实践，避免 socket 耗尽
- **LlamaServerProcess 进程生命周期**：Exited 事件局部捕获 proc 引用防止快速重启时错配；Stop 内部有 `_proc?.Dispose() + _proc = null` 的原子清理
- **OutputContinuer SSE 管道**：byte[] + 游标压实、writeGate 门控、finalize hold 逻辑都设计严谨
- **SSFR 防护 / CORS / Body 上限**：RequestProcessor 有完整的安全头和边界防护

### UI 跨线程规范统一（g4 审计亮点）
- **SmartScheduler 事件路由矩阵已全部验证安全**：8 条后台线程事件全部正确切回 UI 线程（`_view.InvokeOnUi(...)` 或 View 内部自行切）
- **UI 线程阻塞检查全覆盖**：启动/清缓存 Task.Run 移出 UI 线程；ShowDocInPanel/配置导入导出等毫秒级 IO 可接受
- **async void 异常捕获到位**：所有 async void 事件处理器都有 try-catch-finally
- **事件订阅/取消对称**：AttachScheduler ↔ DetachScheduler 成对，无订阅泄漏
- **PerfTrendChart GDI 治理最佳实践**：Font 提升为实例字段 + `Dispose(bool disposing)` 重写；OnPaint 中 Pen/Brush 全部 `using var`

### KV 缓存设计成熟
- **KvCacheManager.RecordSave 锁外写盘**：先锁内更新内存 + 构建 JSON，再锁外 File.WriteAllText，与 SlotAffinity.Save 锁内 IO 形成鲜明对比（后者是反例）
- **双阶段持久化**：Sanitize(key).bin 数据 + meta.json 元数据 + kv_cache_index.json 索引，原子写（tmp + Move）
- **TrimToQuota LRU 淘汰**：基于 FileInfo.LastWriteTime 的磁盘配额治理

### 测试覆盖和稳定性
- **290/290 全绿 + 0 警告**：连续两个版本保持，说明 CI 有有效门禁
- **InternalsVisibleTo 开放内部实现给测试**：PerfSampler 解析工具方法、KvCacheManager 私有逻辑等都有回归测试覆盖
- **PerfAnalyzer 告警状态机**：事件旁路设计（不抑制直发告警列表）兼顾了"测试可复现"和"告警不刷屏"

### 代码组织清晰
- **SmartScheduler partial 六文件拆分**：Gateway / Http / Lifecycle / Pipeline / Crash 各管一块，职责边界清楚
- **MainForm partial 三文件拆分**：外壳 / UI 构建 / 配置映射，Presenter 剥离后 MainForm 只剩生命周期和事件路由
- **static/doc 文档 + static/icon + static/pic 资源**：构建时自动拷贝，运行时按 AppContext.BaseDirectory/static 加载

---

## 三、核心问题（按优先级）

### P0 — 必须立即修复（3 条 High，修复量均 <20 行）

**H1. MetricKeys × PerfAnalyzer.ValueOf 键覆盖错位**（配置驱动契约破坏）
- [PerfAnalyzer.cs:L170-183](file:///c:/project/lunch/LlamaHarness/PerfAnalyzer.cs#L170-L183)
- MetricKeys 声称"唯一权威注册表"，但 ValueOf switch 仅覆盖 12 个键（其中 3 个是 MetricKeys 注册的）；27 个 MetricKeys 键中 24 个在 EvaluatePoints 中返回 null → 阈值规则静默跳过
- 修复：ValueOf switch 补齐 MetricKeys 所有键的等价映射（`"gen_tps" or "tg_tps" or "tokens_per_second" => p.TgTps` 等）
- 影响：用户看文档写的规则全部不生效，当前之所以"没出事"是因为内置默认规则用旧键名

**H2. SystemMetrics.GetCpuPercent 四字段无锁并发读写**
- [SystemMetrics.cs:L43-64](file:///c:/project/lunch/LlamaHarness/SystemMetrics.cs#L43-L64)
- `_prevIdle`/`_prevKernel`/`_prevUser`/`_hasSample` 四个字段无锁读写，多 Timer 并发采样时 CPU 百分比跳变
- 修复：加 `private readonly object _gate = new();` 然后 `lock (_gate)` 包裹整个方法体（5 行内）

**H3. LlamaCppMonitorCollector 三个 Linked CTS 未 Dispose**
- [LlamaCppMonitor.cs:L111-113](file:///c:/project/lunch/LlamaHarness/LlamaCppMonitor.cs#L111-L113)
- 三个 `CreateLinkedTokenSource` 没有 `using`，持有内核句柄 + 回调挂在父 CTS 上
- 修复：`using var slotsCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);`（3 行）

### P1 — 建议下一迭代修复（15 条 Medium，按根因归类）

**根因 1：异步取消语义收尾不彻底**（4 条）
| # | 标题 | 修复建议 |
|---|------|---------|
| M7 | HandleHealth 同步阻塞 GetAwaiter().GetResult() | 改为 `async Task<bool>` |
| M3 | OutputContinuer 整条管道无 CancellationToken 贯穿 | Handle*/Pipe* 方法签名加 ct |
| M4 | SendAsync 看门狗用 CancellationToken.None | 改为 `Task.Delay(timeout, ct)` |
| M17 | GetMetricsAsync/GetJsonAsync 吞没 OCE | `catch (OperationCanceledException) { throw; } catch { return null; }` |

**根因 2：资源释放不对称**（4 条）
| # | 标题 | 修复建议 |
|---|------|---------|
| M1 | TryRecoverContextOverflowAsync 两条失败路径未 Close ctx.Response | 补 `outResp.Close()` 或把 Close 下沉 |
| M5 | PerfLog.Start RotateLocked 后叠加 OpenWriter | 改 `if (RotateLocked()) { } else _writer = OpenWriter(path);` |
| M6 | MainForm._tooltip 未 Dispose | 重写 Dispose(bool) 加 `_tooltip?.Dispose()` |
| M2（已升 High）| LlamaCppMonitor 三个 CTS 未 Dispose | 见 P0-H3 |

**根因 3：会话级状态容器无淘汰策略**（4 条）
| # | 标题 | 修复建议 |
|---|------|---------|
| M11 | SlotPanelView._slotMgmtRowIdx + DataGridViewRows 只增不减 | RefreshSlotMgmtGrid 前清理陈旧 key |
| M12 | SlotAffinity.Save() 锁内完整文件 IO | 对齐 KvCacheManager.RecordSave 改锁外写盘 |
| M13 | RestoreStats._pending 无容量上限 | 加容量 1024，超限时 Dequeue 最旧 |
| M14 | SlotAffinity.PruneStaleBindings 只启动跑一次 | 后台定时跑 1h 或随 GetSlot 刷新 |

**根因 4：其他**（3 条）
| # | 标题 | 修复建议 |
|---|------|---------|
| M8 | Handler 看门狗超时后 handlerTask 不 Cancel | Task.Run 闭包内传 CancellationToken |
| M9 | LogPipeline.Shutdown 与 Enqueue 竞态 | Shutdown 前短暂持 _enqueueGate 确保无进行中 |
| M15 | ClearAllAsync 超时保护失效 | Task.WhenAll + Task.Delay 替代纯 WhenAll |

### P2 — 可随迭代清理（18 条 Low，批量收拾）

#### 魔法值 / 风格统一
- `Color.FromArgb` 魔法值与 `UiTheme.C_*` 常量平行 → 补齐 C_* 常量 + 逐步替换
- BuildPage RowStyles/ColumnStyles 只增不减 → 加 `RowStyles.Clear()` 防御
- PerfAnalyzer 枚举比较依赖 PerfAlarmLevel 排序 → 用显式 SeverityRank 函数
- ParseAutoPreemptivePrefixes 热路径每次重建 List → 解析一次缓存

#### 死代码 / 冗余清理
- LlamaServerProcess.Dispose 冗余调 _proc.Dispose → 删除第二行
- FlatButton 插断 MakeSectionTitle 的 XML 注释 → 移动 FlatButton 到 UiTheme 末尾
- OnServerExited StopNow 后的 dead path → 加注释明确"预期被跳过"

#### 契约/注释修正
- SetPhase TOCTOU → 换 Interlocked.CompareExchange（顺带解决并发竞态）
- AffinityRuleMatcher as-cast 引入隐藏副作用 → 始终 `ToArray()`
- PerfEventTracker 用 DateTime.Now → 改 MonotonicClock.Now

---

## 四、架构演进评价

v2.30 主从槽位隔离是一次**合理且克制的架构扩展**：
- SmartScheduler.Pipeline.cs / SmartScheduler.Gateway.cs 新增的子代理隔离逻辑，通过 `keySuffix` / `AppNameOf` 的 `_sub` 后缀处理实现，不破坏主代理的既有亲和规则
- SlotAffinity 的 `keySuffix` 子代理隔离逻辑在 AffinityRuleMatcher.AppNameOf 中正确对齐
- GatewayErrorCodes.cs 统一了字符串 code + 错误格式，解决了 v2.26 遗留的 B8 问题
- 但 GatewayErrorCodes 的命名与旧 WriteError/WriteErrorV2 两套格式仍不统一——Pipeline.cs 有 8 处旧格式 WriteError 调用未迁移

PerfAnalyzer 告警状态机是**半落地状态**：
- 60s 冷却 + Warn→Crit 升级 + AlarmRecovered 通知的框架已经搭好
- 但 `PerfThresholdRule.Defaults` 仍用旧键 `"tg_tps"`，MetricKeys 注册表用新键 `"gen_tps"`，ValueOf 只识别旧键——三者分裂
- 吞吐类告警负载门控 `(rule.Metric == "tg_tps" || rule.Metric == "pp_tps")` 未覆盖新键

MonotonicClock 是**正确的基础设施**：
- 解决了 PerfPoint 时间戳时钟回拨问题
- EpochLocal + Stopwatch 差值的合成方式，采样点间单调递增
- 但 PerfEventTracker 默认时间戳仍用 DateTime.Now，PerfLog 行首时间戳也是 DateTime.Now——跨通道分析时需注意

---

## 五、修复工作量估算

| 分类 | 条数 | 单条修复量 | 估算总工时 |
|------|------|-----------|-----------|
| P0 High | 3 | 3~20 行 | **0.5 天** |
| P1 Medium | 15 | 5~50 行 | **2~3 天** |
| P2 Low（批量收拾）| 18 | 1~10 行 | **1 天** |
| **合计** | **36** | —— | **3~4 天** |

修复量之所以集中，原因有二：
1. 多数问题是**遗漏补齐**（键名映射、EscapeArg 补全、using 补全、lock 补全）——不是逻辑重写
2. 少数"架构级"问题（OutputContinuer 贯穿 CancellationToken、SlotAffinity 锁内 IO）需要方法签名变更但影响面可控

---

## 六、风险分层

### 即发风险（当前可触发）
- **H3 CTS 泄漏**：每次手动采集监控快照创建 3 个未 Dispose 的 CTS，句柄持续增长直到 GC
- **M1 HttpListener 连接泄漏**：aggressive trim 失败或自愈重发仍失败时漏掉 Close，积累后可能耗尽端口
- **H2 CPU 采样错乱**：如果未来 UI 组件并发采样（如多面板打开），CPU 百分比会跳变

### 长期劣化型（慢积累）
- **M11 SlotPanelView 行无界增长**：槽位频繁绑定/解绑时 DataGridViewRows + _slotMgmtRowIdx 持续膨胀
- **M13 RestoreStats._pending 无上限**：后端长时间不输出 prompt eval 时持续入队
- **M14 PruneStaleBindings 只启动跑一次**：运行时 stale 绑定累积
- **M10 _byKey + _refPrefillTpsBy 无界增长**：长期运行内存缓慢泄漏

### 契约破坏型（最隐蔽但影响最大）
- **H1 MetricKeys × ValueOf 键覆盖错位**：用户按文档写的阈值规则大面积静默不生效
- **M20 吞吐告警门控缺新键名**：空闲态不跳过，周期性误报 tg_tps 骤降

---

## 七、总结

LlamaHarness v2.30 是一个**架构演进中保持了工程纪律**的项目：290/290 测试全绿、build 零警告、并发模型基础扎实、UI 跨线程规范统一、KV 缓存设计成熟。

本轮审计发现的核心问题集中在**"新模块收尾"和"跨模块契约漂移"**：MetricKeys 注册表与 ValueOf 键覆盖错位、MonitorCollector 的 CTS 未 Dispose、OutputContinuer 无 CancellationToken 贯穿——这些都是设计意图已经清晰但实现环节遗漏的点。

**修复工作量估算 3~4 天，核心 High 修复仅 0.5 天**，投入产出比很高。

建议下一迭代按 P0 → P1 → P2 顺序执行，其中 P1 的"异步取消语义收尾"和"会话级状态容器淘汰"两个根因族值得优先处理，因为它们是跨多个模块的模式性问题。

---

*本评审报告基于 2026-09-05 全量代码审计生成*  
*审计产物目录: `audit_output/LlamaHarness_20260905_v230/`*
