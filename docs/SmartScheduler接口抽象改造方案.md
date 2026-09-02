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

/// <summary>后端推理服务统一契约（llama-server / vLLM 等实现）。仅定义 HTTP 传输能力，不承载业务逻辑。</summary>
public interface IBackendClient : IDisposable
{
    // ── ① 推理（透明代理）────────────────────────────────────
    /// <summary>通用转发：request 已含完整 RequestUri/头/body，原样透传（推理路径是透明代理，端点不固定）。
    /// option 透传 HttpClient.SendAsync（流式用 ResponseHeadersRead 直通 SSE）。</summary>
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct);

    /// <summary>非流式 chat/completions（TokenGuard 计量/验证、Lifecycle dummy 预热）：POST /v1/chat/completions，返回原始响应。</summary>
    Task<HttpResponseMessage> ChatCompletionsAsync(string body, CancellationToken ct);

    /// <summary>tokenize 双路径（llama.cpp b10676 移除 /v1/tokenize → 自动回退 /tokenize）。返回 token 数；失败返回 null。</summary>
    Task<int?> TokenizeAsync(string text, CancellationToken ct);

    // ── ② KV 槽位 ───────────────────────────────────────────
    /// <summary>槽位 save/restore/erase：body 为 {"filename":"<文件名>.bin"}（快照按文件名管理），返回原始响应供 KvCacheManager 解析 n_saved/n_written。</summary>
    Task<HttpResponseMessage> SlotSaveAsync(int slot, string filename, CancellationToken ct);
    Task<HttpResponseMessage> SlotRestoreAsync(int slot, string filename, CancellationToken ct);
    Task<HttpResponseMessage> SlotEraseAsync(int slot, CancellationToken ct);

    // ── ③ 状态探测 ──────────────────────────────────────────
    Task<JsonDocument?> GetSlotsAsync(CancellationToken ct);
    Task<JsonDocument?> GetPropsAsync(CancellationToken ct);
    Task<string?> GetMetricsAsync(CancellationToken ct);

    // ── ④ 健康/通用 ─────────────────────────────────────────
    /// <summary>GET 任意后端路径（健康/就绪轮询 /v1/models 等），返回原始响应。</summary>
    Task<HttpResponseMessage> ProbeAsync(string path, CancellationToken ct);
}

**设计要点（实施后最终版）**：
- **推理路径是透明代理**（SmartScheduler 把客户端请求原样透传到后端任意路径，仅 chat/completions 做网关改写）——契约用 `SendAsync(HttpRequestMessage, option, ct)` 透传，
  option 透传 `HttpClient.SendAsync`（流式 ResponseHeadersRead 直通 SSE）。
- KV/状态返回可空类型（`JsonDocument?`/`string?`）——后端不可用时上层可判空降级，与现有 `LlamaCppMonitor` 的容错语义一致。
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
            : new HttpClient(new SocketsHttpHandler
            {
                // 对齐 SmartScheduler 原 _hc：池化连接寿命上限 30s + 空闲超时 60s，
                // 休眠/唤醒后残留死连接由 PooledConnectionLifetime 自然过期淘汰。
                PooledConnectionLifetime = TimeSpan.FromSeconds(30),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
            });
        _http.Timeout = Timeout.InfiniteTimeSpan;   // 超时由调用方 cts 控制（对齐原 _hc）
    }

    // SendAsync：透传 HttpRequestMessage（RequestUri 为完整 URL），option 透传（流式 ResponseHeadersRead 直通 SSE）
    // ChatCompletionsAsync(string)：POST {base}/v1/chat/completions，Content-Type json
    // TokenizeAsync：POST {base}/v1/tokenize → 404 回退 POST {base}/tokenize（llama.cpp b10676 移除旧路径）
    // SlotSave/Restore/Erase → POST {base}/slots/{id}?action=save|restore|erase，body={"filename":"<名>.bin"}
    // GetSlots/GetProps/GetMetrics → GET {base}/slots | /props | /metrics（非 2xx → null）
    // ProbeAsync → GET {base}{path}
}
```
    }

    // SendAsync：透传 HttpRequestMessage（RequestUri 为完整 URL），ResponseHeadersRead 流式直通
    // ChatCompletionsAsync(string)：POST {base}/v1/chat/completions，Content-Type json
    // SlotSave/Restore/Erase → POST {base}/slots/{id}?action=save|restore|erase
    // GetSlots/GetProps/GetMetrics → GET {base}/slots | /props | /metrics（非 2xx → null）
    // ProbeAsync → GET {base}{path}
}
```

