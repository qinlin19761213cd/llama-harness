# SmartScheduler 接口抽象改造方案（IBackendClient 窄版）

> 版本：v1.0（方案稿，待批准）
> 关联评审：SmartScheduler 超 2200 行建议 State 模式拆分 / 缺少接口抽象层（ISmartScheduler）
> 决策：**砍掉 State 模式；接口抽象做窄版（IBackendClient，而非 ISmartScheduler）**

---

## 1. 评审结论与决策依据

| 评审建议 | 决策 | 依据 |
|---|---|---|
| State 模式拆分 SmartScheduler | **不做** | 主类已按职责 partial 拆为 6 文件（主类仅 325 行/13 方法），复杂度在流程编排而非状态机；5 个 Phase 行为高度共享，State 模式徒增 6~8 类属过度设计 |
| 缺少接口抽象层（ISmartScheduler） | **做窄版** | 调度器是编排胶水层（依赖 HttpListener/进程/UI 事件），整体抽接口测试收益低；真正有扩展点的是**后端 HTTP 交互**——目前 3 处各自持有 HttpClient，抽 `IBackendClient` 一举解决"多后端可扩展 + HttpClient 唯一化" |

**本次改造的本质**：不是"加接口好看"，而是把散落在 SmartScheduler / KvCacheManager / LlamaCppMonitor / TokenGuard 四处的后端 HTTP 访问收敛到**一个契约 + 一个实现**，为将来接 vLLM / 远端后端留下真实的扩展点。

---

## 2. 现状盘点（改造依据）

### 2.1 端口拓扑

```
客户端 (dsh / webui / trae)  ──▶  SmartScheduler 网关  :8080  (HttpListener)
                                      │  智能唤醒 / 槽位亲和 / KV 恢复 / TokenGuard / 崩溃自愈
                                      ▼
                              llama-server 后端  :8081  (Port+1)
                                      │
                                      ├─ /v1/chat/completions   推理
                                      ├─ /slots/{id}?action=save|restore|erase   KV
                                      ├─ /slots  /props  /metrics   状态
                                      └─ /health / 任意探测       健康/预热
```

### 2.2 后端 HTTP 交互分散点（4 处）

| 交互点 | 位置 | 方法 | 端点 |
|---|---|---|---|
| ① 推理转发 | SmartScheduler.Pipeline.cs L94/L105/L158 | `_hc.SendAsync` | `/v1/chat/completions` |
| ② KV 生命周期 | KvCacheManager.cs（独立 HttpClient） | `Save/Restore/Erase` | `/slots/{id}?action=*` |
| ③ 状态探测 | LlamaCppMonitor.cs（独立 HttpClient） | `FetchAsync` | `/slots /props /metrics` |
| ④ 计量/预热 | TokenGuard.MeasureAsync / Lifecycle.cs L208/L289 | `_hc.Get/Post` | `/v1/chat/completions`、探测 |

**现状问题**：3 个 HttpClient 实例（SmartScheduler._hc / KvCacheManager / LlamaCppMonitor）、端点 URL 字符串散落、后端地址在 3 处各自拼接 `http://localhost:{backendPort}/...`。换后端时无从下手。

---

## 3. 设计目标与原则

1. **契约按"后端能力"抽象**，不按"llama-server 端点"——保证 vLLM 等后端可实现。
2. **流式推理保留原始流直通**——`ChatCompletionsAsync` 返回 `HttpResponseMessage`（ResponseHeadersRead），SmartScheduler 的 SSE 续接/工具隔离管道零改动。
3. **HttpClient 唯一化**——`IBackendClient` 是唯一持有 HttpClient 的入口，其余组件注入 `IBackendClient`。
4. **行为等价迁移**——本次改造不改变任何运行时行为，全部 259 测试保持全绿。
5. **不把业务逻辑塞进接口**——KV 轮询等待、快照解析、TokenGuard 计量等业务保留在各自组件，只把"HTTP 传输"收敛。

---

## 4. IBackendClient 契约设计

