# SmartScheduler.cs 改进方案（方法粒度 + 文件组织渐进重构）

> 版本：v1.0（2026-08-31）｜基线：`refactor/mainform` 分支 HEAD（cc51617 之后）｜测试基线：82/82 全绿
> 前置：MainForm 分层重构（v2.14）已全部落地；本方案沿用其「每步可编译可回退」验收范式。

---

## 一、现状诊断

### 1.1 规模与职责全景

`SmartScheduler.cs` **2098 行 / 53 个方法**，承担调度器全部职责：

| 职责域 | 代表方法 | 行数占比 |
| --- | --- | --- |
| HTTP 监听与请求分发 | StartListening / AcceptLoopAsync / HandleRequestAsync / WarnNonStreamOnce | ~180 |
| 网关预处理（思考拦截/槽位亲和/KV restore/TokenGuard/强制流式/前缀哈希） | PrepareGatewayAsync / ApplySlotAffinityAsync / LogPrefixHash | ~340 |
| 转发与响应管道（含 400 自愈/崩溃恢复/断点清理/客户端断开兜底） | ForwardAsync / SendAndPipeAsync / DumpRequest | ~300 |
| 生命周期（唤醒/就绪/预热/休眠/停止/Dispose） | WakeUpAsync / WaitReadyAsync / RunWarmingAsync / SleepNowCoreAsync / SaveAllSlotsBeforeStopAsync / OnTick | ~380 |
| 崩溃恢复协调 | RunCrashRecoveryAsync / RestartAndReplayAsync / ProbeClientConnectedAsync / RunKeepAliveAsync | ~140 |
| 思考模式状态机 | SetThinkingMode / DetermineInitialThinkingMode / InjectThinkingMode | ~220 |
| 纯静态辅助（端口/槽位/JSON/请求判定） | PickFreePort / PickWarmSlot / ReadRequestBodyAsync / BuildBackendRequest / IsInferenceRequest / DetectToolLoop / PrefixHash / ContentLen / WriteJsonAsync / WriteError 等 | ~300 |

### 1.2 关键病灶（实测方法体行数，括号配平）

| 方法 | 行数 | 混合的子流程 |
| --- | --- | --- |
| `SendAndPipeAsync` | **~200** | 连接异常 500ms 重试 + **400 上下文超限自愈**（激进裁剪/KV 废弃/重发）+ SSE 响应管道 + 崩溃恢复协调 + 断点快照清理 + 客户端断开兜底 |
| `WakeUpAsync` | **~118** | 参数校验 + 端口探测 + P 核线程钳制 + 进程拉起 + P 核绑定 + 思考基线 + 槽位亲和装配 + KV 初始化 + 就绪等待 |
| `ApplySlotAffinityAsync` | **~110** | 指纹判定 + 已有绑定/KV restore + 新绑定/驱逐 + LRU + n_slots 注入 |
| `PrepareGatewayAsync` | **~133** | 思考拦截 + 槽位亲和 + KV restore + TokenGuard + 强制流式 + 前缀哈希 + 工具循环检测 |
| `InjectThinkingMode` | **~92** | 末条 user 消息扫描 + 5 档命中判定 + 注入 enable_thinking/reasoning_effort + 联动 Tool 检测 |
| `HandleRequestAsync` | **~75** | 读体 + dump + 网关预处理编排 + 转发调用 |

**6 个大方法合计 ~790 行，占总行数 38%**。方法粒度偏大是主要病灶；其次是单文件 2098 行影响导航与评审。

### 1.3 与 MainForm 重构的本质差异（决定策略）

| 维度 | MainForm（已重构） | SmartScheduler |
| --- | --- | --- |
| 角色 | 无状态 View，事件驱动 | **有状态中心节点**：22+ 共享可变字段、53 方法互调、7 事件、进程/HTTP/KV 三态耦合 |
| 可拆性 | 独立类收益高（控件自持） | **强行拆有状态类需搬状态+重接依赖，行为风险高、收益有限**（用户已认可"调度器是天然中心节点"） |
| 改进重点 | 拆职责（View/Presenter/Controller） | **降方法粒度 + 按文件聚类组织** |

> **结论：不强行拆有状态类。** 聚焦 ① 抽纯静态工具（零风险）；② 大方法内部子流程提取（降粒度）；③ partial 文件聚类（改善组织）。

---

## 二、目标架构

```
SmartScheduler.cs（壳，~600 行）
├── 字段/常量/事件/公开 API 门面（Program/MainForm/Presenter 可见面不变）
├── partial：SmartScheduler.Http.cs      —— 监听 + accept + 请求分发（~180 行）
├── partial：SmartScheduler.Gateway.cs   —— 网关预处理 + 槽位亲和 + KV（~340 行）
├── partial：SmartScheduler.Pipeline.cs  —— 转发 + 响应管道 + 400 自愈（~300 行）
├── partial：SmartScheduler.Lifecycle.cs —— 唤醒/就绪/预热/休眠/停止（~380 行）
├── partial：SmartScheduler.Crash.cs     —— 崩溃恢复 + keep-alive（~140 行）
└── 独立 static 类（零实例依赖，可单测）：
    ├── RequestProcessor.cs   —— ReadRequestBodyAsync/BuildBackendRequest/WriteJsonAsync/WriteError/
    │                            IsInferenceRequest/IsChatCompletions/DetectToolLoop/PrefixHash/ContentLen/EnsureStreamTrue
    ├── ThinkingMode.cs       —— DetermineInitialThinkingMode/LabelOf/EffortOf/InjectThinkingMode/InjectNSlots
    └── SchedulerUtils.cs     —— PickFreePort/PickWarmSlot
```

