using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LlamaHarness;

/// <summary>
/// 输出续接 + 崩溃恢复管道：
/// - 流式原位续接：finish_reason=length → 追加 assistant 输出 + 续接指令发起下一轮，继续灌入同一条流（客户端无感）。
///   末块 finish_reason 归一化为 stop + usage 跨轮累加。
/// - 工具隔离：累积输出中出现 tool_calls → 不续接（透传），防 tool_call JSON 拼接损坏。
/// - 崩溃识别：流在无 [DONE] 时中断 → 返回 Aborted（调用方判定 bad_alloc 并触发恢复）。
/// - 非流式：循环续接；bad_alloc 错误响应 → 返回未完成（不转发错误体，交给崩溃恢复）。
/// </summary>
public static class OutputContinuer
{
    private const int FlushDelayMs = 2000;
    private const int HeldLineMax = 65536;
    private const string ContinuePrompt = "请继续输出，不要重复已有内容，延续上文逻辑完成剩余内容";
    /// <summary>AH-3：流式读 idle 超时（毫秒）。后端长期无字节时判定疑似假死（10 分钟足够容忍大上下文 prefill / 长思考）。</summary>
    private const int BackendIdleTimeoutMs = 600_000;

    /// <summary>单轮 SSE 管道结果。</summary>
    private enum RoundOutcome { Normal, Truncated, Aborted }

    /// <summary>跨轮累计状态。</summary>
    private sealed class SseState
    {
        public StringBuilder Accumulated { get; } = new(); // 累计生成内容（续接回填用）
        public bool HasToolCalls { get; set; }             // 输出出现 tool_calls → 不续接
        public string? FinishReason { get; set; }          // 本轮末块 finish_reason
        public long PromptTokens { get; set; }             // usage 跨轮累加
        public long CompletionTokens { get; set; }
        public bool HasUsage { get; set; }
    }

    /// <summary>流式原位续接：把 firstResp 的 SSE 灌入客户端；finish_reason=length 时自动续接（最多 cfg.MaxContinuations 轮）。</summary>
    /// <param name="onTruncation">截断断点回调：finish_reason=length 触发、续接请求发出前调用（槽位 KV 仍完整，可 save 断点快照）。null = 不启用。</param>
    /// <returns>(Completed, Accumulated)：Completed=false 表示流中断（需崩溃恢复）；Accumulated = 已生成内容。</returns>
    public static Task<(bool Completed, string Accumulated)> HandleStreamAsync(
        IBackendClient backend, Uri uri, string originalBody,
        HttpResponseMessage firstResp, HttpListenerResponse outResp,
        AppConfig cfg, Action<string>? log, Func<Task>? onTruncation = null)
        => PipeLoop(backend, uri, originalBody, firstResp, outResp, cfg, log, onTruncation: onTruncation);

