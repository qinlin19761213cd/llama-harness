# LlamaHarness 对抗性代码审查报告

| 项目     | 内容                                                         |
| -------- | ------------------------------------------------------------ |
| 报告版本 | v1.0                                                         |
| 审查日期 | 2026-09-01                                                   |
| 代码基线 | git 7edfdc6（v2.20，主工程 8847 行 / 40 个 .cs）              |
| 对照文档 | docs/代码审计与优化评审报告（2026-08-29）.md（C-001~C-104 / O-13~O-18 / E-1~E-10） |
| 审查范围 | 全部 40 个 .cs 源文件（核心运行时逐行精读，UI 构建层经模式扫描确认） |
| 审查方法 | 对抗视角（像攻击者一样找竞态 / 资源泄漏 / 边界 / 异常路径 / 安全隐患），逐文件精读 + 行号证据 + 与既有审计基线对照去重 |

---

## 1. 总体结论

| 维度 | 结论 |
| ---- | ---- |
| 威胁模型 | 网关仅绑定本机回环（localhost / 127.0.0.1），**排除远程攻击**；但**本机任意进程 / 恶意网页**（浏览器 fetch `http://localhost:8080`）可调用，且网关**无鉴权、无 body 上限、无并发上限、无 Origin/Host 校验**——所有安全项均须按此威胁模型评估 |
| 正确性 | 发现 **2 个功能级 bug**（监控采集端口错位、多模态 content 数组崩溃），常规单文本请求不易触发，但触发即破坏对应功能 |
| 健壮性 | **后端假死无超时看门狗**是最大单点风险（请求永久挂起占用槽位）；若干 `catch-all` 吞异常造成排障盲区 |
| 安全 | 无远程可利用漏洞（回环绑定 + 无第三方依赖）；本机侧风险集中在 **body 无上限（内存 DoS）**、**--host 时后端无鉴权暴露**、**/__status__ 无鉴权信息暴露**、**请求头拼入亲和 key** |
| 性能 | 热路径存在**锁内磁盘 IO**（SlotAffinity.Save / KvCacheManager.SaveIndex / RestoreStats 持久化）、**同步 Sleep 排队阻塞线程池** |
| 与基线对照 | C-001~C-104 已修复项不再重复；本报告为**新增发现**，编号 AH-1~AH-21；另附 4 项经核实排除的疑似问题 |

**总体评价**：代码在单用户本机工具场景下工程质量属上乘——原子写（AppConfig）、有界队列、熔断器、降级路径、线程安全（LogPipeline / LlamaStatsParser / InFlightTracker）均已到位。但对抗视角下仍有 7 高 / 8 中 / 6 低 值得治理，其中 **AH-1（监控端口错位）与 AH-2（content 数组崩溃）为立即可修的正确性 bug**，**AH-3（后端假死无超时）为最值得投入的健壮性短板**。

---

## 2. 高严重度发现（7 项）

### AH-1 【功能 bug · 高】监控采集器端口错位——智能模式下 /slots、/props、/metrics 永远不可达

- **位置**：`MonitorPanelView.cs:243` `int port = _config.Port;`（采集器基址）
- **对比**：`SmartScheduler.Lifecycle.cs:118` `srvPort = AutoMode ? SchedulerUtils.PickFreePort(PreferredBackendPort) : _cfg.Port;`
- **问题**：智能模式下后端 llama-server 实际监听 `Port+1`（或自动探测的空闲端口），而 `LlamaCppMonitorCollector` 固定用**前端网关端口** `_config.Port` 构造基址。网关是代理，`/slots`、`/props`、`/metrics` 不是网关端点 → 三卡片永远显示「✗ 不可用」。
- **对抗场景**：用户全程智能模式（默认 AutoMode=true）时，「系统资源页」的 llama.cpp 三接口监控整体失效，仅本地 CPU/内存/显存采集可用——功能静默降级，无任何报错提示。
- **证据**：`MonitorPanelView.cs:240-245`（`EnsureMonitorCollector`）+ `SmartScheduler.Lifecycle.cs:110-119`（`ResolveLaunchParams`）。
- **修复**：从 `SmartScheduler` 暴露运行时后端端口（如 `public int BackendPort => _backendPort;`），采集器基址改用该值；或在唤醒完成后（端口确定时）再惰性构建采集器。

