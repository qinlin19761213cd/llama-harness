using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ThinkingModeHelper = LlamaHarness.ThinkingMode;

namespace LlamaHarness;

/// <summary>
/// HTTP 监听与请求接收（StartListening/StopListening/AcceptLoopAsync/HandleRequestAsync/WarnNonStreamOnce）。partial 聚类方法体零改动。
/// </summary>
public partial class SmartScheduler
{
    private const int AcceptRetryDelayMs = 2000;
    /// <summary>P2 修复项 4：请求体 32 MB 上限（防 body DoS）。Pipeline 层 MaxRequestBodyBytes=64MB 是最后兜底；
    /// 前置到 HttpListener 层用 ContentLength64 预检可提前拒绝超限请求，避免读入占用内存。</summary>
    private const long MaxRequestBodyBytesHttp = 32L * 1024 * 1024;
    /// <summary>P2 修复项 11：整体 handler 超时（秒）——从入口到响应写回的兜底超时。
    /// Pipeline 层各阶段已有各自 CTS（Body 读/转发/重试），此处作为最外层看门狗防止任何阶段死锁。</summary>
    private const int HandlerTimeoutSeconds = 300;

    /// <summary>P2 修复项 6：进程启动时间（UTC），用于 /health 与 /__status__ 的 uptime 计算。</summary>
    private readonly DateTime _startTime = DateTime.UtcNow;

    private void StartListening()
    {
        if (_listener.IsListening) return;
        try
        {
            // P2 修复项 1：prefix 绑定前探测端口可用性——若 _cfg.Port 被占用（其他进程抢占/上次异常未释放），
            // _listener.Start() 会抛 HttpListenerException(483) 直接导致整个监听失败；
            // 探测不通过则记录更明确的错误提示，让上层能感知并提示用户改端口。
            if (!IsHttpListenerPortAvailable(_cfg.Port))
            {
                Log?.Invoke($"监听端口 {_cfg.Port} 已被占用（HTTP prefix 探测失败），请修改配置后重启智能模式。");
                RaiseStatus($"监听端口 {_cfg.Port} 被占用，请改用其他端口。");
                return;
            }
            // 仅绑定本机回环，无需管理员权限
            _listener.Prefixes.Add($"http://localhost:{_cfg.Port}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{_cfg.Port}/");
            _listener.Start();
            Log?.Invoke($"智能模式：已接管端口 {_cfg.Port}（llama-server 唤醒时将自动选择空闲后端端口，首选 {PreferredBackendPort}），当前显存占用为 0。");
            // v2.23.5：AcceptLoop 必须移出 UI 线程（Task.Run 线程池）——本类请求处理链
            // （AcceptLoop→HandleRequest→Forward→Pump→自愈/重试/TOKEN-GUARD）的 await 均无
            // ConfigureAwait(false)，若从 UI 线程启动则所有 IO 恢复点回 UI 线程 SynchronizationContext，
            // dsh 高并发/报错重试时大量逻辑在 UI 线程执行、消息泵饿死 → 界面假死（实测根因）。
            // 各 UI 回调（Log/Status/InFlight/Slot）已封送（BeginInvoke/InvokeOnUi），线程池启动安全。
            _ = Task.Run(AcceptLoopAsync);
        }
        catch (HttpListenerException ex)
        {
            // 探测通过后仍失败（TOCTOU 竞态：探测→Start 期间被其他进程抢占），保留原有错误日志
            Log?.Invoke($"监听端口 {_cfg.Port} 失败（可能被占用）：{ex.Message}");
        }
    }

