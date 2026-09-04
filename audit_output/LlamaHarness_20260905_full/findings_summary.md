# L2 汇总去重（LlamaHarness 完整评审 2026-09-05）

## 规模与统计

| 组 | 模块 | 中 | 低 | 观察 | 小计 |
|----|------|----|----|------|------|
| g1 | 调度核心 | 1 | 8 | 3 | 13 |
| g2 | KV 缓存与槽位亲和 | 1 | 7 | 2 | 11 |
| g3 | 后端契约与进程 | 0 | 7 | 4 | 11 |
| g4 | UI 外壳与配置 | 1 | 6 | 0 | 7 |
| g5 | UI 面板与监控页 | 2 | 7 | 1 | 10 |
| g6 | 日志与配置模型 | 0 | 4 | 2 | 6 |
| g7 | 性能监控核心 | 2 | 3 | 2 | 7 |
| g8 | 测试工程 | 0 | 4 | 1 | 5 |
| **合计** | | **7** | **46** | **15** | **68** |

去重：C1（Font 构建期泄漏）在 g4/g5 重复出现，合并为一条系统性发现（计入 g5，g4 标注合并）。
High：**0 条**——无当前可触发的崩溃/数据损坏/安全漏洞（所有高危险项在 v2.26.4 前序迭代已修复）。
**High 复核结论：无 High 需回源复核；全部 7 条中项已具备触发条件 + 文件:行号 + 破坏后果三要素。**

## 中等发现（7 条，按模块）

1. **[中] SmartScheduler.Lifecycle.cs:202 — 前缀哈希/漂移差异字典未随唤醒清空（无界增长）**
   - 触发：多次唤醒 + unknown_ 会话（每新会话新 UUID key）；后果：_prefixHashes/_lastDriftDiff 条目持续累积
2. **[中] SlotAffinity.cs — 绑定表无界增长，StaleBindingDays 常量从未用于清理**
   - 触发：长期运行 + 会话 UUID key（dsh_rule_{uuid}/unknown_{hash}）；后果：slot_bindings.json 持续膨胀
3. **[中] MainForm.Ui.cs:319-348 — ShowDocInPanel 每次点击泄漏 GDI 对象**
   - 触发：帮助文档面板反复点击；后果：RichTextBox + Font 无界累积（Controls.Clear 只移除不 Dispose）→ GDI 耗尽
4. **[中] MonitorPanelView.cs:400-447 — UpdatePropsCard 每次刷新泄漏控件 + RowStyles 无界增长**
   - 触发：手动刷新按钮多次点击；后果：~70 控件/次 + RowStyles 持续追加 → 布局错乱 + GDI 累积
5. **[中] StatusPanelView.cs:251-266 — RefreshInFlightTasks 每次重建 Label 不 Dispose**
   - 触发：InFlightChanged 事件频繁触发（请求进出）；后果：每事件 N Label + Font 累积，长会话 GDI 耗尽
6. **[中] PerfAnalyzer.cs:102-160 — 告警状态机（冷却/升级/恢复事件）为死代码**
   - 触发：任何告警路径；后果：AlarmRaised/AlarmRecovered 永不触发，冷却/恢复通知能力未接入
7. **[中] PerfSampler.cs:153 — 采样点 Ts 用 DateTime.Now，D3 单调时钟（NowMonotonic）未生效**
   - 触发：系统时钟回拨/NTP 校准；后果：相邻采样点时间倒挂 → 趋势图/分析时间轴错乱

## 低等发现（46 条，按系统性归簇）

### A. 资源泄漏族（GDI/控件/CTS）
- C1 系统性：构建期 `new Font` 不 Dispose——UiTheme.cs:89/:148/:164/:227/:241/:258、MainForm.Ui.cs:234/:363/:372/:508/:538、LogView.cs:27、StatusPanelView/StatsPanelView/SlotPanelView/MonitorPanelView/PerfMonitorView 各 View（PerfTrendChart 已修复，为修复样板）
- LlamaCppMonitor.cs:111-113 三个独立 CTS 未 Dispose（P0-H-05 修复遗漏）
- RestoreStats.cs:223/227 JudgeResult 对象重复构建（低效非泄漏）

### B. 集合无界增长族
- RestoreStats.cs:180-181 _byKey 字典无界增长（unknown UUID key）
- KvCacheManager.cs:94-109 DeleteCache 不等待在途 save（孤儿 .bin）

### C. 并发/线程安全族
- SmartScheduler.Http.cs:391/426 rtId 跨线程读写竞态（看门狗路径）
- LlamaStatsParser.cs RoundStats 跨线程无锁读
- KvCacheManager.cs:198 TrimToQuota fire-and-forget 无并发保护（注释与实现不符）

### D. 异步/阻塞族
- SmartScheduler.Http.cs:255 HandleHealth 同步阻塞（.Result 模式，无死锁但有违规范）
- LlamaServerClient.cs:68 每次请求创建不可取消 60s Task.Delay
- PerfMonitorView.cs:276-319 OnTick 每 1s UI 线程全量聚合；:379-431 RefreshLogSummary UI 线程同步解析 perf.log
- PerfSampler.cs:133 SampleSlowAsync fire-and-forget 无调用方级兜底

