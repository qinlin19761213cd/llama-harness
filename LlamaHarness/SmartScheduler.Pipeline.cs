using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ThinkingModeHelper = LlamaHarness.ThinkingMode;

namespace LlamaHarness;

/// <summary>
/// 请求转发与响应管道（ForwardAsync/SendAndPipeAsync 及其子流程/PipeResponseAsync/DumpRequest）。partial 聚类方法体零改动。
/// </summary>
public partial class SmartScheduler
{
    private const int ReconnectDelayMs = 500;
    /// <summary>AH-5：请求体大小上限（本机恶意大 body 内存 DoS 防护）。64MB 远大于 LLM 请求体实际量级（256K 上下文 JSON 约数 MB）。</summary>
    private const int MaxRequestBodyBytes = 64 * 1024 * 1024;
    /// <summary>AH-21：请求 dump 隐私提示一次性标记。</summary>
    private int _dumpPrivacyWarned;
    /// <summary>把请求原样转发到后端；ResponseHeadersRead + CopyToAsync 保证 SSE/流式响应直通。
    /// 审计 O-8：按管道阶段拆分为 读体 → 网关预处理 → 转发管道 → 完成清理 四段，本方法仅做编排。</summary>
    private async Task ForwardAsync(HttpListenerContext ctx, string? rtId) // v2.21：性能埋点 id（仅推理请求非空）
    {
        var req = ctx.Request;
        if (_stopRequested)                       // 修复9：停止后拒绝转发，避免新请求挂到已被关断的后端
        {
            WriteErrorV2(ctx, 503, "SERVICE_UNAVAILABLE", "调度器正在停止，请重试。");
            return;
        }
        // M-06 修复：_backendPort==0（尚未唤醒）时构造的 URI 端口为 0，导致异常路径含端口 0 字符串；显式拒绝
        if (_backendPort <= 0)
        {
            Log?.Invoke("ForwardAsync 拒绝：后端尚未就绪（_backendPort=0），请检查唤醒流程。");
            WriteErrorV2(ctx, 503, "BACKEND_NOT_READY", "后端服务尚未就绪。");
            return;
        }
        // M-C3 修复：SSRF 防护——校验 RawUrl 必须是纯路径（以 `/` 开头，禁止 `//` 协议相对 URL），
        // 并对解析结果做二次防御（host 必须是 localhost / 127.0.0.1）。
        string rawUrl = req.RawUrl ?? "/";
        if (rawUrl.Length == 0 || rawUrl[0] != '/')
        {
            WriteErrorV2(ctx, 400, "INVALID_REQUEST", "请求路径必须以 / 开头。");
            return;
        }
        if (rawUrl.Length >= 2 && rawUrl[1] == '/')
        {
            WriteErrorV2(ctx, 400, "INVALID_REQUEST", "禁止协议相对 URL（可能指向外网主机）。");
            return;
        }
        Uri uri;
        try
        {
            uri = new Uri($"http://localhost:{_backendPort}{rawUrl}");
        }
        catch (UriFormatException)
        {
            WriteErrorV2(ctx, 400, "INVALID_REQUEST", "请求路径格式非法。");
            return;
        }
        // 二次防御：解析后的 URI host 必须是 localhost（防 URI 解析器异常时误指向外部）
        if (!string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            && uri.Host != "127.0.0.1")
        {
            WriteErrorV2(ctx, 400, "INVALID_REQUEST", "请求目标必须是本机后端。");
            return;
        }
        string path = req.Url?.AbsolutePath ?? "";

        // ① 读取完整请求体（非流式检测 / 强制流式改写需要）；GET 无请求体
        // AH-5：请求体大小上限（本机恶意大 body 内存 DoS 防护）；超限 413
        // L-05：使用 BackendRequestTimeoutSeconds 超时令牌，防 Slowloris 攻击导致线程永久阻塞
        byte[]? bodyBytes;
        try
        {
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(_cfg.BackendRequestTimeoutSeconds));
            bodyBytes = await RequestProcessor.ReadRequestBodyAsync(req, MaxRequestBodyBytes, readCts.Token);
        }
        catch (InvalidDataException ex)
        {
            // M-03 规范：不透传 ex.Message（可能含本机信息），错误码统一 REQUEST_BODY_TOO_LARGE
            Log?.Invoke($"[WARN] 请求体超上限被拒（Pipeline 兜底 64MB）：{ex.Message}");
            WriteErrorV2(ctx, 413, "REQUEST_BODY_TOO_LARGE", $"请求体超过 {MaxRequestBodyBytes / (1024 * 1024)} MB 上限。");
            return;
        }