    /// <summary>P2 修复项 1：探测 HttpListener prefix 是否可绑定。
    /// 使用独立的临时 HttpListener 尝试 Start——只有真正执行过 HttpListener.Start() 才能
    /// 判定端口可用性（Socket.TryBind 只能判定 TCP 层，无法验证 HttpListener 特权前缀规则）。
    /// 探测成功即 Stop/Dispose，避免占用端口等待正式 Start。</summary>
    private static bool IsHttpListenerPortAvailable(int port)
    {
        HttpListener? probe = null;
        try
        {
            probe = new HttpListener();
            probe.Prefixes.Add($"http://localhost:{port}/");
            probe.Start();
            return true;
        }
        catch (HttpListenerException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            // 端口非法等参数错误：交由正式 Start 处理，此处视为可用不阻塞
            return true;
        }
        finally
        {
            // HttpListener 在 .NET 8 中没有 IDisposable，只能 Stop 释放端口；进程退出后由运行时回收
            try { probe?.Stop(); } catch { }
        }
    }

    private void StopListening()
    {
        try
        {
            if (_listener.IsListening) _listener.Stop();
        }
        catch
        {
            // 忽略停止异常
        }
    }

    private async Task AcceptLoopAsync()
    {
        int failures = 0;
        while (_listener.IsListening)
        {
            HttpListenerContext? ctx = null;
            bool got = false;
            try
            {
                ctx = await _listener.GetContextAsync();
                got = true;
                failures = 0;
            }
            catch (Exception ex)
            {
                // C-008：运行期监听异常（端口抢占/睡眠唤醒/权限变更）——记录 + 有限次数重试
                if (!_listener.IsListening) return; // 正常停止，静默退出
                Log?.Invoke($"错误：监听异常（{ex.Message}），尝试重新监听…");
                if (++failures >= 3)
                {
                    RaiseStatus("监听失败：端口不可用，请检查端口后重启智能模式。");
                    return;
                }
                await Task.Delay(AcceptRetryDelayMs);
                try
                {
                    _listener.Stop();
                    _listener.Start();
                    Log?.Invoke("监听已重新建立。");
                }
                catch (Exception ex2)
                {
                    Log?.Invoke($"错误：重新监听失败：{ex2.Message}");
                    RaiseStatus("监听失败：端口不可用，请检查端口后重启智能模式。");
                    return;
                }
            }
            if (got && ctx != null) _ = HandleRequestWithCatchAsync(ctx); // 仅成功取到请求时处理；重试后回到循环顶部
        }
    }

    /// <summary>HandleRequestAsync 的异常包装器，防止 fire-and-forget 导致未处理异常。</summary>
    private async Task HandleRequestWithCatchAsync(HttpListenerContext ctx)
    {
        try { await HandleRequestAsync(ctx); }
        catch (Exception ex)
        {
            // fire-and-forget 路径：异常必须被捕获并记录，否则会被 .NET 运行时终结进程
            Log?.Invoke($"请求处理未捕获异常：{ex.Message}");
        }
    }

    /// <summary>P2 修复项 7：统一错误响应格式 {"error":{"code":"...","message":"..."}}。
    /// RequestProcessor.WriteError 不可改，本方法在 Http.cs/Gateway.cs 内新增，逐步替换两文件内的调用。
    /// Pipeline.cs 内 8 处 WriteError 调用受文件修改白名单限制保留旧格式（单字段），最终说明中已注明。</summary>
    private static void WriteErrorV2(HttpListenerContext ctx, int statusCode, string errorCode, string message)
    {
        try
        {
            var resp = ctx.Response;
            resp.StatusCode = statusCode;
            resp.ContentType = "application/json; charset=utf-8";
            // HttpListenerResponse 无独立 Charset 属性（charset 由 ContentType 里的 "charset=" 部分承载）
            var json = System.Text.Json.JsonSerializer.Serialize(new { error = new { code = errorCode, message } });
            var buf = Encoding.UTF8.GetBytes(json);
            resp.ContentLength64 = buf.Length;
            resp.OutputStream.Write(buf, 0, buf.Length);
            resp.Close();
        }
        catch
        {
            // 客户端已断开：静默失败
        }
    }

