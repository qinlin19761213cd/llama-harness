using System.Text.Json;

namespace LlamaHarness;

/// <summary>
/// 后端推理服务统一契约（llama-server / vLLM 等实现）。仅定义 HTTP 传输能力，不承载业务逻辑。
/// 契约按"后端能力"抽象，而非"llama-server 端点"——保证非 llama.cpp 后端（如 vLLM）可独立实现。
/// 推理路径是透明代理：<see cref="SendAsync"/> 透传原始 method/path/headers/body（流式直通）；
/// KV/状态/探测提供便捷方法（内部端点固定）。
/// </summary>
public interface IBackendClient : IDisposable
{
    /// <summary>
    /// 通用转发（推理透明代理）：request 已含完整 RequestUri/头/body，原样发送。
    /// 以 ResponseHeadersRead 发送（响应体不预读），返回原始响应供流式 SSE 直通。
    /// </summary>
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct);

    /// <summary>非流式 chat/completions：POST {base}/v1/chat/completions，返回原始响应（TokenGuard 计量/验证、Lifecycle dummy 预热用）。</summary>
    Task<HttpResponseMessage> ChatCompletionsAsync(string body, CancellationToken ct);

    /// <summary>槽位 KV 落盘：POST /slots/{slot}?action=save，key 入 body。成功返回 true。</summary>
    Task<bool> SlotSaveAsync(int slot, string key, CancellationToken ct);

    /// <summary>槽位 KV 恢复：POST /slots/{slot}?action=restore，key 入 body。成功返回 true。</summary>
    Task<bool> SlotRestoreAsync(int slot, string key, CancellationToken ct);

    /// <summary>槽位 KV 擦除：POST /slots/{slot}?action=erase，无 body。成功返回 true。</summary>
    Task<bool> SlotEraseAsync(int slot, CancellationToken ct);

    /// <summary>GET /slots 槽位状态。后端不可用/非 2xx 返回 null（上层判空降级）。</summary>
    Task<JsonDocument?> GetSlotsAsync(CancellationToken ct);

    /// <summary>GET /props 全局配置。后端不可用/非 2xx 返回 null。</summary>
    Task<JsonDocument?> GetPropsAsync(CancellationToken ct);

    /// <summary>GET /metrics Prometheus 文本。后端不可用/非 2xx 返回 null。</summary>
    Task<string?> GetMetricsAsync(CancellationToken ct);

    /// <summary>通用探测：GET {base}{path}（健康/就绪轮询 /v1/models 等），返回原始响应。</summary>
    Task<HttpResponseMessage> ProbeAsync(string path, CancellationToken ct);
}