### AH-2 【功能 bug · 高】OutputContinuer 流式解析对多模态 content 数组崩溃 → 流中断、已生成内容丢失

- **位置**：`OutputContinuer.cs:258` `var content = delta?["content"]?.GetValue<string>();`
- **问题**：OpenAI 规范允许 `content` 为数组（`[{type:"text",text:"..."},{type:"image_url",...}]`）。当 llama-server 返回数组型 content 时，`GetValue<string>()` 抛 `InvalidOperationException`，且该调用**不在任何 try/catch 内**——异常沿 `HandleSseLineAsync → PipeOneRoundAsync → PipeLoop → HandleStreamAsync` 冒泡，被上层 `PumpResponseAsync` 的 catch-all（`Pipeline.cs:265-271`）吞掉并记为「客户端断开」→ 客户端看到**中途截断的 SSE 流**，本轮已生成内容丢失。
- **对抗场景**：任何发送多模态（图/文混排）请求的 Agent 命中即断流；且因异常被归并为「客户端断开」，排查无头绪。
- **证据**：`OutputContinuer.cs:252-282`（`HandleSseLineAsync` 仅对 `JsonNode.Parse` 包 try-catch，`GetValue<string>` 裸露）。
- **修复**：`content` 取值加类型防护（非 string 用 `ToJsonString()` 或跳过累积），对整段 SSE 行处理包 try-catch 防止单行异常中断整条流。

### AH-3 【健壮性 · 高】后端假死无超时看门狗——请求永久挂起占用槽位

- **位置**：`SmartScheduler.cs:29-36`（`HttpClient.Timeout = InfiniteTimeSpan`）+ `OutputContinuer.cs:187`（`await stream.ReadAsync(chunk)` 无读超时）+ `Pipeline.cs`（`TryConnectWithRetryAsync` 仅 catch `HttpRequestException`）
- **问题**：LLM 生成慢是有意为之（禁用客户端超时），但**没有区分「正常慢」与「后端假死」**。若 llama-server 接受连接后卡死（驱动挂起 / 进程假死 / 显存竞争死锁），流式读循环永久阻塞——inflight 永久 +1、槽位被永久占用、线程池线程被吃死；`TryConnectWithRetryAsync` 只重试 `HttpRequestException`，超时/取消类不重试。
- **对抗场景**：后端一次假死即可让 1 个槽位 + 1 个线程永久泄漏；若多请求命中同一后端，网关整体瘫痪，只能杀进程恢复。
- **证据**：`SmartScheduler.cs:35` + `OutputContinuer.cs:187`。
- **修复**：流式读加 **idle 读超时**（如最后字节后 N 分钟无数据 → 判定后端假死 → 断开并告警 + 触发进程健康检查）；对非流式同样加总超时上限。

### AH-4 【健壮性 · 高】PumpResponseAsync catch-all 吞异常——真实错误被归并为「客户端断开」

- **位置**：`SmartScheduler.Pipeline.cs:265-271`
- **问题**：`catch (Exception)` 一律记「客户端断开，已中止本次生成」，**不区分异常类型、不记录异常详情/堆栈**。后端读取失败、管道内部 bug、内存错误全部被归并为「客户端断开」——排障时无法区分「Agent 正常超时」与「网关/后端真实故障」。
- **连带**：该 catch 是崩溃恢复链路的触发判定输入；`PipeResponseAsync` 的 5xx 判定（`Pipeline.cs:324-339`）依赖 `CrashRecovery.WasBadAlloc` 佐证，但流中断路径下非 bad_alloc 的后端错误也可能被误送恢复管道（多余 save/restore/重放）。
- **对抗场景**：后端频繁 5xx（非 OOM）时，每次都被当作「客户端断开」记录，告警失真，真实故障模式无法从日志识别。
- **证据**：`Pipeline.cs:265-271`。
- **修复**：按异常类型分流——`IOException`/`SocketException`/`HttpIOException` = 客户端断开（现状日志）；其余 = 记录完整异常（类型 + Message + StackTrace）到 warn_error 流。

### AH-5 【安全 · 高】请求体无大小上限——本机内存 DoS