    /// <summary>P2 修复项 5：为响应设置 CORS 头（Access-Control-Allow-*）。
    /// 本机网关仅监听 localhost，CORS 主要用于本地浏览器前端/跨源 devtools 调试场景。</summary>
    private static void EnsureCors(HttpListenerResponse resp)
    {
        resp.Headers["Access-Control-Allow-Origin"] = "*";
        resp.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, OPTIONS, HEAD";
        resp.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization, X-Request-Id, X-Session-Id, api-key";
        resp.Headers["Access-Control-Max-Age"] = "86400";
    }

    /// <summary>P2 修复项 5：处理 OPTIONS 预检请求。返回 true 表示已处理（应直接 return 上层）。</summary>
    private static bool TryHandleOptions(HttpListenerContext ctx)
    {
        if (!string.Equals(ctx.Request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase)) return false;
        var resp = ctx.Response;
        EnsureCors(resp);
        resp.StatusCode = 204;
        resp.Close();
        return true;
    }

    /// <summary>P2 修复项 10：Body 编码校验——拒绝非 UTF-8 请求体（Content-Type charset 声明非 UTF-8 时直接 415）。
    /// 未显式声明 charset 时视为 UTF-8（HTTP 规范默认），放行以兼容不传 charset 的客户端。</summary>
    private static bool TryValidateEncoding(HttpListenerRequest req)
    {
        var ct = req.ContentType;
        if (string.IsNullOrEmpty(ct)) return true;
        // 仅当显式声明 charset 且非 UTF-8 才拒绝
        var idx = ct.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return true;
        var charsetPart = ct.Substring(idx + "charset=".Length);
        var sep = charsetPart.IndexOfAny(new[] { ';', ',', ' ', ')' });
        if (sep >= 0) charsetPart = charsetPart.Substring(0, sep);
        charsetPart = charsetPart.Trim().Trim('"').Trim('\'');
        if (string.IsNullOrEmpty(charsetPart)) return true;
        // 允许 UTF-8 / utf-8 / UTF8 / US-ASCII 视为 UTF-8 家族
        return string.Equals(charsetPart, "utf-8", StringComparison.OrdinalIgnoreCase)
            || string.Equals(charsetPart, "utf8", StringComparison.OrdinalIgnoreCase)
            || string.Equals(charsetPart, "us-ascii", StringComparison.OrdinalIgnoreCase)
            || string.Equals(charsetPart, "ascii", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>P2 修复项 4：请求体大小预检（ContentLength64）。
    /// 返回 true 表示超限已拒绝（上层应 return）；返回 false 表示允许继续。
    /// 未声明 Content-Length 时放行（可能用 chunked），交给 Pipeline 层读体阶段的 64MB 兜底。</summary>
    private static bool TryValidateBodySize(HttpListenerRequest req)
    {
        if (req.ContentLength64 > 0 && req.ContentLength64 > MaxRequestBodyBytesHttp)
            return true;
        return false;
    }

    /// <summary>P2 修复项 9：X-Request-Id 提取/生成。
    /// 优先复用上游请求头；缺省时生成 16 字节短 ID（避免 Guid 32 字符过长）。返回值同时用于响应头回传。</summary>
    private static string ResolveRequestId(HttpListenerRequest req)
    {
        var incoming = req.Headers["X-Request-Id"];
        if (!string.IsNullOrWhiteSpace(incoming)) return incoming.Trim();
        return Guid.NewGuid().ToString("N");
    }

    /// <summary>P2 修复项 6：/health 端点——返回 status + uptime + phase，供外部监控探活。
    /// 返回 true 表示已处理（上层应 return）。</summary>
    private bool HandleHealth(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "";
        if (!string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase)) return false;
        var uptimeSec = (long)(DateTime.UtcNow - _startTime).TotalSeconds;
        var phase = CurrentPhase.ToString().ToLowerInvariant();
        // status 字段：健康/不健康判定，便于监控直接判定
        var status = CurrentPhase == Phase.Sleeping || _stopRequested ? "degraded" : "ok";
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            status,
            uptime_seconds = uptimeSec,
            phase,
            inflight = Volatile.Read(ref _inflight),
            backend_port = _backendPort,
        });
        RequestProcessor.WriteJsonAsync(ctx, 200, json).GetAwaiter().GetResult();
        return true;
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        // P2 修复项 5：CORS 预检快速路径（OPTIONS 不进入下游管道，直接 204 返回）
        if (TryHandleOptions(ctx)) return;