```csharp
/// <summary>后端推理服务统一契约（llama-server / vLLM 等实现）。仅定义 HTTP 传输能力，不承载业务逻辑。</summary>
public interface IBackendClient : IDisposable
{
    // ── ① 推理（透明代理）────────────────────────────────────
    /// <summary>通用转发：request 已含完整 RequestUri/头/body，原样透传（推理路径是透明代理，端点不固定）。
    /// ResponseHeadersRead 发送，返回原始响应供流式 SSE 直通。</summary>
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct);

    /// <summary>非流式 chat/completions（TokenGuard 计量/验证、Lifecycle dummy 预热）：POST /v1/chat/completions，返回原始响应。</summary>
    Task<HttpResponseMessage> ChatCompletionsAsync(string body, CancellationToken ct);

    // ── ② KV 槽位 ───────────────────────────────────────────
    Task<bool> SlotSaveAsync(int slot, string key, CancellationToken ct);
    Task<bool> SlotRestoreAsync(int slot, string key, CancellationToken ct);
    Task<bool> SlotEraseAsync(int slot, CancellationToken ct);

    // ── ③ 状态探测 ──────────────────────────────────────────
    Task<JsonDocument?> GetSlotsAsync(CancellationToken ct);
    Task<JsonDocument?> GetPropsAsync(CancellationToken ct);
    Task<string?> GetMetricsAsync(CancellationToken ct);

    // ── ④ 健康/通用 ─────────────────────────────────────────
    /// <summary>GET 任意后端路径（健康/就绪轮询 /v1/models 等），返回原始响应。</summary>
    Task<HttpResponseMessage> ProbeAsync(string path, CancellationToken ct);
}
```

**设计要点**：
- **推理路径是透明代理**（SmartScheduler 把客户端请求原样透传到后端任意路径，仅 chat/completions 做网关改写）——契约用 `SendAsync(HttpRequestMessage)` 透传，而非固定端点方法；响应以 ResponseHeadersRead 返回，SSE 续接/工具隔离管道零改动。
- KV/状态返回可空类型（`JsonDocument?`/`string?`）——后端不可用时上层可判空降级，与现有 `LlamaCppMonitor` 的容错语义一致。
- `ChatCompletionsAsync(string)` 覆盖 TokenGuard 计量/验证与 Lifecycle dummy 预热（非流式 POST + 读状态码）。
- `ProbeAsync` 提供通用探测通道（健康检查、Warming 就绪轮询 /v1/models），覆盖 Lifecycle.cs 的裸 `_hc.GetAsync(url)`。

---

## 5. 实现：LlamaServerClient

```csharp
/// <summary>llama-server 后端实现：统一 HttpClient，端点 URL 集中在 BaseUrl 一处。</summary>
public sealed class LlamaServerClient : IBackendClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;   // http://localhost:8081

    public LlamaServerClient(string baseUrl, HttpMessageHandler? handler = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = handler != null ? new HttpClient(handler)
            : new HttpClient(new SocketsHttpHandler { PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60) });
        _http.Timeout = Timeout.InfiniteTimeSpan;   // 超时由调用方 cts 控制（对齐 SmartScheduler 原 _hc）
    }

    // SendAsync：透传 HttpRequestMessage（RequestUri 为完整 URL），ResponseHeadersRead 流式直通
    // ChatCompletionsAsync(string)：POST {base}/v1/chat/completions，Content-Type json
    // SlotSave/Restore/Erase → POST {base}/slots/{id}?action=save|restore|erase
    // GetSlots/GetProps/GetMetrics → GET {base}/slots | /props | /metrics（非 2xx → null）
    // ProbeAsync → GET {base}{path}
}
```

**收敛说明**：
- `KvCacheManager` 现有 `SlotUrl(slot, action)` 的 URL 拼接逻辑移入 `LlamaServerClient`，`KvCacheManager` 改为注入 `IBackendClient`（其"save 后轮询 /slots 确认"等业务保留）。
- `LlamaCppMonitor` 的 `FetchAsync("/slots|/props|/metrics")` 改为注入 `IBackendClient`，DTO 解析（SlotsSnapshot/PropsSnapshot）保留在 Monitor。
- `TokenGuard.MeasureAsync` 的 `_hc` 参数改为 `IBackendClient`（或复用其 HttpClient）。
- `SmartScheduler` 的 `_hc` 字段替换为 `IBackendClient _backend`，`ForwardAsync` 中 `_hc.SendAsync(msg)` 改为 `_backend.SendAsync(msg)`，**重试/BadRequest 自愈/SSE 管道逻辑全部保留在 Pipeline 不动**。

---

## 6. 多后端扩展（设计验证点）

`IBackendClient` 的契约是"后端能力"而非"llama-server 端点"，因此可新增实现：