- **位置**：`RequestProcessor.cs:16-23`（`ReadRequestBodyAsync`：`req.InputStream.CopyToAsync(ms)` 无 ContentLength/大小校验）
- **问题**：整个请求体全量读入内存 + UTF-8 解码 + `JsonNode.Parse` 整树构建。无上限时，本机恶意进程 / 恶意网页（浏览器 fetch 到 localhost:8080）可 POST 数百 MB body → 内存暴涨 / GC 卡顿 / 网关假死。
- **对抗场景**：恶意网页嵌入 `<script>fetch('http://localhost:8080/v1/chat/completions',{method:'POST',body:大字符串})` 即可耗尽网关内存——浏览器对回环地址无跨域限制（CORS 不拦 localhost 直连）。
- **证据**：`RequestProcessor.cs:20-22`。
- **修复**：按模型上下文上限设 body 上限（如 `CtxSize × 若干倍` 或固定 16MB），超限直接 413；同时对 `ContentLength` 头预校验。

### AH-6 【安全 · 高】WriteError 控制字符未转义——错误响应非法 JSON

- **位置**：`RequestProcessor.cs:176` `var safe = msg.Replace("\\", "\\\\").Replace("\"", "\\\"");`
- **问题**：仅转义 `\` 与 `"`，**未转义 `\n` / `\r` / `\u2028` 等控制字符**。异常消息常含后端原始文本（含换行），拼进 `{"error":"..."}` 后成为**非法 JSON**——严格 JSON 解析的 Agent（pi-ai 等）解析失败，错误信息反而丢失。
- **对抗场景**：后端返回多行错误（如 tokenize 失败详情）→ 网关 503 错误体非法 JSON → Agent 端解析异常，掩盖真实 503 原因。
- **证据**：`RequestProcessor.cs:172-191`（`WriteError` 手写 JSON 拼接）。
- **修复**：`WriteError` 改用 `JsonSerializer.Serialize(new { error = msg })`（或对 `\n\r\u2028\u2029` 一并转义）；`WriteJsonAsync`（`RequestProcessor.cs:159-169`）已走序列化，无此问题。

### AH-7 【效率/正确性 · 高】未知请求随机槽——随机污染已恢复的 KV 快照

- **位置**：`SlotAffinity.cs:76` `return (Random.Shared.Next(_slotCount), null, false, null, -1, false);`
- **问题**：无亲和 key 的未知请求**随机选槽**。该槽若已 restore 快照（绑定 key 的 KV 驻留），未知请求 prefill 会**覆盖槽内 KV** → 下次该 key 请求退化为全量 prefill（KV 复用收益丢失）；多 Agent + 未知客户端混跑时放大。
- **对抗场景**：轮探类/无头请求周期性打到已恢复槽位，静默削弱 KV 命中率（与 RestoreStats 观测的命中率下降叠加，难以归因）。
- **证据**：`SlotAffinity.cs:71-76`。
- **修复**：未知请求改选「无 KV 快照绑定的槽」（复用 `SchedulerUtils.PickWarmSlot` 思路）；或对未知请求标记「临时槽，prefill 后不视为复用」。

---

## 3. 中严重度发现（8 项）

### AH-8 【性能 · 中】SlotAffinity 热路径锁内磁盘 IO + 非原子写

- **位置**：`SlotAffinity.cs:181-182`（`TryAllocateLocked` 锁内 `Save()`）+ `SlotAffinity.cs:262-285`（`SetPreemptive`/`SetKvCache` 锁内 `Save()`）+ `SlotAffinity.cs:333-361`（`Save()` 直接 `File.WriteAllText`）
- **问题**：`GetSlot` 是**每个请求**都走的路径；新建绑定/驱逐时在 `lock(_gate)` 内全量写 `slot_bindings.json`，阻塞所有并发路由。且**非原子写**（无 tmp+rename，对比 `AppConfig.Save` 有原子写）——进程崩溃可能写坏绑定表。
- **修复**：`Save()` 移出锁（脏标记 + 节流后台写），或至少改为 tmp+rename 原子写。

### AH-9 【并发 · 中】GetSlot 排队用 Thread.Sleep 同步阻塞线程池

- **位置**：`SlotAffinity.cs:105-119`
- **问题**：全槽被强占时，新请求在**请求处理线程**上 `Thread.Sleep(1000)` 循环最多 30s 同步阻塞；多并发排队 → 线程池线程被占满 → 线程饥饿/网关无响应。（E-5 已把 Sleep 移出锁，但仍是同步阻塞。）
- **修复**：改 async 版本（`await Task.Delay`），或排队逻辑放到请求管道外层的 async 等待。

### AH-10 【一致性 · 中】ClearAllAsync 与后台 save 竞态