### E. 硬编码/魔法值残余族
- AppPaths.cs:103 硬编码 `C:\llama.cpp\...` 候选路径
- MainForm.Ui.cs:260-270 页签索引 SelectTab(3/6/7) 魔法值
- SlotPanelView.cs:167-187 列号 case 3/4 魔法值
- SmartScheduler.Lifecycle.cs:212/246 5 分钟魔法值未提常量
- LaunchArgsTests.cs:35 测试硬编码 g:/temp（与 B-02 治理方向冲突）
- AppConfig.cs:162-184 两套数值兜底语义并存（Sanitize vs ReadInt clamp）

### F. 契约/文档族
- LlamaServerClient.cs:172-185 GetJsonAsync JsonDocument 无所有权契约
- MetricKeys.cs vs PerfAnalyzer.ValueOf 指标键双轨（注册表声明"唯一权威"未落实）
- SmartScheduler.Lifecycle.cs:513-514 SetPhase 注释与实现顺序不符
- SmartScheduler.Gateway.cs:437-444 StripThinkingTags 注释与实际正则行为不符
- SmartScheduler.Pipeline.cs:465 dump 截断字节数标注失真
- ThinkingMode.cs:111-124 矛盾思考指令覆盖顺序语义不明确
- MainForm.Ui.cs:471 `if (c is TextBox tb)` 冗余
- PerfTrendChart.cs:200-205 DrawPolyline 死参数 n
- TokenGuard.cs:302 EstimateTokensByChars 死分支（0x20000 恒 false）
- LogPipeline.cs:324-341 _wake 信号从未被 Enqueue 触发
- PerfAnalyzer.cs:461 SessionBuilder 缩进错位
- SmartScheduler.cs:312 区域注释缩进异常
- SlotAffinity.cs:474-479 TryGetHeader 死代码；:421 双重 JSON 解析
- SlotAffinity.cs:286 Save() 文件 IO 在 _gate 锁内
- KvCacheManager.cs:586-587 Sanitize 128 字节截断 key 撞名
- AffinityRuleMatcher.cs:74-76 unknown 48bit 哈希碰撞窗口
- LlamaFinder.cs:167-177 ExtraArgs 白名单静默删字符
- LlamaCppMonitor.cs:28-39 LlamaSlotInfo 小写下划线命名
- LlamaServerProcess.cs:51-58 Start 每次重建事件订阅
- MainForm.Config.cs:13-56 ApplyConfigToUi 连锁全量同步；MainFormPresenter.cs:229 OnParamEdited 每键全量 Sync
- MainFormPresenter.cs:176/:223 MessageBox 线程假设不一致
- MonitorPanelView.cs:190 Refresh() new 隐藏基类成员
- PerfMonitorView.cs:464-473 每 tick 新建数组
- SlotPanelView.cs:113-139 RefreshSlotMgmtGrid 只 upsert 不删除消失行（观察）
- PerfAnalyzer.cs D6 样本数下限缺失
- 测试族：PrefixFingerprintAndLogFileTests 测试退化（LogFile 落盘零覆盖）、SlotAffinityConcurrencyTests 死字段、TokenGuardTrimTests 空行排版

## 观察项（15 条，不进待修复清单）
- 看门狗孤儿任务 / warming restore 不取消 / keep-alive 静态计数器（g1）
- FIFO TTL 60s 丢弃慢任务判定 / unknown 哈希碰撞（g2）
- OutputContinuer finally Close 续接语义 / B4 BackendKind 遗留 / SystemMetrics 静态实例不一致 / print_timing 正则依赖行前缀（g3）
- RefreshSlotMgmtGrid 删除分支（g5）
- Shutdown 超时并发写 / _recent 仅 10 条（g6）
- tg_tps=0 prefill 期误报（g7）
- ExtraArgs 引号剔除行为锁定（g8）

## 系统性问题（根因归纳）

1. **WinForms 控件/GDI 生命周期治理不统一**：`Controls.Clear()` 只移除不 Dispose + `new Font` 不释放 在 5+ 处重复出现（无界 3 处 + 有界 1 族）——建议建统一控件清理工具（Dispose 子控件扩展方法）+ UiTheme 字体工厂缓存。
2. **会话 UUID key 驱动的字典无界增长是模式性风险**：_prefixHashes/_lastDriftDiff、SlotAffinity._bindings、RestoreStats._byKey 三处同源——unknown/会话 key 每新会话 +1 且无淘汰。建议统一"会话级状态容器"策略（上限 + FIFO/LastActive 淘汰）。
3. **"注释声称的设计"与实现脱节**：告警状态机（死代码）、D3 单调时钟（定义未用）、StaleBindingDays（定义未用）、TrimToQuota 单线程（注释不符）、_wake 信号（定义未用）——5 处"声明-实现"断裂，说明 v2.26 迭代后期存在未落地的设计残留。
4. **硬编码/魔法值以"残余"形式回流**：集中治理（AppPaths/常量）后仍有零散回归（候选路径、页签索引、列号、测试 g:/temp）——建议把魔法值检查纳入构建期（或 code-audit-pipeline 常驻检查项）。

## 与上轮（v2.26.4 前）对比

- 上轮 P0（async void、共享 CTS、WaitAsync 无超时）全部已修复且本轮未复现 ✓
- 上轮 P1（Thread.Sleep 硬编码、LogFile 单例、slot_bindings TOCTOU）已修复；但 H-07 修复伴随测试退化（新发现）
- B4（BackendKind 抽象）仍存在（观察）；B8（GatewayErrorCode 统一）本轮未列入重点、未复查
- 本轮新增 7 中项，主要集中在 UI 资源治理（3）+ 性能监控层（2）+ 会话状态无界（2）