**收敛说明（实施后最终版）**：
- ✅ `KvCacheManager`：构造改注入 `IBackendClient`，删自身 HttpClient/SlotUrl/backendPort；save/restore/erase 走 `SlotSave/SlotRestore/SlotEraseAsync`，响应体自解析 n_saved/n_written（业务保留）。
- ✅ `LlamaCppMonitorCollector`：删自身 HttpClient/BaseAddress/FetchAsync，改注入 `LlamaServerClient`（端口可变场景内部封装、调用方零改动）；8s 探测超时改链接 cts；Raw 字段用 `JsonDocument.RootElement.GetRawText()` 保留原始文本；DTO 解析（Slots/Props 折叠）保留。
- ✅ `TokenGuard`：`MeasureAsync`/`GuardAsync`/`CountTokensAsync` 签名改 `IBackendClient`，tokenize 双路径容错下沉 `TokenizeAsync`。
- ✅ `SmartScheduler`：`_hc` 字段删除，换懒加载 `Backend` 属性（走 `BackendClientFactory.Create`）；ForwardAsync/预热/自愈/重试全部经 `Backend`，**重试/BadRequest 自愈/SSE 管道逻辑全部保留在 Pipeline 不动**（行为等价迁移）。
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
| SlotSave/Restore/Erase | URL=`/slots/{id}?action=save\|restore\|erase`、body=`{"filename":"<名>.bin"}`（非 {key}）；返回 HttpResponseMessage（KvCacheManager 自解析 n_saved/n_written） |
| GetSlots/GetProps | GET 正确路径；404 → 返回 null（判空降级） |
| GetMetrics | GET /metrics，文本透传 |
| ProbeAsync | GET 任意路径，返回状态码 |
| TokenizeAsync | 旧路径 `/v1/tokenize` 计数；404 → 回退新路径 `/tokenize`；两次均失败 → 返回 null |
| 超时/后端不可用 | 抛 HttpRequestException 或返回 null，上层容错路径验证 |

**回归**：Step1 后 259→274 全绿；Step2/3 后 274→277 全绿（行为等价迁移）；如发现行为差异按 bug 修复，不静默调整测试。

---

## 9. 分步实施（每步独立 build + test + commit）

| 步骤 | 内容 | 验收 |
|---|---|---|
| **Step 1** ✅ | 新增 `IBackendClient` + `LlamaServerClient`，写 BackendClientTests（Mock 注入） | build 0 错 + 259→274 全绿（commit b69f2bb） |
| **Step 2** ✅ | SmartScheduler 系列 `_hc` → `_backend`：ForwardAsync / 预热唤醒 / TokenGuard 调用点替换 | build 0 错 + 274→277 全绿（commit 7af4427，含误入 react-demo 剔除） |
| **Step 3** ✅ | LlamaCppMonitorCollector 收敛到 IBackendClient（删自身 HttpClient/FetchAsync） | build 0 错 + 277 全绿（commit 60f2f46） |
| **Step 4** ✅ | 文档：架构设计说明书更新至 v2.26 + 本方案契约同步 + `BackendClientFactory` 骨架（SmartScheduler 懒加载走工厂） | 文档同步 + git 提交（commit e23a291 + docs） |

**本次不做**（边界）：State 模式改造、ISmartScheduler 整体抽象、vLLM 实际实现、UI/性能模块改动。

---

## 10. 风险与回滚

- **超时漂移**：收敛时逐项对照原 HttpClient 的 Timeout/请求头，测试覆盖。
- **行为回归**：全部为等价迁移，259 测试守住；若某步破坏行为，git 单步回滚（每步独立 commit）。
- **HttpClient 生命周期**：`LlamaServerClient` 由 SmartScheduler 创建并 Dispose（Lifecycle.cs L464 现有 `_hc.Dispose()` 位置不变），KvCacheManager/Monitor 只注入不持有所有权。