- **位置**：`KvCacheManager.cs:268-296`（`ClearAllAsync` 删除全部 *.bin）+ `SmartScheduler.Pipeline.cs:248-261`（后台 save `Task.Run`）
- **问题**：清缓存时并发后台 save 正在写文件 → 半写文件被删 → 该 key 快照永久丢失；且 save 完成回调 `RecordSave` 把已删 key 重新写回索引 → 缓存「清空」后索引残留过期条目。
- **修复**：清缓存前置「清空中」标志，等待 `_inflightSaves` 排空（或取消）后再删文件，并清空 `_index` 与 `_inflightSaves`。

### AH-11 【可观测性 · 中】fire-and-forget 任务无异常观察者

- **位置**：`SmartScheduler.Lifecycle.cs:318`（`_ = SleepNowCoreAsync()`）、`SmartScheduler.Lifecycle.cs:414`（`_ = VerifyVramReleasedAsync()`）、`Program.cs:26`（仅注册 `AppDomain.UnhandledException`，无 `TaskScheduler.UnobservedTaskException`）
- **问题**：休眠流程/显存校验的 fire-and-forget 任务若抛异常，**静默丢失**（不会写 unhandled.log）。后台 save（`Pipeline.cs:248`）已有内部 try/catch 排除，但上述两处无兜底。
- **修复**：`SleepNowCoreAsync` / `VerifyVramReleasedAsync` 内部包 try/catch + 日志；`Program` 注册 `TaskScheduler.UnobservedTaskException` 统一记录。

### AH-12 【观测准确 · 中】RestoreStats FIFO 判定错位

- **位置**：`RestoreStats.cs:99-116`
- **问题**：`RecordRequest` 入队 (key,slot)，`OnPromptEval` 按**入队顺序**弹最旧条目判定；多并发请求时 llama-server 的 `print_timing` 输出顺序与请求入队顺序可能不一致（尤其同 slot 并发）→ prompt eval 归属错位 → 命中率虚高/虚低，告警失真。
- **证据**：`RestoreStats.cs:106-116`（TTL 60s 防过期错位，但无法防顺序错位）。
- **修复**：判定增加顺序一致性启发式（如最近入队条目窗口内匹配），或接受并文档化该误差范围。

### AH-13 【性能 · 中】KvCacheManager / RestoreStats 锁内磁盘写

- **位置**：`KvCacheManager.cs:301-305`（`RecordSave` 在 `_gate` 锁内 `SaveIndex()` 全量写索引）+ `RestoreStats.cs:199-217`（`DoSaveLocked` 在 `_gate` 锁内写盘）
- **问题**：每次 save/判定都在锁内全量写 JSON 文件，高并发下串行化 + 锁内磁盘 IO 阻塞其他读锁操作（`SavedTokens` / `Snapshot`）。
- **修复**：持久化移出锁（脏标记 + 节流/后台写），或仅在累计变更阈值后落盘。

### AH-14 【信息暴露 · 中】`/__status__` 无鉴权暴露运行时信息

- **位置**：`SmartScheduler.Http.cs:93-117`
- **问题**：`/__status__` 返回 `backend_port`、槽位绑定布局、`recent_logs`（最近 10 条日志）。本机任意进程/恶意网页可读取；配合 AH-15（--host 时后端暴露）可定位后端端口直接操作。
- **修复**：加简单鉴权（如配置 token 或仅允许本机环回 + 无 referer 校验）；至少考虑隐藏 `backend_port` 或脱敏日志。

### AH-15 【安全 · 中】`--host` 时后端无鉴权暴露到局域网

- **位置**：`SmartScheduler.Lifecycle.cs:133-135`
- **问题**：检测到 `ExtraArgs` 含 `--host` 仅日志警告并放行 → llama-server 监听 0.0.0.0，其 `/v1/chat/completions`、`/slots/{id}?action=save|restore|erase`、`/v1/tokenize` **全部无鉴权** → 局域网内任意主机可读写 KV 快照（泄露对话上下文）、消耗 GPU、清空缓存。
- **修复**：`--host` 时强制阻断（或要求显式确认 + 启动后校验监听地址），或在说明书明确该风险并建议反向代理鉴权层。

---

## 4. 低严重度发现（6 项）