**目标**：`SmartScheduler.cs` 2098 → ~500-700 行；单方法上限 ≤60 行；职责按文件聚类；纯逻辑可单测。

---

## 三、拆分设计（类级规格）

### 3.1 RequestProcessor（static，新增）

**依据**：以下 10 个方法已逐个体扫描确认**零实例字段访问**（`_cfg/_hc/_server/_listener/_backendPort/_phase/_affinity/_kvCache/_thinkingMode/_wakeTask/_prefixHashes/_recentOutput/_tickTimer` 均未出现），可安全迁出为纯静态。

| 方法 | 签名（保持不变） | 说明 |
| --- | --- | --- |
| `ReadRequestBodyAsync` | `static Task<byte[]?> (HttpListenerRequest req)` | 仅 POST 读体 |
| `BuildBackendRequest` | `static HttpRequestMessage (HttpListenerRequest req, Uri uri, byte[]? bodyBytes)` | 过滤逐跳头 |
| `WriteJsonAsync` | `static Task (HttpListenerContext ctx, int code, string json)` | JSON 响应 |
| `WriteError` | `static void (HttpListenerContext ctx, int code, string msg)` | 错误响应 |
| `IsInferenceRequest` | `static bool (HttpListenerRequest req)` | 推理请求判定 |
| `IsChatCompletions` | `static bool (string path)` | 路径判定 |
| `DetectToolLoop` | `static bool (JsonObject obj)` | 工具循环检测 |
| `PrefixHash` | `static string? (JsonObject obj)` | 轻量前缀指纹 |
| `ContentLen` | `static int (JsonObject? m)` | 消息长度 |
| `EnsureStreamTrue` | `static string? (string json)` | 非流式 → 强制流式 |

> 校验方式：抽前对每个方法体做「实例字段引用扫描」断言，抽后 build + 82 测试双重确认。含 `ref/out` 的成员不在本类（见 ThinkingMode）。

### 3.2 ThinkingMode（static，新增）

| 方法 | 签名（保持不变） | 说明 |
| --- | --- | --- |
| `DetermineInitialThinkingMode` | `static ThinkingLevel (string extraArgs)` | 启动参数 → 基线档位 |
| `LabelOf` | `static string (ThinkingLevel lvl)` | 档位 → 中文标签 |
| `EffortOf` | `static string? (ThinkingLevel lvl)` | 档位 → reasoning_effort |
| `InjectThinkingMode` | `static void (JsonObject obj, ref ThinkingLevel level, out string? effortFix)` | **92 行大方法**：末条 user 扫描 + 5 档命中 + 注入（步骤 4 可继续拆小） |
| `InjectNSlots` | `static bool (JsonObject obj, int slot)` | n_slots 注入 |

> `InjectThinkingMode` 含 `ref ThinkingLevel` + `out` 参数，签名保持原样即可迁出；迁出后其内部 `ThinkingLevel` 枚举引用不受影响（同命名空间）。

### 3.3 SchedulerUtils（static，新增）

| 方法 | 签名（保持不变） |
| --- | --- |
| `PickFreePort` | `static int (int preferred)` |
| `PickWarmSlot` | `static int (int parallel, IEnumerable<int> kvBoundSlots)` |

### 3.4 SmartScheduler.cs —— 壳（partial 主文件）

保留：字段/常量/7 事件/枚举 Phase/ThinkingMode 属性/公开 API 门面（`Initialize/SetThinkingMode/StopNow/SetAutoMode/Dispose` 等 Program/MainForm/Presenter 依赖的成员）+ 状态机（`SetPhase/OnTick`）+ 线程同步。

### 3.5 partial 分文件（方法体零改动，只移动位置）

| 文件 | 迁入方法（保持 internal/private 语义不变，同类互调不受影响） |
| --- | --- |
| `SmartScheduler.Http.cs` | StartListening / StopListening / AcceptLoopAsync / HandleRequestAsync / WarnNonStreamOnce |
| `SmartScheduler.Gateway.cs` | PrepareGatewayAsync / ApplySlotAffinityAsync / ParseAutoPreemptivePrefixes / IsAutoPreKey / ParseAutoSnapshotPrefixes / IsAutoSnapshotKey / LogPrefixHash |
| `SmartScheduler.Pipeline.cs` | ForwardAsync / SendAndPipeAsync / DumpRequest / IsInferenceRequest / IsChatCompletions |
| `SmartScheduler.Lifecycle.cs` | EnsureRunningAsync / WakeUpAsync / WaitReadyAsync / RunWarmingAsync / SleepNow / SleepNowCoreAsync / SaveAllSlotsBeforeStopAsync / StopNow / OnServerExited / VerifyVramReleasedAsync / OnTick / SetPhase / Dispose |
| `SmartScheduler.Crash.cs` | RunCrashRecoveryAsync / RestartAndReplayAsync / ProbeClientConnectedAsync / RunKeepAliveAsync |