```csharp
/// <summary>vLLM 后端（OpenAI 兼容 /v1/chat/completions，KV 能力视后端而定）。</summary>
public sealed class VllmClient : IBackendClient
{
    // ChatCompletionsAsync → POST {base}/v1/chat/completions（OpenAI 格式）
    // SlotSave/Restore/Erase → 不支持则抛 NotImplementedException（上层判功能开关降级）
    // GetSlots/GetProps → 不支持则返回 null（监控卡显示"不可用"）
    // GetMetrics → 若有 /metrics 则透传
}
```

接入方式（未来，不在本次范围）：`AppConfig` 增 `BackendType: llama|vllm`，工厂 `BackendClientFactory.Create(cfg)` 按类型返回，SmartScheduler/KvCacheManager/LlamaCppMonitor 均只依赖 `IBackendClient`，无需感知具体后端。

---

## 7. 影响面清单

| 文件 | 改动 | 量级 |
|---|---|---|
| **IBackendClient.cs**（新增） | 接口定义 | ~60 行 |
| **LlamaServerClient.cs**（新增） | 实现 + URL 集中 | ~150 行 |
| SmartScheduler.cs / Pipeline.cs / Lifecycle.cs / Gateway.cs | `_hc` → `_backend`（IBackendClient），ForwardAsync/预热/TokenGuard 调用点替换 | 每处 1~3 行，行为零变化 |
| KvCacheManager.cs | 注入 IBackendClient，删自身 HttpClient 与 SlotUrl | 收敛 |
| LlamaCppMonitor.cs | 注入 IBackendClient，删自身 HttpClient | 收敛 |
| TokenGuard.cs | MeasureAsync 参数 `HttpClient` → `IBackendClient` | 签名 |
| AppConfig / LlamaFinder | 无改动（端口/路径配置不变） | — |
| MonitorPanelView / Perf* | 无改动 | — |

**风险**：低。纯收敛重构，无行为变化；唯一注意点是 `LlamaServerClient` 的 HttpClient 超时/请求头需与原实现逐项对齐（对照 KvCacheManager/Monitor/原 _hc 的默认值），防止超时行为漂移。

---

## 8. 测试计划

新增 `BackendClientTests.cs`（用 `MockHttpMessageHandler` 注入，无需真实 llama-server）：

| 用例 | 断言 |
|---|---|
| ChatCompletions 流式 | URL=`/v1/chat/completions`、方法=POST、body 原样、Accept 头含 `text/event-stream` |
| ChatCompletions 非流式 | Accept 不含 event-stream |
| SlotSave/Restore/Erase | URL=`/slots/{id}?action=save\|restore\|erase`、key 入 body |
| GetSlots/GetProps | GET 正确路径；404 → 返回 null（判空降级） |
| GetMetrics | GET /metrics，文本透传 |
| ProbeAsync | GET 任意路径，返回状态码 |
| 超时/后端不可用 | 抛 HttpRequestException 或返回 null，上层容错路径验证 |

**回归**：现有 259 测试全绿（行为等价迁移）；如发现行为差异按 bug 修复，不静默调整测试。

---

## 9. 分步实施（每步独立 build + test + commit）

| 步骤 | 内容 | 验收 |
|---|---|---|
| **Step 1** | 新增 `IBackendClient` + `LlamaServerClient`，写 BackendClientTests（Mock 注入） | build 0 错 + 新测试绿 + 259 回归全绿 |
| **Step 2** | SmartScheduler 系列 `_hc` → `_backend`：ForwardAsync / 预热唤醒 / TokenGuard 调用点替换 | build 0 错 + 回归全绿（行为零变化） |
| **Step 3** | KvCacheManager + LlamaCppMonitor 收敛到 IBackendClient（删各自 HttpClient） | build 0 错 + 回归全绿 |
| **Step 4** | 文档：架构设计说明书更新至新版本 + 本方案归档；可选：补 `BackendClientFactory` 骨架（不含 vLLM 实现） | 文档同步 + git 提交 |

**本次不做**（边界）：State 模式改造、ISmartScheduler 整体抽象、vLLM 实际实现、UI/性能模块改动。

---

## 10. 风险与回滚

- **超时漂移**：收敛时逐项对照原 HttpClient 的 Timeout/请求头，测试覆盖。
- **行为回归**：全部为等价迁移，259 测试守住；若某步破坏行为，git 单步回滚（每步独立 commit）。
- **HttpClient 生命周期**：`LlamaServerClient` 由 SmartScheduler 创建并 Dispose（Lifecycle.cs L464 现有 `_hc.Dispose()` 位置不变），KvCacheManager/Monitor 只注入不持有所有权。