| 编号 | 位置 | 问题 | 建议 |
| --- | --- | --- | --- |
| AH-16 | `LlamaFinder.cs:71` / `AffinityRuleMatcher.cs:32` | 命令行拼接注入面：`-m "{ModelPath}"` 路径含 `"` 可注入额外 llama-server 参数；`KeyTemplate.Replace("{value}", 请求头)` 把客户端可控头值拼入亲和 key（超长/特殊字符 → key 膨胀、KV 文件名超长） | 路径校验拒绝 `"`；头值加长度上限（如 256） |
| AH-17 | `SystemMetrics.cs:109-121` | nvidia-smi 多行输出时 `ReadLineAsync` 首行后 `WaitForExit(5s)` 超时但**未 Kill** → 进程残留/句柄泄漏（低概率） | 超时路径统一 Kill + WaitForExit |
| AH-18 | `SmartScheduler.Http.cs:148` | 唤醒期间请求无限 `await EnsureRunningAsync()`（模型加载可达数分钟），客户端断开后服务器仍继续等待才写失败 | 唤醒等待加可取消/超时 |
| AH-19 | `AppConfig.cs:164` | 旧版配置探测 `json.Contains("\"ExePath\"")` 脆弱（新配置若含该字符串误判走 Legacy 反序列化） | 改用 schema_version 判定 |
| AH-20 | `KvCacheManager.cs:318-367` / `SlotAffinity.cs:306-361` | 索引/绑定持久化无文件锁，多实例同时运行会互写损坏 | 文档化单实例约束或加锁 |
| AH-21 | `LogFile.cs:118-128` + `DumpRequest` | `request_dump.log` 明文写入完整 prompt（默认关闭 O-18，但开启后 prompt 明文落盘） | 保持默认关；开启时 UI 明确隐私提示 |

---

## 5. 已核实排除的疑似问题（4 项）

对抗审查中曾怀疑、经代码核实为**非问题**的项，避免后续误修：

| 疑似项 | 核实结论 | 证据 |
| --- | --- | --- |
| InFlightTracker 字典泄漏 | **排除**——`Unregister` 在 `finally` 中保证 | `SmartScheduler.Http.cs:161-166` |
| 调度器事件在非 UI 线程直调破坏线程安全 | **排除**——`LlamaStatsParser.Feed` 有锁、事件回调经 `invokeOnUi` 切回；`SlotPanelView.OnSlotLog` 同；`StatsReset` 有锁 | `LlamaStatsParser.cs:36/88`、`StatsPanelView.cs:107`、`SlotPanelView.cs:92` |
| llama-server stdout 背压死锁 | **排除**——`LogFile.Append → LogPipeline.Enqueue` 仅一次 lock 零阻塞（不等待 UI），UI 消费不阻塞生产 | `LogPipeline.cs:251-259`、`LogView.cs:39-46` |
| FlatButton 禁用色被 ApplyPhase 覆盖 | **排除**——`ApplyPhase` 设置 `ForeColor` 不影响禁用态自绘（禁用态用 `DisabledForeColor`） | `UiTheme.cs:120-138`、`StatusPanelView.cs:239-240` |

---

## 6. 修复优先级建议

| 优先级 | 项 | 理由 |
| --- | --- | --- |
| P0（立即） | AH-1 监控端口错位、AH-2 content 数组崩溃 | 功能级 bug，改造成本低（各 1~5 行） |
| P1（本迭代） | AH-3 后端假死超时、AH-4 异常分类、AH-6 WriteError 转义 | 健壮性/排障核心，直接影响线上可诊断性 |
| P1（本迭代） | AH-5 body 上限、AH-14/15 信息暴露 | 安全面，改动小 |
| P2（后续） | AH-7~AH-13 | 性能/一致性/观测，需结合重构窗口 |
| P3（择机） | AH-16~AH-21 | 低风险治理 |

---

## 7. 与既有审计基线的关系

- C-001~C-104（2026-08-29 审计）已修复项不在本报告重复；O-13~O-18 / E-1~E-10 优化已生效（休眠 save 60s 超时、tokenize 口径、aff 移出锁、DOM 管道、有界队列、锁外排队等）均经本次复核确认仍在。
- 本报告 21 项为**对抗视角新增发现**，其中 AH-3/AH-5/AH-8/AH-9/AH-12 属既有架构的**结构性取舍点**，修复需评估对「LLM 长生成」「多 Agent 并发」核心语义的影响，不宜简单一刀切。
