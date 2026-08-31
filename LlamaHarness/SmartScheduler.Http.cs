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
    private void StartListening()
    {
        if (_listener.IsListening) return;
        try
        {
            // 仅绑定本机回环，无需管理员权限
            _listener.Prefixes.Add($"http://localhost:{_cfg.Port}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{_cfg.Port}/");
            _listener.Start();
            Log?.Invoke($"智能模式：已接管端口 {_cfg.Port}（llama-server 唤醒时将自动选择空闲后端端口，首选 {PreferredBackendPort}），当前显存占用为 0。");
            _ = AcceptLoopAsync();
        }
        catch (HttpListenerException ex)
        {
            Log?.Invoke($"监听端口 {_cfg.Port} 失败（可能被占用）：{ex.Message}");
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
                await Task.Delay(2000);
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
            if (got && ctx != null) _ = HandleRequestAsync(ctx); // 仅成功取到请求时处理；重试后回到循环顶部
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;

        // 本地状态探测端点：不触发唤醒、不刷新闲置计时
        var reqPath = req.Url?.AbsolutePath;
        if (string.Equals(reqPath, "/__status__", StringComparison.OrdinalIgnoreCase))
        {
            // C-103：phase 输出枚举名、idle_minutes 为当前已闲置分钟数（动态值）+ 配置阈值、
            // recent_logs 取 LogFile 环形缓冲（含 harness 侧日志），供外部 Agent 远程诊断
            var aff = _affinity;
            var idleMinutes = (DateTime.Now - new DateTime(Interlocked.Read(ref _lastTouchTicks))).TotalMinutes;
            var payload = new
            {
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
            RequestProcessor.WriteError(ctx, 502, "LLM 服务正在休眠释放，请稍后重试。");
            return;
        }

        bool isInference = RequestProcessor.IsInferenceRequest(req);

        // 探测类请求（GET /v1/models、健康检查等）无唤醒权：
        // 服务运行时照常代理；待机/休眠时直接拒绝，防止 Agent 周期性轮探
        // 把刚休眠的服务反复唤醒（唤醒→15分钟倒计时→再休眠→再唤醒循环）
        if (!isInference && !_server.IsRunning)
        {
            RequestProcessor.WriteError(ctx, 503, "LLM 服务处于待机/休眠状态，仅推理请求可触发唤醒。");
            return;
        }

        int cur = Interlocked.Increment(ref _inflight);
        if (cur > Volatile.Read(ref _inflightPeak)) Volatile.Write(ref _inflightPeak, cur); // C-102 峰值记录
        try
        {
            // 首请求排队等待唤醒完成（共享同一唤醒任务，防多进程冲突）
            await EnsureRunningAsync();
            // 只有真实推理请求才刷新闲置计时；探测类请求不算使用
            if (isInference) Touch();
            await ForwardAsync(ctx);       // 代理转发到后端 llama-server（流式直通）
            if (isInference) Touch();      // 请求完成：再次刷新倒计时
        }
        catch (Exception ex)
        {
            // 带上内层异常细节，便于定位（如连接重置 vs 超时）
            var detail = ex.InnerException != null ? $"（内层：{ex.InnerException.Message}）" : "";
            Log?.Invoke($"请求处理失败：{ex.Message}{detail}");
            RequestProcessor.WriteError(ctx, 503, ex.Message);
        }
        finally
        {
            Interlocked.Decrement(ref _inflight);
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