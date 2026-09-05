# Findings Summary — LlamaHarness v2.30 审计

**代码基线**: git `240b3b6`（v2.30 feat(slot): 主从槽位隔离架构）  
**审计方式**: 8 组并行逐片审计 → L2 汇总去重 + High 复核  
**覆盖范围**: 57 主工程文件 + 34 测试文件 = 91 文件 / ~23K 行

---

## 一、复核后严重度分布

| 严重度 | 数量 | 说明 |
|--------|------|------|
| **High** | 2 | 当前可触发的正确性/契约破坏 |
| **Medium** | 15 | 资源泄漏/竞态/无界增长/设计未落地 |
| **Low** | 18 | 魔法值/注释不符/死代码/风格 |
| **Observation** | 12 | 防御建议/微优化/观察 |

### High 复核结果（逐条回源）

| 原始组 | 原始标记 | 复核后 | 原因 |
|--------|----------|--------|------|
| g2 | AffinityRuleMatcher 原地排序 | → **Medium** | 触发条件严格（需调用方传数组而非 List），当前 SmartScheduler 传 List 不触发；但公共 API 设计有缺陷 |
| g2 | KvCacheManager IDisposable 接口缺失 | → **Low** | 有 public Dispose() 方法，GC 兜底；宿主可手动调；纯风格问题 |
| g3 | SystemMetrics.GetCpuPercent 多线程无锁 | → **High ✅** | 四字段无锁读写，真实竞态；修复量 <5 行 |
| g3 | LlamaCppMonitor 三个 CTS 未 Dispose | → **High ✅** | CreateLinkedTokenSource 持有内核句柄+父回调注册；GC 前句柄泄漏 |
| g5 | SlotPanelView 行无界增长 | → **Medium** | 触发需绑定频繁变更；GDI 泄漏真实但慢；加 bindings.Count==0 清理 5 行搞定 |
| g7 | MetricKeys × PerfAnalyzer 键覆盖 90% 错位 | → **High ✅** | 配置驱动契约破坏；用户写 `"gen_tps"` 规则静默不生效；修复量 <20 行 |

---

## 二、High 详细清单

