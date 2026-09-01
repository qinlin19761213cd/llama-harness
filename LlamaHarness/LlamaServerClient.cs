using System.Net;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;

namespace LlamaHarness;

/// <summary>
/// llama-server 后端实现（IBackendClient）。统一持有 HttpClient，端点 URL 集中在 BaseUrl 一处。
/// - 推理透明代理：<see cref="SendAsync"/> 透传已构造好的 HttpRequestMessage（RequestUri 为完整 URL），ResponseHeadersRead 流式直通。
/// - KV/状态/探测便捷方法基于 BaseUrl 拼接内部固定端点。
/// - HttpClient：SocketsHttpHandler 连接池（PooledConnectionIdleTimeout=60s，对齐 SmartScheduler 原 _hc），Timeout 无限（超时由调用方 cts 控制）。
/// </summary>
public sealed class LlamaServerClient : IBackendClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    /// <param name="baseUrl">后端基地址，如 http://localhost:8081（尾斜杠自动去除）。</param>
    /// <param name="handler">测试注入用 HttpMessageHandler（Mock）；null 时用 SocketsHttpHandler 连接池。</param>
    public LlamaServerClient(string baseUrl, HttpMessageHandler? handler = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = handler != null
            ? new HttpClient(handler)
            : new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
            });
        _http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
    }

    // ── ① 推理（透明代理）──────────────────────────────────
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

    // ── ② 非流式 chat/completions（计量/预热）───────────────
    public Task<HttpResponseMessage> ChatCompletionsAsync(string body, CancellationToken ct)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return _http.PostAsync(new Uri(_baseUrl + "/v1/chat/completions"), content, ct);
    }

    // ── ③ KV 槽位 ──────────────────────────────────────────
    public Task<bool> SlotSaveAsync(int slot, string key, CancellationToken ct)
        => SlotActionAsync(slot, "save", key, ct);

    public Task<bool> SlotRestoreAsync(int slot, string key, CancellationToken ct)
        => SlotActionAsync(slot, "restore", key, ct);

    public Task<bool> SlotEraseAsync(int slot, CancellationToken ct)
        => SlotActionAsync(slot, "erase", null, ct);

    private async Task<bool> SlotActionAsync(int slot, string action, string? key, CancellationToken ct)
    {
        HttpContent? content = null;
        if (key != null)
        {
            content = new ByteArrayContent(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { key })));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        using var resp = await _http.PostAsync(new Uri($"{_baseUrl}/slots/{slot}?action={action}"), content, ct);
        return resp.IsSuccessStatusCode;
    }

    // ── ④ 状态探测 ─────────────────────────────────────────
    public Task<JsonDocument?> GetSlotsAsync(CancellationToken ct) => GetJsonAsync("/slots", ct);

    public Task<JsonDocument?> GetPropsAsync(CancellationToken ct) => GetJsonAsync("/props", ct);

    public async Task<string?> GetMetricsAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(new Uri(_baseUrl + "/metrics"), HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return null; // 后端不可用：判空降级
        }
    }

    // ── ⑤ 通用探测 ─────────────────────────────────────────
    public Task<HttpResponseMessage> ProbeAsync(string path, CancellationToken ct)
    {
        var p = path.StartsWith('/') ? path : "/" + path;
        return _http.GetAsync(new Uri(_baseUrl + p), HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(new Uri(_baseUrl + path), HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct);
            return string.IsNullOrWhiteSpace(body) ? null : JsonDocument.Parse(body);
        }
        catch
        {
            return null; // 后端不可用 / 解析失败：判空降级
        }
    }

    public void Dispose() => _http.Dispose();
}