        if (_stopRequested)                       // 修复9：StopNow 后拒绝新请求，返回 503 Service Unavailable
        {
            try
            {
                var stopResp = ctx.Response;
                stopResp.StatusCode = 503;
                EnsureCors(stopResp);
                stopResp.Close();
            }
            catch { }
            return;
        }
        var req = ctx.Request;

        // P2 修复项 9：X-Request-Id 读取/生成并回传——每条链路一个全局可搜索 ID，日志/监控可用它把请求串联起来
        var requestId = ResolveRequestId(req);
        ctx.Response.Headers["X-Request-Id"] = requestId;
        // P2 修复项 5：所有响应统一附加 CORS 头（OPTIONS 已在上方拦截，此分支处理正常方法）
        EnsureCors(ctx.Response);

        // P2 修复项 6：/health 端点快速路径（不触发唤醒、不刷新闲置计时、不走转发管道）
        if (HandleHealth(ctx)) return;

        // 本地状态探测端点：不触发唤醒、不刷新闲置计时
        var reqPath = req.Url?.AbsolutePath;
        if (string.Equals(reqPath, "/__status__", StringComparison.OrdinalIgnoreCase))
        {
            // C-103：phase 输出枚举名、idle_minutes 为当前已闲置分钟数（动态值）+ 配置阈值、
            // recent_logs 取 LogFile 环形缓冲（含 harness 侧日志），供外部 Agent 远程诊断
            // P2 修复项 6：补充 status（健康判定）+ uptime_seconds（进程启动时长），便于监控直接消费
            var aff = _affinity;
            var idleMinutes = (DateTime.Now - new DateTime(Interlocked.Read(ref _lastTouchTicks))).TotalMinutes;
            var uptimeSec = (long)(DateTime.UtcNow - _startTime).TotalSeconds;
            var healthStatus = CurrentPhase == Phase.Sleeping ? "degraded" : "ok";
            var payload = new
            {
                status = healthStatus,
                uptime_seconds = uptimeSec,
                phase = CurrentPhase.ToString(),
                inflight = Volatile.Read(ref _inflight),
                backend_port = _backendPort,
                idle_minutes = Math.Round(idleMinutes, 1),
                idle_threshold_minutes = IdleMinutes,
                slots = aff == null ? null : new
                {
                    count = aff.SlotCount,
                    bindings = aff.Snapshot().ToDictionary(
                        kv => kv.Key,
                        kv => new { slot = kv.Slot, last_active = kv.LastActive }),
                },
                recent_logs = LogFile.SnapshotRecent(),
            };
            await RequestProcessor.WriteJsonAsync(ctx, 200, System.Text.Json.JsonSerializer.Serialize(payload));
            return;
        }

        // 休眠释放进行中：不转发（服务正被终止），提示客户端稍后重试
        if (CurrentPhase == Phase.Sleeping)
        {
            WriteErrorV2(ctx, 502, "SERVICE_SLEEPING", "LLM 服务正在休眠释放，请稍后重试。");
            return;
        }

        bool isInference = RequestProcessor.IsInferenceRequest(req);