    /// <summary>发起新的推理请求并把 SSE 灌入客户端（崩溃恢复重放路径；同样支持截断续接）。</summary>
    /// <param name="writeGate">写门控：与并发 keep-alive 写入互斥，防 SSE 行交错损坏。null = 无并发写者（普通路径）。</param>
    public static async Task<(bool Completed, string Accumulated)> SendAndPipeStreamAsync(
        IBackendClient backend, Uri uri, string body,
        HttpListenerResponse outResp, AppConfig cfg, Action<string>? log,
        SemaphoreSlim? writeGate = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(cfg.ContinuationTimeoutSeconds));
        var resp = await backend.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        return await PipeLoop(backend, uri, body, resp, outResp, cfg, log, writeGate);
    }

    /// <summary>核心管道循环：灌一轮 SSE；截断时自动续接（最多 MaxContinuations 轮）。</summary>
    private static async Task<(bool Completed, string Accumulated)> PipeLoop(
        IBackendClient backend, Uri uri, string originalBody,
        HttpResponseMessage firstResp, HttpListenerResponse outResp,
        AppConfig cfg, Action<string>? log, SemaphoreSlim? writeGate = null,
        Func<Task>? onTruncation = null)
    {
        var state = new SseState();
        HttpResponseMessage resp = firstResp;
        int round = 0;

        while (true)
        {
            bool allowContinue = cfg.ContinuationEnabled && round < cfg.MaxContinuations;
            var outcome = await PipeOneRoundAsync(resp, outResp, state, allowContinue, writeGate);
            resp.Dispose();
            if (outcome != RoundOutcome.Truncated)
                return (outcome != RoundOutcome.Aborted, state.Accumulated.ToString());

            // P1：跨轮 keep-alive——等待下一轮期间（KV save / tokenize / prefill）周期写 SSE 注释行，
            // 防客户端空闲超时掐线；本轮响应到手后取消并等最后一条注释写完再开下一轮管道，防 SSE 行交错
            using var kaCts = new CancellationTokenSource();
            var keepAlive = RunKeepAlive(outResp, writeGate, kaCts.Token);
            try
            {
                // 截断断点回调（§4.1）：续接请求发出前触发，槽位 KV 仍完整（可 save 断点快照）；失败不阻断续接
                if (onTruncation != null)
                {
                    try { await onTruncation(); } catch { /* 断点快照失败不影响续接 */ }
                }

                // 构造续接请求：追加 assistant 输出 + 续接指令（originalBody 含 n_slots → 同槽亲和，KV 前缀命中免重算）
                var nextBody = BuildContinuationBody(originalBody, state.Accumulated.ToString());
                if (nextBody == null) return (false, state.Accumulated.ToString());
                originalBody = nextBody;

                // TokenGuard 防护（续接输入可能超预算）
                int budget = cfg.GetInputBudget();
                var (ok, guarded, note) = await TokenGuard.GuardAsync(backend, originalBody, budget);
                if (!ok) { log?.Invoke($"续接中止：{note}"); return (false, state.Accumulated.ToString()); }
                if (guarded != null && guarded != originalBody) originalBody = guarded;
                if (note != null) log?.Invoke(note);

                // 发起下一轮推理
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, uri)
                    {
                        Content = new StringContent(originalBody, Encoding.UTF8, "application/json"),
                    };
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(cfg.ContinuationTimeoutSeconds));
                    resp = await backend.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    round++;
                    log?.Invoke($"续接触发（第 {round} 轮）：输出截断（finish_reason=length），自动续接…");
                }
                catch (OperationCanceledException)
                {
                    // A8 修复：续接请求被取消（客户端断开 / ContinuationTimeout 超时 / 停机）——非崩溃。
                    // 向上抛（与第一轮异常路径一致）：PumpResponseAsync catch 记录"响应管道异常"并关闭连接，
                    // 不触发崩溃恢复——避免把"取消/超时/业务错误"误判为 bad_alloc 而白白重放/重启。
                    log?.Invoke($"续接请求取消（第 {round + 1} 轮）：超时或连接中止，保留已生成内容。");
                    throw;
                }
                catch (Exception ex)
                {
                    log?.Invoke($"续接请求异常（第 {round + 1} 轮）：{ex.Message}");
                    return (false, state.Accumulated.ToString());
                }
            }
            finally
            {
                kaCts.Cancel();
                await keepAlive; // 等最后一条注释行写完再开下一轮管道
            }
        }
    }

    /// <summary>P1：跨轮 keep-alive——等待下一轮期间周期写 SSE 注释行（客户端按规范忽略），防空闲超时掐线。</summary>
    private static Task RunKeepAlive(HttpListenerResponse outResp, SemaphoreSlim? writeGate, CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(FlushDelayMs, ct);
                    // P1：心跳必须发「合法 SSE data 事件」（空 delta chunk）。
                    // 不能用 ":" 开头的注释行——DSH(deepseek-harness) 等严格 JSON 客户端无法解析注释行，
                    // 会报 "Unexpected non-whitespace character after JSON"。空 delta chunk 符合 OpenAI SSE 规范，
                    // 客户端解析后不产生内容，既保活又不污染输出。
                    var bytes = Encoding.UTF8.GetBytes("data: {\"id\":\"hb\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{}}]}\n\n");
                    if (writeGate != null) await writeGate.WaitAsync();
                    try
                    {
                        await outResp.OutputStream.WriteAsync(bytes);
                        await outResp.OutputStream.FlushAsync();
                    }
                    finally
                    {
                        if (writeGate != null) writeGate.Release();
                    }
                }
            }
            catch (OperationCanceledException) { /* 正常取消 */ }
            catch { /* 客户端已断开，keep-alive 失败不影响主流程 */ }
        });
    }

    /// <summary>
    /// 灌一轮 SSE 到客户端并累积内容；末块（含 finish_reason）暂扣待决策/改写。
    /// Normal = 正常结束；Truncated = 需续接（末块已剥离 finish_reason、本轮 [DONE] 抑制不写，客户端继续等待下一轮流）；
    /// Aborted = 流在无 [DONE] 时中断（崩溃迹象）。
    /// </summary>
    private static async Task<RoundOutcome> PipeOneRoundAsync(
        HttpResponseMessage resp, HttpListenerResponse outResp, SseState state, bool allowContinue,
        SemaphoreSlim? writeGate = null)
    {
        var stream = resp.Content.ReadAsStream();
        var held = new List<string>();      // finish_reason 之后暂扣的原始行（含 [DONE]）
        string? finalPayload = null;        // 含 finish_reason 的最后 chunk JSON
        string? finalReason = null;
        bool holding = false;
        bool sawDone = false;
        var chunk = new byte[8192];

        // E-8：byte[] + 已处理偏移游标（替代 List<byte> + RemoveRange）：
        // 游标只前移不搬字节；已处理量超 64KB 时一次性 Array.Copy 压实、偏移归零。
        var buf = new byte[65536];
        int len = 0;          // buf 中总字节数
        int lineStart = 0;    // 当前未处理行的起始偏移
        int scanFrom = 0;     // 下一轮扫描起点（上次扫描到的位置）
        try
        {
            while (true)
            {
                // AH-3：流式读 idle 超时看门狗——后端假死（长期无字节）时不再无限挂起。
                // 每轮重建 CTS：读到数据即重置计时；超时抛 TimeoutException 由上层记录告警并断开（不触发自动崩溃恢复，避免误杀长思考）。
                using var readCts = new CancellationTokenSource(BackendIdleTimeoutMs);
                int n;
                try
                {
                    n = await stream.ReadAsync(chunk, readCts.Token);
                }
                catch (OperationCanceledException) when (!readCts.IsCancellationRequested)
                {
                    throw; // 外部取消（非本看门狗超时），原样上抛
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException("后端流式响应空闲超时（疑似后端假死，长期无数据）。");
                }
            if (n <= 0) break;
            if (len + n > buf.Length)
            {
                var nb = new byte[Math.Max(buf.Length * 2, len + n)];
                Array.Copy(buf, nb, len);
                buf = nb;
            }
            Array.Copy(chunk, 0, buf, len, n);
            len += n;
            // 单遍扫描：只从上次扫描位置继续找换行
            for (int i = scanFrom; i < len; i++)
            {
                if (buf[i] != (byte)'\n') continue;
                var line = DecodeLine(buf, lineStart, i);
                await HandleSseLineAsync(line);
                lineStart = i + 1;
            }
            scanFrom = len; // 已扫到末尾，下轮从新追加的字节继续
            // 压实：已处理量超 64KB → 未处理尾部一次性搬到头部、偏移归零（避免长期运行 buf 无限增长）
            if (lineStart > HeldLineMax)
            {
                int remaining = len - lineStart;
                Array.Copy(buf, lineStart, buf, 0, remaining);
                len = remaining;
                lineStart = 0;
                scanFrom = len;
            }
        }
        if (len > lineStart)
            await HandleSseLineAsync(DecodeLine(buf, lineStart, len));

        /// <summary>写一行到客户端；有门控时先取锁（与并发 keep-alive 互斥，防 SSE 行交错）。</summary>
        async Task ForwardAsync(string line)
        {
            // SSE 规范：每个事件以空行（\n\n）结束。llama-server 原始流为 "data: {...}\n\n"，
            // 按行拆分后空行丢失，必须在此补回，否则连续 data 行会被客户端拼成同一事件，
            // pi-ai 严格 JSON.parse 报 "Unexpected non-whitespace character after JSON"。
            var bytes = Encoding.UTF8.GetBytes(line + "\n\n");
            if (writeGate != null) await writeGate.WaitAsync();
            try
            {
                await outResp.OutputStream.WriteAsync(bytes);
                await outResp.OutputStream.FlushAsync();
            }
            finally
            {
                if (writeGate != null) writeGate.Release();
            }
        }

        async Task HandleSseLineAsync(string line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (holding) held.Add(line + "\n"); else await ForwardAsync(line);
                return;
            }
            var payload = line.Substring(5).Trim();
            if (payload == "[DONE]")
            {
                sawDone = true;
                if (holding) held.Add(line + "\n"); else await ForwardAsync(line);
                return;
            }
            JsonObject? obj = null;
            try { obj = JsonNode.Parse(payload)?.AsObject(); } catch { }
            if (obj != null)
            {
                var choice = (obj["choices"] as JsonArray)?.FirstOrDefault()?.AsObject();
                var delta = choice?["delta"]?.AsObject();
                // AH-2：content 可为数组（多模态 [{type:...}]），GetValue<string> 抛异常会中断整条 SSE 流；类型防护 + 数组序列化累积
                string? content = null;
                var contentNode = delta?["content"];
                if (contentNode is JsonValue cv && cv.TryGetValue<string>(out var s)) content = s;
                else if (contentNode is JsonArray ca) content = ca.ToJsonString();
                if (!string.IsNullOrEmpty(content)) state.Accumulated.Append(content);
                if (delta?["tool_calls"] != null) state.HasToolCalls = true;
                var fr = choice?["finish_reason"]?.GetValue<string>();
                if (fr != null)
                {
                    holding = true;
                    finalPayload = payload;
                    finalReason = fr;
                    return; // 暂扣：本轮结束后决策
                }
                var usage = obj["usage"];
                if (usage != null)
                {
                    try
                    {
                        state.PromptTokens += usage["prompt_tokens"]?.GetValue<int>() ?? 0;
                        state.CompletionTokens += usage["completion_tokens"]?.GetValue<int>() ?? 0;
                        state.HasUsage = true;
                    }
                    catch { }
                }
            }
            await ForwardAsync(line);
        }

        // ── 流结束决策 ──
        if (holding && finalPayload != null)
        {
            bool doContinue = finalReason == "length" && allowContinue && !state.HasToolCalls;
            var finalObj = JsonNode.Parse(finalPayload)?.AsObject();
            if (finalObj != null)
            {
                var ch = (finalObj["choices"] as JsonArray)?.FirstOrDefault()?.AsObject();
                if (doContinue)
                {
                    // 剥离 finish_reason：客户端继续等待下一轮流
                    if (ch != null) ch["finish_reason"] = JsonValue.Create<string>(null);
                }
                else
                {
                    // 归一化：强制 stop + 合并跨轮 usage
                    if (ch != null) ch["finish_reason"] = "stop";
                    if (state.HasUsage)
                    {
                        finalObj["usage"] = new JsonObject
                        {
                            ["prompt_tokens"] = state.PromptTokens,
                            ["completion_tokens"] = state.CompletionTokens,
                            ["total_tokens"] = state.PromptTokens + state.CompletionTokens,
                        };
                    }
                }
                finalPayload = finalObj.ToJsonString();
            }
            // 末块事件必须显式以空行（\n\n）结束（SSE 规范）。
            // 续接分支下本轮的空行/[DONE] 被抑制不写，若末块只带单个 \n，下一轮首个 data 会与末块
            // 拼成同一事件 → 客户端把两段 JSON 连读，报 "Unexpected non-whitespace character after JSON"。
            var finalBytes = Encoding.UTF8.GetBytes("data: " + finalPayload + "\n\n");
            if (writeGate != null) await writeGate.WaitAsync();
            try
            {
                await outResp.OutputStream.WriteAsync(finalBytes);
                await outResp.OutputStream.FlushAsync();
            }
            finally
            {
                if (writeGate != null) writeGate.Release();
            }
            // P0：暂扣行（含 [DONE]）只在真正末轮写出——续接分支若泄漏 [DONE]，客户端会判定流结束而断开连接，
            // 后续轮输出永远不可见，续接链路被毁。续接时丢弃本轮 [DONE]，留给真正末轮。
            if (!doContinue)
            {
                // 整体持锁写入，防 keep-alive 在末块与 [DONE] 之间插入
                if (writeGate != null) await writeGate.WaitAsync();
                try
                {
                    foreach (var h in held)
                    {
                        // held 行原始为 "line\n"（单换行），SSE 规范要求事件以 \n\n 结束，补一个 \n
                        var bytes = Encoding.UTF8.GetBytes(h + "\n");
                        await outResp.OutputStream.WriteAsync(bytes);
                    }
                    await outResp.OutputStream.FlushAsync();
                }
                finally
                {
                    if (writeGate != null) writeGate.Release();
                }
            }
            return doContinue ? RoundOutcome.Truncated : RoundOutcome.Normal;
        }

        // 无 finish_reason chunk：见过 [DONE] = 正常结束；否则 = 流中断（崩溃迹象）
        return sawDone ? RoundOutcome.Normal : RoundOutcome.Aborted;
        }
        finally
        {
            // AH-4 修复：超时/异常路径下确保关闭响应，防止客户端连接泄漏
            try { outResp.Close(); } catch { /* 已关闭或异常时忽略 */ }
        }
    }

    /// <summary>非流式续接：读完整 JSON 响应；finish_reason=length 时循环续接；末轮归一化 finish_reason=stop + 合并 usage。</summary>
    /// <returns>(Completed, Accumulated)：Completed=false 表示 bad_alloc 错误（恢复启用时不转发错误体，交给崩溃恢复）。</returns>
    public static async Task<(bool Completed, string Accumulated)> HandleNonStreamAsync(
        IBackendClient backend, Uri uri, string originalBody,
        HttpResponseMessage firstResp, HttpListenerResponse outResp,
        AppConfig cfg, Action<string>? log, bool crashRecoveryEnabled)
    {
        var state = new SseState();
        string body = Encoding.UTF8.GetString(await firstResp.Content.ReadAsByteArrayAsync());
        int round = 0;

        // bad_alloc 错误响应：恢复启用 → 不转发，交给崩溃恢复；否则原样透传
        if (firstResp.StatusCode >= System.Net.HttpStatusCode.InternalServerError
            && (body.Contains("bad allocation", StringComparison.OrdinalIgnoreCase)
                || CrashRecovery.WasBadAlloc(TimeSpan.FromSeconds(60))))
        {
            if (crashRecoveryEnabled) return (false, "");
            await WriteJsonToClient(outResp, body);
            return (true, "");
        }

        while (true)
        {
            bool allowContinue = cfg.ContinuationEnabled && round < cfg.MaxContinuations;
            if (!ParseNonStream(body, state)) break; // 解析失败：原样转发
            if (state.FinishReason != "length" || state.HasToolCalls || !allowContinue) break;

            var nextBody = BuildContinuationBody(originalBody, state.Accumulated.ToString());
            if (nextBody == null) break;
            originalBody = nextBody;

            int budget = cfg.GetInputBudget();
            var (ok, guarded, note) = await TokenGuard.GuardAsync(backend, originalBody, budget);
            if (!ok) { log?.Invoke($"续接中止：{note}"); break; }
            if (guarded != null && guarded != originalBody) originalBody = guarded;
            if (note != null) log?.Invoke(note);

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, uri)
                {
                    Content = new StringContent(originalBody, Encoding.UTF8, "application/json"),
                };
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(cfg.ContinuationTimeoutSeconds));
                using var r2 = await backend.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                body = Encoding.UTF8.GetString(await r2.Content.ReadAsByteArrayAsync(cts.Token));
                round++;
                log?.Invoke($"续接触发（第 {round} 轮）：输出截断，自动续接…");
            }
            catch (OperationCanceledException)
            {
                // A8 修复：续接请求取消/超时（非崩溃）——以当前已生成内容收尾（break 后归一化转发），不误判崩溃
                log?.Invoke($"续接请求取消（第 {round + 1} 轮）：超时或连接中止，以当前结果返回。");
                break;
            }
            catch (Exception ex)
            {
                log?.Invoke($"续接请求异常（第 {round + 1} 轮）：{ex.Message}，返回已生成内容。");
                break;
            }
        }

        // 归一化：finish_reason=stop + 合并 usage
        try
        {
            var root = JsonNode.Parse(body)?.AsObject();
            if (root != null)
            {
                var ch = (root["choices"] as JsonArray)?.FirstOrDefault()?.AsObject();
                if (ch != null) ch["finish_reason"] = "stop";
                if (state.HasUsage)
                {
                    root["usage"] = new JsonObject
                    {
                        ["prompt_tokens"] = state.PromptTokens,
                        ["completion_tokens"] = state.CompletionTokens,
                        ["total_tokens"] = state.PromptTokens + state.CompletionTokens,
                    };
                }
                body = root.ToJsonString();
            }
        }
        catch { /* 改写失败：原样转发 */ }

        await WriteJsonToClient(outResp, body);
        return (true, state.Accumulated.ToString());
    }

    /// <summary>写 JSON 响应到客户端。</summary>
    private static async Task WriteJsonToClient(HttpListenerResponse outResp, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        outResp.ContentType = "application/json";
        outResp.ContentLength64 = bytes.Length;
        await outResp.OutputStream.WriteAsync(bytes);
    }

    /// <summary>解析非流式响应：累积 content/usage/finish_reason。解析失败返回 false。</summary>
    private static bool ParseNonStream(string body, SseState state)
    {
        try
        {
            var root = JsonNode.Parse(body)?.AsObject();
            if (root == null) return false;
            var ch = (root["choices"] as JsonArray)?.FirstOrDefault()?.AsObject();
            if (ch != null)
            {
                var msg = ch["message"]?.AsObject();
                var content = msg?["content"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(content)) state.Accumulated.Append(content);
                if (msg?["tool_calls"] != null) state.HasToolCalls = true;
                state.FinishReason = ch["finish_reason"]?.GetValue<string>();
            }
            var usage = root["usage"];
            if (usage != null)
            {
                state.PromptTokens += usage["prompt_tokens"]?.GetValue<int>() ?? 0;
                state.CompletionTokens += usage["completion_tokens"]?.GetValue<int>() ?? 0;
                state.HasUsage = true;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>在原请求 messages 末尾追加 assistant 输出 + 续接指令。失败返回 null。</summary>
    public static string? BuildContinuationBody(string originalBody, string content)
    {
        try
        {
            var root = JsonNode.Parse(originalBody)?.AsObject();
            var msgs = root?["messages"] as JsonArray;
            if (root == null || msgs == null) return null;
            msgs.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
            msgs.Add(new JsonObject { ["role"] = "user", ["content"] = ContinuePrompt });
            return root.ToJsonString();
        }
        catch { return null; }
    }

    /// <summary>从字节缓冲取 [start, end) 区间解码为一行（去 \r）。E-8：直接区间解码，不再逐行 new byte[]。</summary>
    private static string DecodeLine(byte[] buf, int start, int end)
    {
        var s = Encoding.UTF8.GetString(buf, start, end - start);
        if (s.EndsWith("\r")) s = s.Substring(0, s.Length - 1);
        return s;
    }
}
