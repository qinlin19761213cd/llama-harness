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
    // B5：共享 HttpClient（SocketsHttpHandler 池化连接 + Timeout 无限）。
    // 测试注入的 handler 走独立实例（便于每个测试隔离 Mock 行为）；正常路径共享以复用连接池、避免 socket exhaustion。
    // 正常路径由 _ownsHttp=false 保证 Dispose 时不释放共享实例（连接池直到进程退出才回收，符合 .NET 最佳实践）。
    private static readonly Lazy<HttpClient> _sharedHttp = new(() => new HttpClient(new SocketsHttpHandler
    {
        // E-7：keep-alive + 池化连接寿命上限（对齐 SmartScheduler 原 _hc）——
        // 休眠/唤醒后残留的死连接由 PooledConnectionLifetime 自然过期淘汰，偶发死连接由调用方 500ms 重试兜底。
        PooledConnectionLifetime = TimeSpan.FromSeconds(30),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
    })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    });

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _baseUrl;
    /// <summary>M-11：诊断日志回调（tokenize 降级路径等）。由调用方注入以避免 Console.WriteLine 污染。</summary>
    public Action<string>? Log { get; set; }

    /// <param name="baseUrl">后端基地址，如 http://localhost:8081（尾斜杠自动去除）。</param>
    /// <param name="handler">测试注入用 HttpMessageHandler（Mock）；null 时用共享的 SocketsHttpHandler 连接池。</param>
    public LlamaServerClient(string baseUrl, HttpMessageHandler? handler = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        if (handler != null)
        {
            // 测试路径：独立 HttpClient，便于每次 Mock 隔离；Dispose 时释放
            _http = new HttpClient(handler);
            _ownsHttp = true;
        }
        else
        {
            // 生产路径：共享 HttpClient，连接池跨实例复用；Dispose 不释放共享实例
            _http = _sharedHttp.Value;
            _ownsHttp = false;
        }
    }

    // ── ① 推理（透明代理）──────────────────────────────────
    /// <summary>发送请求到后端（ResponseHeadersRead 流式直通）。
    /// P2 修复项 2：SSE 首字节超时——ResponseHeadersRead 意味着 Task 完成时机=首个响应头/字节到达；
    /// 若后端挂起（llama-server 未就绪/队列爆堵）会卡死整个请求链。用 Task.WhenAny 监视 60s 首字节看门狗：
    /// 超时后取消 linkedCts（sendTask 抛 OperationCanceled），向调用方抛 TimeoutException 走统一 503。</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct)
    {
        const int firstByteTimeoutSeconds = 60;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(firstByteTimeoutSeconds));

        var sendTask = _http.SendAsync(request, option, linkedCts.Token);
        var delayTask = Task.Delay(TimeSpan.FromSeconds(firstByteTimeoutSeconds), CancellationToken.None);
        var completed = await Task.WhenAny(sendTask, delayTask);
        if (completed == sendTask)
            return await sendTask;
        // 首字节超时：等待 sendTask 收敛（CancelAfter 触发后应抛 OperationCanceledException），吞掉异常后向调用方抛 TimeoutException
        try { await sendTask; } catch { /* 忽略取消异常 */ }
        throw new TimeoutException($"后端服务首字节超时（{firstByteTimeoutSeconds}s），请检查 llama-server 状态。");
    }

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
                    Log?.Invoke($"[TOKEN-GUARD-WARN] tokenize {endpoints[i]} → HTTP {(int)resp.StatusCode}，尝试下一路径");
                    continue;
                }
                var body = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("tokens", out var toks) && toks.ValueKind == JsonValueKind.Array)
                    return toks.GetArrayLength();
                if (root.TryGetProperty("n_tokens", out var n) && n.TryGetInt32(out var v))
                    return v;
                Log?.Invoke($"[TOKEN-GUARD-WARN] tokenize {endpoints[i]} 响应缺少 tokens/n_tokens 字段，尝试下一路径");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[TOKEN-GUARD-WARN] tokenize {endpoints[i]} 异常：{ex.Message}，尝试下一路径");
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
        // M-P2 修复：slot/action 参数用 Uri.EscapeDataString 显式转义（防 URI 拼接畸形）；
        // 允许字符合法性校验（slot 必须非负整数，action 仅允许 save/restore/erase）。
        if (slot < 0) throw new ArgumentOutOfRangeException(nameof(slot), slot, "slot 必须 >= 0");
        if (action != "save" && action != "restore" && action != "erase")
            throw new ArgumentException($"非法 action: {action}", nameof(action));
        HttpContent? content = null;
        if (filename != null)
        {
            content = new ByteArrayContent(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { filename })));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        var url = $"{_baseUrl}/slots/{slot}?action={Uri.EscapeDataString(action)}";
        return _http.PostAsync(new Uri(url), content, ct);
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

    // B5：共享 HttpClient 不由本实例释放（由 Lazy 持有直到进程退出）；仅测试注入的 handler 分支才需要 Dispose。
    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