        // 探测类请求（GET /v1/models、健康检查等）无唤醒权：
        // 服务运行时照常代理；待机/休眠时直接拒绝，防止 Agent 周期性轮探
        // 把刚休眠的服务反复唤醒（唤醒→15分钟倒计时→再休眠→再唤醒循环）
        if (!isInference && !_server.IsRunning)
        {
            WriteErrorV2(ctx, 503, "SERVICE_NOT_RUNNING", "LLM 服务处于待机/休眠状态，仅推理请求可触发唤醒。");
            return;
        }

        int cur = Interlocked.Increment(ref _inflight);
        // C-102 峰值记录：使用 CAS 循环避免 TOCTOU 竞态
        int peak;
        do
        {
            peak = Volatile.Read(ref _inflightPeak);
            if (cur <= peak) break;
        } while (Interlocked.CompareExchange(ref _inflightPeak, cur, peak) != peak);
        // 在途任务明细登记（v2.18 状态栏服务阶段卡片）：方法 + 路径 + 亲和应用名（未知请求 App 为 null）
        var affTask = _affinity;
        string? taskKey = affTask?.GetAffinityKey(req.Headers);
        string? taskApp = taskKey == null ? null : affTask?.AppNameOf(taskKey);
        int taskSeq = _inflightTracker.Register(req.HttpMethod, req.Url?.AbsolutePath ?? req.RawUrl ?? "?", taskApp);
        InFlightChanged?.Invoke();

        // P2 修复项 8：StreamingResponse 中止
        // 客户端断连的中止实际由 Pipeline.cs L344 的 IOException 兜底天然完成：
        // 客户端断开后 handlerTask 内写响应会抛 IOException → Pipeline catch 并日志记录 →
        // outResp.Close() → llama-server 感知上游断连并取消生成任务。
        // HttpListenerRequest 无 IsLocalConnectionClosed / Abort API，不再做轮询式探测。

