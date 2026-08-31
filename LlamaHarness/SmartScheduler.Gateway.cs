using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ThinkingModeHelper = LlamaHarness.ThinkingMode;

namespace LlamaHarness;

/// <summary>
/// 网关路由与槽位亲和（PrepareGatewayAsync/ApplySlotAffinityAsync/自动预取与快照前缀解析/前缀指纹日志）。partial 聚类方法体零改动。
/// </summary>
public partial class SmartScheduler
{
    /// <summary>网关预处理管道（仅推理请求）：
    /// 思考模式拦截 → 槽位亲和路由 + Tool 链锁定 + KV 驱逐 save / restore 自愈 → TokenGuard 裁剪 → 强制流式改写 → 前缀哈希可观测。
    /// 返回 (改写后 bodyBytes, finalBody, effStreaming, routedSlot, routedKey, root)；返回 null = TokenGuard 拒绝（已向客户端写 400）。</summary>
    private async Task<(byte[] BodyBytes, string FinalBody, bool EffStreaming, int? RoutedSlot, string? RoutedKey, JsonObject? Root)?> PrepareGatewayAsync(
        HttpListenerContext ctx, HttpListenerRequest req, string path, byte[] bodyBytes)
    {
        string p = req.Url?.AbsolutePath ?? "";
        bool isCompletions = p.Contains("completion", StringComparison.OrdinalIgnoreCase)
                             || p.Contains("embedding", StringComparison.OrdinalIgnoreCase);
        if (!isCompletions)
            return null; // 非推理请求：不做网关处理（finalBody=null 走纯透传管道）

        string body = System.Text.Encoding.UTF8.GetString(bodyBytes);

        // E-1/E-3：入口一次性解析 → 后续所有阶段复用同一棵 DOM，管道末端只序列化一次。
        // 解析失败（非法 JSON）→ root=null → 跳过全部 DOM 改写、原样透传（等价于旧实现各方法 try-catch 透传）。
        JsonObject? root = null;
        try { root = JsonNode.Parse(body)?.AsObject(); } catch { /* 非法 JSON */ }

        int? routedSlot = null;
        string? routedKey = null;

        // 思考模式拦截（仅 chat/completions）：识别指令 / 注入 reasoning_effort + enable_thinking / 校验修正非法档位
        if (RequestProcessor.IsChatCompletions(p) && root != null)
        {
            ThinkingLevel lvl, prev;
            bool changed;
            string? effortFix = null;
            lock (_thinkingGate)
            {
                prev = _thinkingMode;
                lvl = _thinkingMode;
                ThinkingModeHelper.InjectThinkingMode(root, ref lvl, out effortFix); // DOM 版：原地改树，不再 parse/serialize
                changed = lvl != prev;
                _thinkingMode = lvl;
            }
            if (changed)
            {
                Log?.Invoke($"思考模式已切换为「{ThinkingModeHelper.LabelOf(lvl)}」（{(ThinkingModeHelper.EffortOf(lvl) is var e && e != null ? $"reasoning_effort={e}, " : "")}enable_thinking={(lvl == ThinkingLevel.Off ? "false" : "true")}）。");
                ThinkingModeChanged?.Invoke(lvl);
            }
            if (effortFix != null)
                Log?.Invoke($"思考参数清洗：{effortFix}。");
        }

        // 槽位亲和路由（单槽/多槽均启用）：指纹绑定 + 注入 n_slots 固定槽位；槽忙时 llama.cpp 原生排队，不跨槽漂移
        var aff = _affinity;
        bool didKvRestore = false;
        if (aff != null && p.Contains("completion", StringComparison.OrdinalIgnoreCase))
        {
            (routedSlot, routedKey, didKvRestore) = await ApplySlotAffinityAsync(req, aff, root);
        }

        // Token Guard（仅 chat/completions）：计量 + 裁剪，防 "exceeds context size" 400
        // MeasureAsync：每次调用强制输出 [TOKEN-GUARD] 计量日志（消除排查盲区），再执行裁剪
        // KV restore 后强制重跑校验：saved_n 残留 + 新 prompt 叠加可能击穿窗口（本次故障根因之一）
        if (RequestProcessor.IsChatCompletions(p) && _cfg.TokenGuardEnabled && root != null)
        {
            var budget = _cfg.GetInputBudget(); // 多槽均分总容量：CtxSize ÷ Parallel − 输出预留 − Prompt头部开销预留
            var (ok, _, note) = await TokenGuard.MeasureAsync(root, _hc, _backendPort, budget, _cfg.ReservedOutputTokens, _cfg.ReservedPromptOverhead);
            if (!ok)
            {
                Log?.Invoke($"Token Guard 拒绝：{note}");
                RequestProcessor.WriteError(ctx, 400, note ?? "上下文超长");
                return null;
            }
            if (note != null) Log?.Invoke(note);
            if (didKvRestore)
            {
                Log?.Invoke("[TOKEN-GUARD] KV restore 后重跑校验通过（saved_n 残留 + 新 prompt 未超预算）");
                // restore 命中 = 快照已加载到槽位：标记新鲜，避免本轮完成后立即冗余重存
                if (routedKey != null) lock (_kvStateGate) _freshSnapshotKeys.Add(routedKey);
            }
        }

        // 非流式请求检测 + 可选强制流式改写：
        // 非流式时 llama-server 会缓存整个响应直到生成完毕才返回，期间无任何字节流动，
        // 客户端读超时→断开→agent 重试全量上下文→重新预填。流式则边生成边发字节，不会读超时。
        bool streaming;
        if (root != null)
        {
            // DOM 直读替代对数 MB body 的正则扫描（E-1）
            streaming = false;
            try { if (root["stream"]?.GetValue<bool>() == true) streaming = true; } catch { /* 非 bool 值：按 false */ }
        }
        else
            streaming = System.Text.RegularExpressions.Regex.IsMatch(body, @"""stream""\s*:\s*true");

        if (!streaming)
        {
            if (_cfg.ForceStream)
            {
                if (root != null)
                {
                    RequestProcessor.EnsureStreamTrue(root); // DOM 版：直接树上置 stream=true
                    Log?.Invoke("强制流式：已将非流式请求改写为 stream=true（SSE 直通）。");
                }
                else
                {
                    // C-005 降级：非法 JSON 走字符串级改写；改写失败透传原始请求，禁止下发损坏 JSON
                    var rewritten = RequestProcessor.EnsureStreamTrue(body);
                    if (rewritten != null)
                        bodyBytes = System.Text.Encoding.UTF8.GetBytes(rewritten);
                    Log?.Invoke("警告：强制流式改写失败（请求体不是合法 JSON），已透传原始请求。");
                }
            }
            else
            {
                WarnNonStreamOnce();
            }
        }

        // §8 可观测：前缀哈希 HIT/MISS 判定（原生 KV 前缀复用；TokenGuard 之后按实际下发体计算）
        if (routedKey != null)
        {
            bool? wrapperHit = LogPrefixHash(routedKey, root);
            // 3.1：入队 restore 判定上下文（仅该 key 存在快照时；FIFO + TTL 防错位，prompt eval 行到达时弹最旧条目判定）
            var rs = _restoreStats;
            var kvc = _kvCache;
            if (rs != null && routedSlot is int rsSlot && kvc != null && kvc.HasCache(routedKey))
                rs.RecordRequest(routedKey, rsSlot, wrapperHit ?? false, kvc.SavedTokens(routedKey));
        }

        // 管道末端：唯一一次序列化 + 编码转换（E-1/E-3）
        if (root != null)
        {
            body = root.ToJsonString();
            bodyBytes = System.Text.Encoding.UTF8.GetBytes(body);
        }

        return (bodyBytes, body, streaming || _cfg.ForceStream, routedSlot, routedKey, root);
    }

    /// <summary>槽位亲和阶段：指纹绑定（LRU 驱逐 / §4.2 自动强占）→ §4.5 Tool 链锁定 → 驱逐前 KV save → restore 自愈 → n_slots 注入。
    /// E-1：直接操作调用方持有的同一棵 DOM（root=null 时跳过 DOM 步骤，等价旧实现 parse 失败透传）。
    /// 返回（路由槽位、绑定 key、是否执行了 KV restore——restore 后需重跑 TokenGuard 校验）。</summary>
    private async Task<(int? RoutedSlot, string? RoutedKey, bool DidRestore)> ApplySlotAffinityAsync(
        HttpListenerRequest req, SlotAffinity aff, JsonObject? root)
    {
        // §4.2 自动冻结：应用类型前缀在 AutoPreemptiveApps → 绑定强制强占（暂停 LRU 驱逐）
        var autoPre = ParseAutoPreemptivePrefixes();
        var (slot, key, isNew, evicted, evictedSlot, evictedKvCache) = aff.GetSlot(req.Headers, autoPre);
        int? routedSlot = slot;
        string? routedKey = key;

        // §4.5 Tool 链会话锁定：末条消息 role=tool → agent 工具循环进行中 → 锁槽位防驱逐；循环结束自动解锁
        if (key != null && root != null)
        {
            bool inToolLoop = RequestProcessor.DetectToolLoop(root);
            bool didLock = false, didUnlock = false;
            // O-15：锁内只做 _toolLockedKeys 集合判定；aff 调用（自带内部锁 + 文件 I/O）全部移出，消除锁嵌套
            bool alreadyPreemptive = aff.IsPreemptive(key);
            lock (_kvStateGate)
            {
                if (inToolLoop)
                {
                    if (!_toolLockedKeys.Contains(key) && !alreadyPreemptive)
                    {
                        _toolLockedKeys.Add(key);
                        didLock = true;
                    }
                }
                else if (_toolLockedKeys.Remove(key))
                {
                    didUnlock = true;
                }
            }
            if (didLock)
            {
                aff.MarkToolLocked(key); // 标记到 SlotAffinity（驱逐优先级：Tool 锁定 > 手动/自动强占）
                aff.SetPreemptive(key, true); // 移出锁外（O-15）
                EmitSlot($"[KV-LOCK] Tool 链会话锁定：{key} → slot{slot}（强占，不驱逐）");
            }
            else if (didUnlock)
            {
                aff.UnmarkToolLocked(key);
                aff.SetPreemptive(key, false);
                EmitSlot($"[KV-UNLOCK] Tool 链结束，解除锁定：{key}");
            }
        }

        var kv = _kvCache;

        // KV Cache：驱逐前 save（仅当被驱逐者的 KvCache=true；evicted != null 已蕴含 evictedSlot 有效，SlotAffinity 仅驱逐时置位）
        if (evicted != null && kv != null && evictedKvCache)
        {
            try
            {
                var saveTask = kv.SaveAsync(evictedSlot, evicted);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await saveTask;
                EmitSlot($"KV Cache 保存：{evicted} → slot{evictedSlot}（{sw.Elapsed.TotalSeconds:F1}s）");
            }
            catch (Exception ex)
            {
                EmitSlot($"KV Cache 保存失败：{evicted}（{ex.Message}），降级为全量 prefill。");
            }
        }
        else if (evicted != null && !evictedKvCache)
        {
            EmitSlot($"驱逐 {evicted}（KV Cache 已关闭，不保存）");
        }

        // KV Cache：restore（两种触发：① isNew 重绑定；② 进程重启后该 key 首次使用——休眠唤醒 KV 自愈。
        // 无论是否命中 restore，都把 key 记入 _servedKeysThisRun：本进程服务过即不再 restore，防误用磁盘旧快照回退内存新状态）
        bool didRestore = false;
        if (key != null)
        {
            bool firstUseThisRun;
            lock (_kvStateGate) firstUseThisRun = _servedKeysThisRun.Add(key);
            if (kv != null && kv.HasCache(key) && (isNew || firstUseThisRun))
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    bool ok = await kv.RestoreAsync(slot, key);
                    if (ok)
                    {
                        EmitSlot($"[KV-RESTORE] KV Cache 恢复：{key} → slot{slot}（{sw.Elapsed.TotalSeconds:F1}s，跳过全量 prefill）");
                        // §8：restore 后重建前缀哈希基线（旧哈希对应驱逐前状态，避免下次请求误报 MISS）
                        lock (_kvStateGate) _prefixHashes.Remove(key);
                        didRestore = true; // restore 成功：标记需重跑 TokenGuard（saved_n 残留 + 新 prompt 叠加可能击穿窗口）
                    }
                    else
                    {
                        EmitSlot($"KV Cache 恢复失败：{key}（槽位可能忙），降级为全量 prefill。");
                    }
                }
                catch (Exception ex)
                {
                    EmitSlot($"KV Cache 恢复异常：{key}（{ex.Message}），降级为全量 prefill。");
                }
            }
        }

        if (isNew)
        {
            var evt = $"槽位绑定：{key} → slot{slot}{(evicted != null ? $"（驱逐 {evicted}）" : "")}";
            EmitSlot(evt);
            SlotBindingChanged?.Invoke();
        }
        // E-1：n_slots 注入直接改树（已有 n_slots 时不覆盖，尊重客户端显式指定）
        if (root != null)
            ThinkingModeHelper.InjectNSlots(root, slot);
        return (routedSlot, routedKey, didRestore);
    }

    /// <summary>解析 AutoPreemptiveApps 配置为前缀集合（§4.2 自动冻结）。</summary>
    private List<string> ParseAutoPreemptivePrefixes()
    {
        return _cfg.AutoPreemptiveApps.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    /// <summary>判定亲和 key 是否匹配任一自动强占前缀（§4.2 槽位冻结语义，public 供测试）。</summary>
    public bool IsAutoPreKey(string key)
    {
        return ParseAutoPreemptivePrefixes().Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>解析 AutoSnapshotKeys 配置为前缀集合（仅快照持久化：首请求存档 + Warming eager restore，不锁槽）。</summary>
    private List<string> ParseAutoSnapshotPrefixes()
    {
        return _cfg.AutoSnapshotKeys.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    /// <summary>判定亲和 key 是否匹配任一自动快照前缀（1.1 首请求存档 / 3.2 Warming eager restore 条件；不参与强占/驱逐拒绝，public 供测试）。</summary>
    public bool IsAutoSnapshotKey(string key)
    {
        return ParseAutoSnapshotPrefixes().Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>§8 可观测：前缀哈希 HIT/MISS 判定。一致 → 原生 KV 前缀复用（增量 prefill）；不一致 → 全量重算。
    /// 返回 wrapper 指纹判定结果（true=HIT / false=MISS / null=无指纹数据），供 3.1 RestoreStats FIFO 归属。
    /// KV-MISS 条件式日志：
    /// - HitByDelta（上一轮 restore 命中 + 增量 prefill）→ [KV-MISS-DEBUG]（降级，agent 每轮 messages 必变是设计预期）；
    /// - FullPrefill/MidRange（真实全量重算）或无判定数据 → [KV-MISS]（保留 INFO，用于快照损坏等故障排查）。
    /// Metrics 埋点不受影响：RestoreStats.OnPromptEval 持续统计 false_miss。</summary>
    private bool? LogPrefixHash(string key, JsonObject? root)
    {
        var hash = root != null ? RequestProcessor.PrefixHash(root) : null;
        if (hash == null) return null;
        lock (_kvStateGate)
        {
            if (_prefixHashes.TryGetValue(key, out var prev))
            {
                bool hit = prev == hash;
                if (hit)
                    Log?.Invoke($"[KV-HIT] {key}：前缀未变 → 原生 KV 复用（增量 prefill）");
                else
                {
                    // MISS 分支：区分 HitByDelta 虚假 MISS vs 真实 MISS
                    var lj = _restoreStats?.LastJudgeResult;
                    if (lj != null && lj.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && lj.Reason == "HitByDelta")
                        Log?.Invoke($"[KV-MISS-DEBUG] {key}：消息指纹变更，HitByDelta 增量复用，增量 prefill={lj.PromptEvalTokens} tokens");
                    else
                        Log?.Invoke($"[KV-MISS] {key}：前缀变更 → 全量重算");
                }
                _prefixHashes[key] = hash;
                return hit;
            }
            _prefixHashes[key] = hash;
            return null; // 该 key 首次请求：无历史指纹可比
        }
    }
}