> partial 类的实例字段、事件、方法跨文件可见，互调零改动。此步纯搬移，行为等价性由「方法体逐字不变 + 编译 + 测试」三重保证。

### 3.6 SendAndPipeAsync 子流程提取（步骤 2，方法内重构）

`SendAndPipeAsync`（~200 行）按「编排骨架 + 子流程方法」重组：

| 新私有方法 | 迁出的子流程 | 原行号区间 |
| --- | --- | --- |
| `TryRecoverContextOverflowAsync` | 400 上下文超限自愈（读取 errBody → TokenGuard 激进裁剪 → KV 废弃 → 重发） | ~L995-1060 |
| `TryConnectWithRetryAsync` | 连接异常 500ms 重试一次 | ~L977-987 |
| `PumpResponseAsync` | SSE 响应管道 + 崩溃恢复协调 + 断点快照清理 + 客户端断开兜底 | ~L1060-1169 |

重组后 `SendAndPipeAsync` ≈ 40-50 行：读体 → 建消息 → 连接重试 → 400 自愈 → 泵响应 → 收尾。**每个子流程仍是原逻辑逐字搬迁，仅加方法签名包裹。**

---

## 四、实施步骤（每步可编译 + 82 测试全绿 + commit）

### 步骤 1：抽纯静态工具类（零风险）

- 新建 `RequestProcessor.cs` / `ThinkingMode.cs` / `SchedulerUtils.cs`（3.1-3.3）
- 从 SmartScheduler.cs 删除 17 个已迁出静态方法，调用点改 `RequestProcessor.`/`ThinkingMode.`/`SchedulerUtils.` 前缀
- 脚本：按精确文本块删除 + 正则改调用点（沿用 MainForm 重构的 here-string + To-CrLf 校验范式）
- **预期**：SmartScheduler.cs ~1800 行；build 0 警告 0 错误 + 82 测试全绿

### 步骤 2：SendAndPipeAsync 子流程提取（降粒度）

- 提取 `TryRecoverContextOverflowAsync` / `TryConnectWithRetryAsync` / `PumpResponseAsync`（3.6）
- `SendAndPipeAsync` 200 → ~50 行
- **验收**：400 自愈/崩溃恢复/断点清理行为逐字等价；build + 82 测试全绿

### 步骤 3：partial 文件聚类（结构重组）

- 新建 `SmartScheduler.Http.cs` / `.Gateway.cs` / `.Pipeline.cs` / `.Lifecycle.cs` / `.Crash.cs`（3.5）
- `SmartScheduler.cs` 声明改 `public sealed partial class SmartScheduler`
- **预期**：SmartScheduler.cs ~500-700 行；build + 82 测试全绿

### 步骤 4（可选进阶）：剩余大方法继续拆小

- `InjectThinkingMode`（92 行）→ 拆「末条 user 扫描」+「命中判定」+「注入」三段私有方法
- `WakeUpAsync`（118 行）→ 拆「参数校验」+「进程拉起」+「装配初始化」分段
- `ApplySlotAffinityAsync`（110 行）→ 拆「已有绑定/KV restore」+「新建绑定/驱逐」分段
- **收益**：单方法上限进一步降到 ~50 行；**代价**：跨段共享局部变量多，需传参/改字段，风险略升，需另行评估

---

## 五、风险与红线（重构必须守住）

1. **不强行拆有状态类**：调度器是天然中心节点（22+ 共享字段互锁），拆类收益低于风险；只做 纯静态抽取 + 方法内子流程提取 + partial 聚类 三档。
2. **不改公开 API**：Program / MainForm / MainFormPresenter 对 `SmartScheduler` 的构造与成员调用不变；7 事件签名不变。
3. **行为等价为唯一验收**：静态方法签名逐字不变（含 `ref ThinkingLevel` / `out string?` / 返回元组）；partial 搬移方法体零改动；SendAndPipeAsync 子流程逐字搬迁。
4. **实例依赖扫描前置**：抽静态类前对每个候选方法做实例字段引用扫描（本方案已预扫 17 个全绿），杜绝隐性实例访问。
5. **不引入新依赖 / 不改业务逻辑**：任何步骤禁止顺手改调度算法、事件触发时机、锁粒度。
6. **每步独立提交可回退**：`refactor(stepN): …`，失败即 `git checkout` 回退。

## 六、验收标准

- [ ] 步骤 1-3 全部落地：`dotnet build` 0 警告 0 错误 + 82 测试全绿
- [ ] `SmartScheduler.cs` 2098 → ~500-700 行；`SendAndPipeAsync` ≤60 行
- [ ] 单方法上限 ≤60 行（步骤 4 后进一步收紧）
- [ ] 冒烟：启动/停止/休眠/推理转发/KV 复用/崩溃恢复全流程行为与重构前一致
- [ ] git log 每步独立 commit，可逐级回退