### H1. MetricKeys 注册表与 PerfAnalyzer.ValueOf 键覆盖错位（配置驱动契约破坏）
- **文件**: [MetricKeys.cs](file:///c:/project/lunch/LlamaHarness/MetricKeys.cs) × [PerfAnalyzer.cs:L170-183](file:///c:/project/lunch/LlamaHarness/PerfAnalyzer.cs#L170-L183)
- **触发**: 用户按 MetricKeys 文档写 `"gen_tps"` / `"prompt_eval_tps"` / `"vram"` 等配置驱动阈值规则
- **后果**: 27 个 MetricKeys 注册键中仅 3 个被 ValueOf 识别；其余全部在 EvaluatePoints 中返回 null → 规则静默跳过，用户无任何提示
- **证据链**: MetricKeys 声称"唯一权威注册表"，ValueOf 是指标值读取唯一入口，但仅覆盖 `"cpu"` / `"vram_mb"` / `"pp_tps"` / `"tg_tps"` 等旧键名
- **修复量**: 在 ValueOf switch 中补齐 MetricKeys 所有键名的等价映射

### H2. SystemMetrics.GetCpuPercent 四字段无锁并发读写
- **文件**: [SystemMetrics.cs:L43-64](file:///c:/project/lunch/LlamaHarness/SystemMetrics.cs#L43-L64)
- **触发**: 多个独立 UI 组件同时通过 Timer 周期采样调用 GetCpuPercent()
- **后果**: `_prevIdle`/`_prevKernel`/`_prevUser`/`_hasSample` 四个字段被半更新状态读取，CPU 百分比跳变到 0% 或 >100%
- **修复量**: 方法体加 `lock (_gate)` 或 `[MethodImpl(Synchronized)]`（5 行内）

### H3. LlamaCppMonitorCollector 三个 Linked CTS 未 Dispose
- **文件**: [LlamaCppMonitor.cs:L111-113](file:///c:/project/lunch/LlamaHarness/LlamaCppMonitor.cs#L111-L113)
- **触发**: CaptureSnapshotAsync 每次调用必然创建 3 个 CreateLinkedTokenSource
- **后果**: CTS 持有内核 ManualResetEvent 句柄 + 回调挂在父 timeoutCts.Token 上；GC 触发终结器前持续泄漏；高频调用时句柄缓慢堆积
- **修复量**: 三个 CTS 改用 `using var`（3 行）

---

## 三、Medium 详细清单（15 条）

### 资源泄漏 / 生命周期（6）
| # | 标题 | 文件 | 简述 |
|---|------|------|------|
| M1 | TryRecoverContextOverflowAsync 两条失败路径未 Close ctx.Response | [Pipeline.cs:L217,L268](file:///c:/project/lunch/LlamaHarness/SmartScheduler.Pipeline.cs#L217) | HttpListener 连接泄漏；aggressive trim 失败或重发仍失败时跳过 PumpResponseAsync 的 finally Close |
| M2 | LlamaCppMonitor 三个 Linked CTS 未 Dispose | [LlamaCppMonitor.cs:L111](file:///c:/project/lunch/LlamaHarness/LlamaCppMonitor.cs#L111) | 内核句柄 + 父回调注册（High 降 Medium 后此处重复，保留为 High 已单独列出）|
| M3 | OutputContinuer 整条流式管道无 CancellationToken 贯穿 | [OutputContinuer.cs:L41-45](file:///c:/project/lunch/LlamaHarness/OutputContinuer.cs#L41) | 客户端断开后续接循环仍跑，浪费后端资源 |
| M4 | LlamaServerClient.SendAsync 看门狗 Delay 用 CancellationToken.None | [LlamaServerClient.cs:L65](file:///c:/project/lunch/LlamaHarness/LlamaServerClient.cs#L65) | 外部取消后 delayTask 仍等满 timeout |
| M5 | PerfLog.Start RotateLocked 后叠加 OpenWriter 句柄延迟释放 | [PerfLog.cs:L37-38](file:///c:/project/lunch/LlamaHarness/PerfLog.cs#L37) | RotateLocked 内已 _writer = OpenWriter()，Start 紧接着又 OpenWriter 覆盖，第一次的延迟释放 |
| M6 | MainForm._tooltip 未 Dispose（Component 泄漏） | [MainForm.Ui.cs:L82](file:///c:/project/lunch/LlamaHarness/MainForm.Ui.cs#L82) | ToolTip 持内部 GDI/句柄；依赖 GC finalizer |

### 并发 / 竞态（4）
| # | 标题 | 文件 | 简述 |
|---|------|------|------|
| M7 | HandleHealth 同步阻塞 GetAwaiter().GetResult() | [Http.cs:L255](file:///c:/project/lunch/LlamaHarness/SmartScheduler.Http.cs#L255) | 反模式；当前在线程池安全，但 UI 线程调用会死锁 |
| M8 | Handler 看门狗超时后 handlerTask 继续后台跑 | [Http.cs:L365](file:///c:/project/lunch/LlamaHarness/SmartScheduler.Http.cs#L365) | 300s 超时后 handlerTask 不 Cancel，可能写已 Close 的 ctx.Response |
| M9 | LogPipeline.Shutdown 与并发 Enqueue 窗口竞态 | [LogPipeline.cs:L324,L446](file:///c:/project/lunch/LlamaHarness/LogPipeline.cs#L324) | 关闭阶段最后若干条日志静默丢失（尽力而为可接受） |
| M10 | RestoreStats._byKey + _refPrefillTpsBy 无界增长 | [RestoreStats.cs:L28,L34](file:///c:/project/lunch/LlamaHarness/RestoreStats.cs#L28) | 长期运行内存泄漏；字典条目只增不减 |

### 无界增长（3）
| # | 标题 | 文件 | 简述 |
|---|------|------|------|
| M11 | SlotPanelView.RefreshSlotMgmtGrid 行只增不减 + 陈旧引用 | [SlotPanelView.cs:L113](file:///c:/project/lunch/LlamaHarness/SlotPanelView.cs#L113) | DataGridViewRows + _slotMgmtRowIdx 不清理已解绑 key |
| M12 | SlotAffinity.Save() 锁内完整文件 IO 链 | [SlotAffinity.cs:L290](file:///c:/project/lunch/LlamaHarness/SlotAffinity.cs#L290) | 热路径 GetSlot 被磁盘抖动阻塞；应改锁外写盘 |
| M13 | RestoreStats._pending 队列无容量上限 | [RestoreStats.cs:L27](file:///c:/project/lunch/LlamaHarness/RestoreStats.cs#L27) | 后端长时间不输出 prompt eval 时持续入队 |

### 设计未落地（2）
| # | 标题 | 文件 | 简述 |
|---|------|------|------|
| M14 | SlotAffinity 绑定表 PruneStaleBindings 只启动跑一次 | [SlotAffinity.cs:L68](file:///c:/project/lunch/LlamaHarness/SlotAffinity.cs#L68) | 运行时 stale 绑定累积；应该后台定时跑或随 GetSlot 刷新 |
| M15 | KvCacheManager.ClearAllAsync 超时保护失效 | [KvCacheManager.cs:L353](file:///c:/project/lunch/LlamaHarness/KvCacheManager.cs#L353) | WhenAll 不接受 CancellationToken，5s CTS 形同虚设 |

### 其他 Medium
| # | 标题 | 文件 | 简述 |
|---|------|------|------|
| M16 | LlamaFinder.BuildArgs LoadMode/SpecType 未 EscapeArg | [LlamaFinder.cs:L120](file:///c:/project/lunch/LlamaHarness/LlamaFinder.cs#L120) | 参数边界错乱风险 |
| M17 | LlamaServerClient GetMetricsAsync/GetJsonAsync 吞没 OCE | [LlamaServerClient.cs:L171](file:///c:/project/lunch/LlamaHarness/LlamaServerClient.cs#L171) | OperationCanceledException 应穿透 |
| M18 | TokenGuard 截断可能切断 surrogate pair | [TokenGuard.cs:L170](file:///c:/project/lunch/LlamaHarness/TokenGuard.cs#L170) | emoji 被切断后 JSON 序列化偏差 |
| M19 | AppConfig.Sanitize 字符串字段无格式校验 | [AppConfig.cs:L181](file:///c:/project/lunch/LlamaHarness/AppConfig.cs#L181) | ExePath 空串、PCoreMask 非法格式直通 |
| M20 | 吞吐类告警负载门控硬编码缺失新键名 | [PerfAnalyzer.cs:L202](file:///c:/project/lunch/LlamaHarness/PerfAnalyzer.cs#L202) | `"gen_tps"`/`"prompt_eval_tps"` 在空闲态不跳过，周期性误报 |

---

## 四、Low 摘要（按簇归类，完整清单见各分组 findings）

### 并发/线程族
- SetPhase TOCTOU 竞态（Interlocked.CompareExchange 可解）
- RoundStats 锁外触发事件回调（double 字段半更新）
- PerfEventTracker 用 DateTime.Now 而非 MonotonicClock
- IdleMinutes public 可写字段撕裂风险（int 在 x64 安全，但声明脆弱）

### 性能/分配族
- ParseAutoPreemptivePrefixes 每次请求重建 List（热路径分配）
- MarkdownRenderer.StripMdInline 每次 new 3 个 Regex
- GetRounds 每次 OrderBy + ToList

### 魔法值/硬编码族
- Page/Column 索引数字（3/4/6/7）散落各处
- AppConfig 候选路径硬编码
- UiTheme / MainForm.Ui Color.FromArgb 魔法值与 C_* 常量平行

### 契约/风格族
- FlatButton 插断 MakeSectionTitle 的 XML 注释
- PerfAnalyzer 枚举比较依赖 PerfAlarmLevel 排序（Warn=0/Crit=1）
- BuildPage RowStyles/ColumnStyles 只增不减（当前只 Build 一次，但防御性不足）
- LlamaServerProcess.OutputLine 事件非 UI 线程触发（文档声明但未封装切换）
- PerfLog TimeoutMs=3000 无配置化

### 死代码/冗余族
- LlamaServerProcess.Dispose 冗余调 _proc.Dispose
- OutputContinuer 整条管道无取消通路（已列入 Medium，此处补充）
- OnServerExited StopNow 后的分支是 dead path（防御性设计，加注释即可）

---

## 五、Observation 摘要（不进待修复清单）

| # | 观察标题 | 简述 |
|---|----------|------|
| O1 | OutputContinuer finally Close 与 TryRecover 分支不对称 | 建议把 Close 上收到 SendAndPipeAsync 级别 |
| O2 | Lazy<IBackendClient> Dispose 竞态 | Dispose 与首次并发访问时 Lazy 工厂可能创建不被 Dispose 的客户端 |
| O3 | WriteErrorV2 / WriteError 两套错误响应格式并存 | Pipeline.cs 有 8 处旧格式未迁移 |
| O4 | TrimToQuota 每次 save 都触发应节流 | 累积 10 次跑一次即可 |
| O5 | Mutex 持有窗口 | Program.cs catch 块期间 Mutex 持续持有（合理） |
| O6 | UiTheme.IconCache 无 lock | 当前仅 UI 构建时单线程调用，暂无触发路径 |
| O7 | BackendClientFactory 每次 new 但共享 HttpClient（安全） | 生产高频创建销毁无问题 |
| O8 | SystemMetrics nvidia-smi Kill 窗口 | 3s Kill 后 WaitForExit 可能短暂句柄泄漏（极端） |
| O9 | SlotAffinity 构造函数 Load→Prune→Enforce 顺序耦合 | 隐式依赖，容易被未来重构破坏 |
| O10 | RequestTimingTracker._open 无上限 | Begin 后 Complete 不调用会长期占用 |
| O11 | MetricKeys All 列表不含事件型指标 op 值 | PerfEvent.Category + Op 散落在注释中无集中定义 |
| O12 | PerfLog.Write 写本地时间 vs PerfPoint.Ts 单调时钟 | 跨时区迁移时解析有偏差 |

---

## 六、v2.26 → v2.30 修复验证（旧 High 项）

| 旧项 | 描述 | 状态 | 备注 |
|------|------|------|------|
| B8 | GatewayErrorCode 未统一 | ✅ 已解决 | GatewayErrorCodes.cs 统一字符串 code + 错误格式 |
| M1 | UpdatePropsCard 控件无界增长 | ✅ 已修复 | DisposeChildren + RowStyles.Clear + RowCount=0 三件套 |
| M2/M3 | StatusPanelView/MainForm.Ui 控件泄漏 | ⚠️ 部分修复 | PerfTrendChart 已 Font 提升为实例字段 + Dispose 重写；其余处仍需关注 |
| D3 | NowMonotonic 定义未用 | ✅ 已使用 | MonotonicClock 独立实现，PerfPoint.Ts 改用 |
| M7 | 采样点用 DateTime.Now | ⚠️ 部分 | PerfEventTracker 仍用 DateTime.Now；PerfSampler 采样点已用 MonotonicClock |

---

## 七、系统性问题根因归纳

1. **WinForms 资源治理不统一**：`new Font` 不 Dispose、控件动态创建无清理、Component（ToolTip）生命周期依赖 GC。根因：无统一的"控件+Font+Component"清理工具类，各 View 各实现。
2. **配置驱动契约漂移**：MetricKeys 注册表与 PerfAnalyzer.ValueOf 键覆盖 90% 错位。根因：v2.22 引入注册表后，ValueOf 没同步更新，旧键名与新键名并存。
3. **会话级状态容器无淘汰策略**：RestoreStats._byKey、_pending、SlotAffinity._bindings（Prune 只启动跑一次）。根因："会话级"假设被打破——unknown key、动态 Header key 导致条目持续累积。
4. **异步模式收尾不彻底**：OutputContinuer 无 CancellationToken、Handler 超时后不 Cancel、GetMetricsAsync 吞没 OCE、HandleHealth 同步阻塞。根因：fire-and-forget / 看门狗 / 快速实现等路径牺牲了取消语义的完整性。
5. **注释声称 vs 实际实现脱节**：PerfAnalyzer.ValueOf 注释说"长短名兼容"但只覆盖旧短名；AffinityRuleMatcher 注释说"避免 LINQ 分配"但 as-cast 引入了隐藏副作用。根因：代码演进过程中注释没跟上实现。

---

## 八、模块健康度分布

| 模块 | High | Medium | Low | 健康度 |
|------|------|--------|-----|--------|
| g1 调度核心 | 0 | 3 | 6 | ★★★★☆（并发模型健壮，多数是收尾问题） |
| g2 KV 缓存与槽位亲和 | 0 | 5 | 3 | ★★★★☆（Prune 运行时不跑是最大问题） |
| g3 后端契约与进程 | 2 | 5 | 6 | ★★★☆☆（两个 High 都是修复量 <5 行的遗漏） |
| g4 UI 外壳与配置 | 0 | 3 | 5 | ★★★★☆（跨线程/async void/事件订阅矩阵已验证安全） |
| g5 UI 面板与监控页 | 0→1 | 2 | 3 | ★★★☆☆（SlotPanelView 行无界增长降级 Medium） |
| g6 日志与配置模型 | 0 | 5 | 3 | ★★★★☆（Shutdown 竞态影响有限） |
| g7 性能监控核心 | 1 | 6 | 3 | ★★★☆☆（MetricKeys 契约破坏 + 多个 Medium） |
| **合计** | **3→2** | **15** | **18** | **★★★★☆（良好）** |

注：g3 原始 High 2 条，复核后 2 条保留；g5 原始 High 1 条降级为 Medium；g7 原始 High 1 条保留。最终 High = 3 条。

---

*完成时间: 2026-09-05*  
*审计产物: 详见 `findings/g1.md` ~ `g7.md`*