        // 请求体 dump（应用识别分析用）：每个 POST 请求的原始 body + headers 落盘；O-18：默认关闭，配置开启才生效（防 prompt 隐私落盘与无谓 IO）
        if (bodyBytes != null && bodyBytes.Length > 0 && _cfg.RequestDumpEnabled)
            DumpRequest(ctx, bodyBytes);

        // ② 网关预处理：思考模式拦截 / 槽位亲和与 KV restore / TokenGuard / 强制流式 / 前缀哈希
        string? finalBody = null;   // 最终请求体（网关改写后），供输出续接构造下一轮
        bool effStreaming = false;  // 有效流式（含 ForceStream 改写）
        int? routedSlot = null;     // 本次请求亲和路由的槽位号（崩溃恢复快照接续用）
        string? routedKey = null;   // 本次请求亲和路由的绑定 key（KV 快照文件名）
        JsonObject? root = null;    // 解析后的 DOM（400 自愈分支需原地裁剪重发）
        if (bodyBytes != null && bodyBytes.Length > 0)
        {
            var prepared = await PrepareGatewayAsync(ctx, req, path, bodyBytes);
            if (prepared == null) return; // TokenGuard 拒绝：响应已写出
            (bodyBytes, finalBody, effStreaming, routedSlot, routedKey, root) = prepared.Value;
        }