        // v2.21 性能埋点：仅推理请求计时（四段时延：t_recv→t_ready→t_sent→t_complete）
        string? rtId = null;
        // P2 修复项 11：整体 handler 超时兜底——用外层 CTS 作为最外层看门狗，
        // Pipeline 层各阶段（唤醒等待 / 转发 / 写回）虽有自己的 CTS，但万一遗漏会被这个统一超时约束。
        // 用 Task.WhenAny 模式监视到期事件：到期后 handlerCts.Cancel()，并检查下游任务是否取消，
        // 若下游无 token 参数无法被直接中断，则由看门狗在 await 完成后统一返回 504（不改变下游任务执行，但保证请求响应不越过 HandlerTimeoutSeconds）。
        using var handlerCts = new CancellationTokenSource(TimeSpan.FromSeconds(HandlerTimeoutSeconds));
        try
        {
            // P2 修复项 10：编码校验（在触发唤醒前尽早拒绝非法请求，避免为不合法请求付出唤醒代价）
            if (!TryValidateEncoding(req))
            {
                WriteErrorV2(ctx, 415, "UNSUPPORTED_MEDIA_TYPE", "请求体编码必须是 UTF-8。");
                return;
            }
            // P2 修复项 4：请求体大小预检（ContentLength64 声明时提前拒绝，避免占内存读入）
            if (TryValidateBodySize(req))
            {
                WriteErrorV2(ctx, 413, "REQUEST_BODY_TOO_LARGE", $"请求体超过 {MaxRequestBodyBytesHttp / (1024 * 1024)} MB 上限。");
                return;
            }

            // 把"唤醒 + 转发 + 打点"打包成单一下游任务，便于看门狗用 Task.WhenAny 监视其完成时机。
            var handlerTask = Task.Run(async () =>
            {
                // 首请求排队等待唤醒完成（共享同一唤醒任务，防多进程冲突）
                await EnsureRunningAsync();
                if (rtId != null) _timing.MarkReady(rtId); // 打点：唤醒/排队完成（后端就绪）
                // 只有真实推理请求才刷新闲置计时；探测类请求不算使用
                if (isInference)
                {
                    Touch();
                    rtId = _timing.Begin(taskApp ?? "?", req.Url?.AbsolutePath ?? req.RawUrl ?? "?");
                }
                await ForwardAsync(ctx, rtId);       // 代理转发到后端 llama-server（流式直通）
                if (rtId != null) _timing.Complete(rtId, success: true); // 打点：成功完成
                if (isInference) Touch();            // 请求完成：再次刷新倒计时
            });

            // 看门狗：handlerTask 正常完成 vs handlerCts 到期
            // 用 Task.Delay(Infinite, token) 挂起直到 handlerCts 触发取消，再 WhenAny 比较两个任务完成顺序。
            var watchdog = Task.Delay(Timeout.Infinite, handlerCts.Token);
            var finished = await Task.WhenAny(handlerTask, watchdog);
            if (finished == handlerTask)
            {
                await handlerTask; // 传播下游异常（成功路径无异常；失败路径抛出由外层 catch 分类）
            }
            else
            {
                // 看门狗先到：显式 Cancel（handlerCts 到期也会触发，但显式 Cancel 保证 token 状态已变更）
                handlerCts.Cancel();
                // 立即抛 TimeoutException 走外层统一 catch → 504；不再阻塞等待 handlerTask
                // （handlerTask 会继续在后台跑，Pipeline 层各阶段自己有 CTS/超时兜底）
                throw new TimeoutException($"请求处理超时（>{HandlerTimeoutSeconds}s）");
            }
        }
        catch (OperationCanceledException)
        {
            // P2 修复项 11：整体超时到期或被上层 CTS 取消——统一返回 504
            Log?.Invoke($"请求超时或取消（RequestId={requestId}）。");
            try { WriteErrorV2(ctx, 504, "REQUEST_TIMEOUT", $"请求处理超时（>{HandlerTimeoutSeconds}s）。"); } catch { }
        }
        catch (Exception ex)
        {
            // 带上内层异常细节，便于定位（如连接重置 vs 超时）
            var detail = ex.InnerException != null ? $"（内层：{ex.InnerException.Message}）" : "";
            Log?.Invoke($"请求处理失败（RequestId={requestId}）：{ex.Message}{detail}");
            if (rtId != null) _timing.Complete(rtId, success: false); // 打点：失败完成
            // M-03 修复：不直接透传 ex.Message（可能含本机路径/模型文件名等内部信息），
            // 客户端只返回错误类型 + 通用文案；完整细节走 Log（本地日志）
            string clientMsg = ex is TimeoutException ? "服务唤醒/请求超时，请稍后重试。"
                            : ex is System.Net.Http.HttpRequestException or IOException or System.Net.Sockets.SocketException
                                ? "与后端服务连接异常，请检查 llama-server 状态。"
                                : "服务内部错误，详见本地日志。";
            string errCode = ex is TimeoutException ? "TIMEOUT"
                             : ex is System.Net.Http.HttpRequestException or IOException or System.Net.Sockets.SocketException
                                 ? "BACKEND_UNREACHABLE"
                                 : "INTERNAL_ERROR";
            try { WriteErrorV2(ctx, 503, errCode, clientMsg); } catch { }
        }
        finally
        {
            Interlocked.Decrement(ref _inflight);
            _inflightTracker.Unregister(taskSeq);
            InFlightChanged?.Invoke();
        }
    }

    /// <summary>非流式推理请求告警（每会话一次）：非流式是"断开→全量重填"循环的常见诱因。</summary>
    private void WarnNonStreamOnce()
    {
        if (Interlocked.Increment(ref _nonStreamWarned) == 1)
            Log?.Invoke("警告：检测到非流式推理请求。llama-server 会阻塞整个生成后才返回，客户端读超时可能触发断开→重试全量重新预填。" +
                        "建议：Agent 侧启用流式（stream=true）或加大请求超时；也可在启动器开启「强制流式」。");
    }
}
