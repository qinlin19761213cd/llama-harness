using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
                // E-7：keep-alive + 池化连接寿命上限（对齐 SmartScheduler 原 _hc）——
                // 休眠/唤醒后残留的死连接由 PooledConnectionLifetime 自然过期淘汰，偶发死连接由调用方 500ms 重试兜底。
                PooledConnectionLifetime = TimeSpan.FromSeconds(30),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
            });
        _http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
    }

    // ── ① 推理（透明代理）──────────────────────────────────
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct)
        => _http.SendAsync(request, option, ct);

    // ── ② 非流式 chat/completions（计量/预热）───────────────
    public Task<HttpResponseMessage> ChatCompletionsAsync(string body, CancellationToken ct)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return _http.PostAsync(new Uri(_baseUrl + "/v1/chat/completions"), content, ct);
    }

    // ── ②' tokenize 计数（TokenGuard 计量）──────────────────
    public async Task<int?> TokenizeAsync(string text, CancellationToken ct)
    {
        string[] endpoints = { "/v1/tokenize", "/tokenize" }; // b10676+ 迁移：旧 /v1/tokenize 404 后回退 /tokenize
        for (int i = 0; i < endpoints.Length; i++)
        {
            try
            {
                var payload = new JsonObject { ["content"] = text };
                using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload.ToJsonString()));
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var resp = await _http.PostAsync(new Uri(_baseUrl + endpoints[i]), content, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[TOKEN-GUARD-WARN] tokenize {endpoints[i]} → HTTP {(int)resp.StatusCode}，尝试下一路径");
                    continue;
                }
                var body = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("tokens", out var toks) && toks.ValueKind == JsonValueKind.Array)
                    return toks.GetArrayLength();
                if (root.TryGetProperty("n_tokens", out var n) && n.TryGetInt32(out var v))
                    return v;
                Console.WriteLine($"[TOKEN-GUARD-WARN] tokenize {endpoints[i]} 响应缺少 tokens/n_tokens 字段，尝试下一路径");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TOKEN-GUARD-WARN] tokenize {endpoints[i]} 异常：{ex.Message}，尝试下一路径");
            }
        }
        return null; // 全部路径失败：降级
    }

    // ── ③ KV 槽位 ──────────────────────────────────────────
    public Task<HttpResponseMessage> SlotSaveAsync(int slot, string filename, CancellationToken ct)
        => SlotActionAsync(slot, "save", filename, ct);

    public Task<HttpResponseMessage> SlotRestoreAsync(int slot, string filename, CancellationToken ct)
        => SlotActionAsync(slot, "restore", filename, ct);

    public Task<HttpResponseMessage> SlotEraseAsync(int slot, CancellationToken ct)
        => SlotActionAsync(slot, "erase", null, ct);

    private Task<HttpResponseMessage> SlotActionAsync(int slot, string action, string? filename, CancellationToken ct)
    {
        HttpContent? content = null;
        if (filename != null)
        {
            content = new ByteArrayContent(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { filename })));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        return _http.PostAsync(new Uri($"{_baseUrl}/slots/{slot}?action={action}"), content, ct);
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