        // ③ 转发后端 + 响应管道 + 完成清理
        await SendAndPipeAsync(ctx, uri, path, req, bodyBytes, finalBody, effStreaming, routedSlot, routedKey, root, rtId);
    }

    /// <summary>转发阶段：构造后端请求（过滤逐跳头）→ 连接异常 500ms 重试一次 → 400 上下文超限自愈 → 响应管道（崩溃恢复/断点快照清理/客户端断开兜底）。</summary>
    private async Task SendAndPipeAsync(
        HttpListenerContext ctx, Uri uri, string path, HttpListenerRequest req,
        byte[]? bodyBytes, string? finalBody, bool effStreaming, int? routedSlot, string? routedKey, JsonObject? root, string? rtId) // v2.21：性能埋点
    {
        if (rtId != null) _timing.MarkSent(rtId); // v2.21 打点：网关预处理完成（读体+路由/裁剪/流式改写），即将发向后端

        // 连接异常重试需重建请求（HttpRequestMessage 发送后 Content 流即被消费，不能复用同一 msg）——传入工厂，重试时重新构造
        HttpResponseMessage resp = await TryConnectWithRetryAsync(() => RequestProcessor.BuildBackendRequest(req, uri, bodyBytes));
        using (resp)
        {
            var outResp = ctx.Response;

            // 400 上下文超限自愈（激进裁剪 + KV 废弃 + 重发）；已处理则返回
            // B-04 修复：400 自愈路径也需打点 timing.Complete，避免 rtId 泄漏导致统计偏差
            if (await TryRecoverContextOverflowAsync(ctx, resp, outResp, req, uri, path, root, finalBody, effStreaming, routedSlot, routedKey))
            {
                if (rtId != null) _timing.Complete(rtId, success: false);
                return;
            }

            // 响应管道 + 崩溃恢复 + 断点快照清理 + 存档（含客户端断开兜底）
            await PumpResponseAsync(resp, outResp, uri, path, finalBody, effStreaming, routedSlot, routedKey);
        }
    }

    /// <summary>连接异常 500ms 重试一次：后端刚重启/连接被重置时稍等重发（SendAndPipeAsync 子流程①）。
    /// 注意：HttpRequestMessage 发送后其 Content 流即被消费，重试必须经工厂重建请求（复用 bodyBytes 字节，
    /// 字节流可重复读取）——直接复用同一 msg 会抛 InvalidOperationException "The request message was already sent..."
    /// （实测连接异常重试路径曾因此二次失败）。</summary>
    private async Task<HttpResponseMessage> TryConnectWithRetryAsync(Func<HttpRequestMessage> build)
    {
        HttpResponseMessage resp;
        try
        {
            using var msg = build();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_cfg.BackendRequestTimeoutSeconds)); // AH-22：转发等响应头超时兜底（单槽排队/调度异常不无限挂起，默认 300s 可配）
            resp = await Backend.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Net.Sockets.SocketException)
        {
            // 连接层瞬时失败（后端刚重启 / 连接被重置 / 连接不存在）：稍等后重试一次（重建请求，Content 字节流可重读）。
            // v2.23.6：catch 范围从 HttpRequestException 扩到 IOException/SocketException——实测某些连接失败
            // （如 WSAENOTCONN "企图在不存在的网络连接上进行操作"）被 HttpClient 直抛而非包装成 HttpRequestException，
            // 之前因此漏掉重试机会直接失败。
            Log?.Invoke("转发连接异常，正在重试…");
            await Task.Delay(ReconnectDelayMs);
            using var retryMsg = build();
            using var retryCts = new CancellationTokenSource(TimeSpan.FromSeconds(_cfg.BackendRequestTimeoutSeconds));
            resp = await Backend.SendAsync(retryMsg, HttpCompletionOption.ResponseHeadersRead, retryCts.Token);
        }
        return resp;
    }

    /// <summary>400 上下文超限自愈（SendAndPipeAsync 子流程②）：读取 errBody → TokenGuard 激进裁剪 → KV 废弃 → 重发。
    /// 前置 TokenGuard 是快速预估（BuildMessagesText 不含 tools/Jinja 模板），ReservedPromptOverhead 预留不足时仍可能击穿；
    /// 此分支是最后一道防线。返回 true = 已处理（调用方应 return）；false = 未触发自愈（继续正常流程）。</summary>
    private async Task<bool> TryRecoverContextOverflowAsync(
        HttpListenerContext ctx, HttpResponseMessage resp, HttpListenerResponse outResp, HttpListenerRequest req, Uri uri,
        string path, JsonObject? root, string? finalBody, bool effStreaming, int? routedSlot, string? routedKey)
    {
        if (resp.StatusCode != System.Net.HttpStatusCode.BadRequest || !RequestProcessor.IsChatCompletions(path) || root == null || finalBody == null)
            return false;
        // 上面 guard 已保证：BadRequest && IsChatCompletions && root/finalBody 非空，直接进入自愈
        {
            string errBody = "";
            try { errBody = await resp.Content.ReadAsStringAsync(); }
            catch (Exception ex) { Log?.Invoke($"[WARN] 400 自愈分支：读取后端错误体失败，按非超限处理（{ex.GetType().Name}: {ex.Message}）"); }
            if (errBody.Contains("exceeds the available context size", StringComparison.OrdinalIgnoreCase))
            {
                Log?.Invoke("[EDGE-CASE-CONTEXT-OVERFLOW-400] llama.cpp 上下文超限 400，触发自愈（aggressive trim + KV 废弃 + 重发）");
                Log?.Invoke("[TOKEN-GUARD-FATAL] real prompt overflow，aggressive trim + KV 废弃 + 重发");
                // 1. 激进裁剪：预算收紧 50%（比正常预算更严格）
                int tightBudget = Math.Max(AppConfig.MinInputBudgetTokens, _cfg.GetInputBudget() / 2);
                // 修复：tokenize 失败禁止 fail-open 穿透（否则重发未裁剪 body 死循环 400），退化为字符级保守估算继续裁剪
                var (ok, modified, note) = await TokenGuard.GuardAsync(root, Backend, tightBudget, failOpenOnTokenizeError: false);
                if (!ok)
                {
                    // 裁剪失败：原样返回 400
                    outResp.StatusCode = 400;
                    outResp.ContentType = "application/json";
                    var bytes = System.Text.Encoding.UTF8.GetBytes(errBody);
                    outResp.ContentLength64 = bytes.Length;
                    await outResp.OutputStream.WriteAsync(bytes);
                    return true;
                }
                // 2. 废弃 slot KV 缓存（旧 saved_n 残留与新裁剪后 prompt 不匹配，强制全量 prefill）
                if (routedKey != null && _kvCache != null)
                {
                    try
                    {
                        _kvCache.DeleteCache(routedKey);
                        lock (_kvStateGate) _prefixHashes.Remove(routedKey);
                        Log?.Invoke($"[TOKEN-GUARD-FATAL] KV 缓存废弃：{routedKey}（强制全量 prefill）");
                    }
                    catch (Exception ex)
                    {
                        // P3-D：KV 废弃失败不影响重发主流程，但需可观测输出（否则线上难排查 KV 残留）
                        Log?.Invoke($"[WARN] 400 自愈分支：KV 缓存废弃失败（{routedKey}）：{ex.GetType().Name} {ex.Message}（不阻塞重发）");
                    }
                }
                // 3. 重新提交请求（用裁剪后的 root 序列化）
                string newBody = modified ? root.ToJsonString() : finalBody;
                var newMsg = RequestProcessor.BuildBackendRequest(req, uri, System.Text.Encoding.UTF8.GetBytes(newBody));
                HttpResponseMessage retryResp;
                try
                {
                    // M-05 修复：使用超时令牌替代 CancellationToken.None，防止重发请求无限挂起
                    using var retryCts = new CancellationTokenSource(TimeSpan.FromSeconds(_cfg.BackendRequestTimeoutSeconds));
                    retryResp = await Backend.SendAsync(newMsg, HttpCompletionOption.ResponseHeadersRead, retryCts.Token);
                }
                catch (HttpRequestException)
                {
                    Log?.Invoke("[WARN] 400 自愈重发连接失败（连接层异常，走统一 V2 错误响应）");
                    WriteErrorV2(ctx, 502, "SELF_HEAL_RECONNECT_FAILED", "400 自愈重发连接失败。");
                    return true;
                }
                using (retryResp)
                {
                    outResp.StatusCode = (int)retryResp.StatusCode;
                    var ct2 = retryResp.Content.Headers.ContentType?.ToString();
                    outResp.ContentType = string.IsNullOrEmpty(ct2) ? "application/octet-stream" : ct2!;
                    if (retryResp.IsSuccessStatusCode)
                    {
                        // 重发成功：走正常响应管道
                        (bool completed, string accumulated) = await PipeResponseAsync(
                            retryResp, outResp, uri, path, newBody, effStreaming, routedSlot, routedKey);
                        Log?.Invoke($"[TOKEN-GUARD-FATAL] 400 自愈重发{(completed ? "成功" : "失败")}");
                        return true;
                    }
                    else
                    {
                        // 重发仍失败：返回错误
                        string retryErr = "";
                        try { retryErr = await retryResp.Content.ReadAsStringAsync(); }
                        catch (Exception ex) { Log?.Invoke($"[WARN] 400 自愈重发：读取重发响应错误体失败，回传空 body（{(int)retryResp.StatusCode}，{ex.GetType().Name}: {ex.Message}）"); }
                        outResp.ContentType = "application/json";
                        var bytes2 = System.Text.Encoding.UTF8.GetBytes(retryErr);
                        outResp.ContentLength64 = bytes2.Length;
                        await outResp.OutputStream.WriteAsync(bytes2);
                        Log?.Invoke($"[TOKEN-GUARD-FATAL] 400 自愈重发仍失败（{(int)retryResp.StatusCode}）");
                        return true;
                    }
                }
            }
        }
        return false;
    }

    /// <summary>响应管道编排（SendAndPipeAsync 子流程③）：设置响应头 → PipeResponseAsync（输出续接/崩溃识别）
    /// → 崩溃恢复（keep-alive 保活 + KV 快照接续/全量重放）→ 续接成功清理过期断点快照
    /// → 首请求存档 + 每轮条件式后台 save；含客户端断开兜底（catch）与响应关闭（finally）。</summary>
    private async Task PumpResponseAsync(
        HttpResponseMessage resp, HttpListenerResponse outResp, Uri uri, string path,
        string? finalBody, bool effStreaming, int? routedSlot, string? routedKey)
    {
        outResp.StatusCode = (int)resp.StatusCode;
        var ct = resp.Content.Headers.ContentType?.ToString();
        outResp.ContentType = string.IsNullOrEmpty(ct) ? "application/octet-stream" : ct!;
        try
        {
            (bool completed, string accumulated) = await PipeResponseAsync(
                resp, outResp, uri, path, finalBody, effStreaming, routedSlot, routedKey);

            // 崩溃恢复：流中断/5xx bad_alloc → keep-alive 保活 + KV 快照接续 / 进程重启全量重放
            if (!completed && _cfg.CrashRecoveryEnabled && effStreaming && finalBody != null)
            {
                var log2 = (string s) => Log?.Invoke(s);
                await TryCrashRecoverAsync(uri, outResp, finalBody, accumulated, routedSlot, routedKey, log2);
            }

            // §6.3：续接成功 → 清理过期断点快照（槽活 KV 已领先断点，旧快照 restore 会回退状态）；失败则保留供下次 rebinding/崩溃恢复 restore
            if (completed && routedKey != null)
            {
                bool wasPending;
                lock (_kvStateGate) wasPending = _truncPending.Remove(routedKey);
                if (wasPending)
                {
                    try
                    {
                        _kvCache?.DeleteCache(routedKey);
                        Log?.Invoke($"[KV-CLEANUP] 续接成功，清理过期断点快照：{routedKey}");
                    }
                    catch (Exception ex)
                    {
                        // P3-D：清理失败不影响主流程，但需可观测输出（残留断点快照会影响下次 restore）
                        Log?.Invoke($"[WARN] KV-CLEANUP 清理失败（{routedKey}）：{ex.GetType().Name} {ex.Message}（不阻塞主流程）");
                    }
                }
            }

            // 1.1 首请求存档：自动快照 key 首次真实 prefill 完成后立即落盘快照（每唤醒周期一次），
            // 防进程崩溃未休眠时磁盘快照停留在旧状态（缺最新 KV）。失败不阻塞主流程，下请求重试。
            if (completed && routedKey != null && _kvCache != null && routedSlot is int saveSlot
                && IsAutoSnapshotKey(routedKey))
            {
                bool alreadySaved;
                lock (_kvStateGate) alreadySaved = _savedKeysThisRun.Contains(routedKey);
                if (!alreadySaved)
                {
                    var swSave = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        await _kvCache.SaveAsync(saveSlot, routedKey);
                        lock (_kvStateGate) { _savedKeysThisRun.Add(routedKey); _freshSnapshotKeys.Add(routedKey); }
                        Log?.Invoke($"[KV-SAVE] 首请求存档：{routedKey} → slot{saveSlot}（{swSave.Elapsed.TotalSeconds:F1}s）");
                    }
                    catch (Exception ex)
                    {
                        Log?.Invoke($"[EDGE-CASE-SAVE-FAILED] {routedKey}：首请求存档失败（{ex.Message}），废弃旧快照，下次请求重试。");
                        _kvCache.DeleteCache(routedKey);
                    }
                }
            }

            // 1.2 每轮条件式后台 save（RAMDisk 快照全权接管）：快照非新鲜（上一轮后 KV 有增量）→ 异步后台 save，
            // 不阻塞响应返回（零额外延迟）；成功 → 标记新鲜；失败 → [EDGE-CASE-SAVE-FAILED] + 废弃快照（下轮自动重试）。
            // 并发安全：KvCacheManager._inflightSaves 按 key 去重，与驱逐前/休眠前同步 save 共享在途任务。
            if (completed && routedKey != null && _kvCache != null && routedSlot is int bgSaveSlot
                && IsAutoSnapshotKey(routedKey))
            {
                bool fresh;
                lock (_kvStateGate) fresh = _freshSnapshotKeys.Contains(routedKey);
                if (!fresh)
                {
                    var bgKey = routedKey;
                    var bgSlot = bgSaveSlot;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _kvCache.SaveAsync(bgSlot, bgKey);
                            lock (_kvStateGate) _freshSnapshotKeys.Add(bgKey);
                            Log?.Invoke($"[KV-SAVE] 每轮后台快照：{bgKey} → slot{bgSlot}");
                        }
                        catch (Exception ex)
                        {
                            Log?.Invoke($"[EDGE-CASE-SAVE-FAILED] {bgKey}：每轮后台快照失败（{ex.Message}），废弃旧快照。");
                            _kvCache.DeleteCache(bgKey);
                        }
                    });
                }
                // 本轮请求已完成并产生新的 KV（prompt 增量 + 生成输出），磁盘快照已滞后于 RAM。
                // 清除 fresh 使下一轮条件式 save 重新触发，保证磁盘快照持续覆盖最新 KV
                // （c93e0e0 缺陷补齐：原 fresh 只增不减，首存档后每轮后台 save 永不触发）。
                lock (_kvStateGate) _freshSnapshotKeys.Remove(routedKey);
            }
        }
        catch (IOException ex)
        {
            // 客户端断开/写入失败：方法退出时 dispose resp 关闭后端连接，
            // llama-server 检测到断开会取消任务并保留部分槽位 KV（f_keep），释放 GPU。
            // 多 agent 模式下这是预期行为（agent 超时/重试），非致命错误。
            Log?.Invoke($"客户端断开，已中止本次生成（{ex.Message}；多 agent 下属正常重试）。");
        }
        catch (Exception ex)
        {
            // AH-4：非 IO 异常（后端流异常/解析异常等）不能再笼统归为"客户端断开"——记录异常类型与消息，便于定位真因
            Log?.Invoke($"错误：响应管道异常（{ex.GetType().Name}: {ex.Message}），已中止本次生成并关闭后端连接。");
        }
        finally
        {
            outResp.Close();
        }
    }

    /// <summary>响应管道：chat/completions 走输出续接 + 崩溃识别（截断断点快照闭包 / 5xx bad_alloc 判定），其余透传。</summary>
    /// 返回 (是否完整完成, 已累积输出文本)。</summary>
    private async Task<(bool Completed, string Accumulated)> PipeResponseAsync(
        HttpResponseMessage resp, HttpListenerResponse outResp, Uri uri, string path,
        string? finalBody, bool effStreaming, int? routedSlot, string? routedKey)
    {
        if (!(RequestProcessor.IsChatCompletions(path) && finalBody != null))
        {
            await resp.Content.CopyToAsync(outResp.OutputStream);
            return (true, "");
        }

        // 输出续接 + 崩溃识别：finish_reason=length 自动续接；流中断/5xx bad_alloc → Completed=false
        var log2 = (string s) => Log?.Invoke(s);

        // §4.1 截断断点快照闭包：finish_reason=length 时、续接请求发出前 save 槽位 KV（此时槽位 KV 仍完整）
        Func<Task>? onTrunc = null;
        var kvForTrunc = _kvCache;
        if (kvForTrunc != null && routedSlot is int truncSlot && !string.IsNullOrEmpty(routedKey))
        {
            var truncKey = routedKey;
            onTrunc = async () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await kvForTrunc.SaveAsync(truncSlot, truncKey);
                EmitSlot($"[KV-SAVE] 截断断点快照：{truncKey} → slot{truncSlot}（{sw.Elapsed.TotalSeconds:F1}s）");
                lock (_kvStateGate) _truncPending.Add(truncKey); // 标记「截断待续接」
            };
        }

        bool completed;
        string accumulated = ""; // bad_alloc 崩溃路径无输出累积（保持与原实现一致的初始值）
        if (resp.IsSuccessStatusCode)
        {
            if (effStreaming)
            {
                // SSE 流式响应：必须设置 text/event-stream（llama-server 返回 application/json，
                // 直接复制会导致客户端按 JSON 解析 SSE 行报错 "Unexpected non-whitespace character after JSON"）
                outResp.ContentType = "text/event-stream";
                (completed, accumulated) = await OutputContinuer.HandleStreamAsync(Backend, uri, finalBody, resp, outResp, _cfg, log2, onTrunc);
            }
            else
                (completed, accumulated) = await OutputContinuer.HandleNonStreamAsync(Backend, uri, finalBody, resp, outResp, _cfg, log2, _cfg.CrashRecoveryEnabled);
        }
        else
        {
            // 5xx 错误响应：判定是否 bad_alloc 崩溃（恢复启用 → 不转发，交给崩溃恢复）
            string errBody = Encoding.UTF8.GetString(await resp.Content.ReadAsByteArrayAsync());
            // 修复4：聚合最近 8 行输出与 errBody 关键词，避免 OOM 提示跨行/跨流时被漏识别
            bool isBadAlloc = errBody.Contains("bad allocation", StringComparison.OrdinalIgnoreCase)
                              || CrashRecovery.WasBadAlloc(BadAllocEvidenceWindow)
                              || RecentOutputHasBadAlloc();
            if (isBadAlloc && _cfg.CrashRecoveryEnabled && effStreaming)
            {
                completed = false; // 交给 TryCrashRecoverAsync
            }
            else
            {
                // P3-D：后端 5xx 错误响应原样透传，但加 X-Error-Source: upstream 标记来源，
                // 让客户端/监控区分是本机网关错误（走 V2 格式）还是上游 llama-server 直返的原始 JSON。
                outResp.Headers["X-Error-Source"] = "upstream";
                var bytes = System.Text.Encoding.UTF8.GetBytes(errBody);
                outResp.ContentType = "application/json";
                outResp.ContentLength64 = bytes.Length;
                await outResp.OutputStream.WriteAsync(bytes);
                completed = true;
            }
        }
        return (completed, accumulated);
    }

    /// <summary>请求体 dump（应用识别分析用）：原始 body + headers 入统一日志管道 Dump 流（request_dump.log，2MB 轮切）。
    /// 时间戳由管道 Enqueue 侧统一添加（秒级精度）。</summary>
    private void DumpRequest(HttpListenerContext ctx, byte[] bodyBytes)
    {
        // AH-21：首次 dump 提示隐私风险（request_dump.log 完整落盘请求体与请求头，含对话/prompt 内容）
        if (Interlocked.Exchange(ref _dumpPrivacyWarned, 1) == 0)
            Log?.Invoke("注意：请求体 dump 已启用（RequestDumpEnabled）——request_dump.log 将完整记录请求体与请求头，包含对话与 prompt 隐私内容，请仅在本地分析时开启。");
        try
        {
            var req = ctx.Request;
            var path = req.Url?.AbsolutePath ?? "";
            var bodyStr = System.Text.Encoding.UTF8.GetString(bodyBytes);

            var headers = new StringBuilder();
            foreach (var key in req.Headers.AllKeys)
            {
                headers.AppendLine($"{key}: {req.Headers[key]}");
            }

            // 请求体截断（DumpBodyMaxLength 字符）：避免日志爆炸，system prompt 通常在前部
            if (bodyStr.Length > DumpBodyMaxLength)
                bodyStr = bodyStr.Substring(0, DumpBodyMaxLength) + $"...(truncated, total {System.Text.Encoding.UTF8.GetByteCount(bodyStr)} bytes)";

            var dumpBlock = $"POST {path}\n--- Headers ---\n{headers}--- Body ---\n{bodyStr}\n{new string('=', 80)}\n\n";
            LogFile.DumpAppend(dumpBlock); // 异步管道：请求路径零磁盘 I/O
        }
        catch (Exception ex)
        {
            // P3-D：dump 失败不影响主流程，但需可观测输出（否则请求分析静默失效，配置已开却无日志产出）
            Log?.Invoke($"[WARN] 请求 dump 失败：{ex.GetType().Name} {ex.Message}（不阻塞主流程）");
        }
    }
}
