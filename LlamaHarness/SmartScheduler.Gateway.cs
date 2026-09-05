using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
    private async Task<(byte[] BodyBytes, string FinalBody, bool EffStreaming, int? RoutedSlot, string? RoutedKey, JsonObject? Root, string? AgentRole)?> PrepareGatewayAsync(
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

        // v2.29：子代理身份标记识别与剥离（在所有网关预处理之前，确保标记不进入 TokenGuard 计量和模型上下文）
        string? agentRole = null;
        if (root != null)
        {
            agentRole = DetectAndStripAgentTag(root);
            if (agentRole != null)
                Log?.Invoke($"[AGENT-TAG] 识别到{agentRole}代理请求，已剥离 AGENT_TAG 标记（不污染模型上下文）。");
        }

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

            // P2 修复项 3：剥离客户端 messages 里携带的 <thinking>...</thinking> 标签
            // —— Qwen3/DashScope 客户端在思考轮次会把上轮的 <thinking> 输出回填到 assistant.content 里，
            // 逐轮累积会把 prompt 撑到 context window 上限；半截标签（跨行/嵌套/流式中断）也会污染 token 预算。
            // 使用正则做闭合标签匹配（非贪婪 + 跨行 + 忽略大小写），覆盖跨行/嵌套（最外对）/半截开头三种场景。
            if (StripThinkingTags(root))
                Log?.Invoke("[THINKING] 已从 messages 中剥离客户端携带的 <thinking> 标签（防跨轮累积撑爆 context window）。");
        }

        // 槽位亲和路由（单槽/多槽均启用）：指纹绑定 + 注入 n_slots 固定槽位；槽忙时 llama.cpp 原生排队，不跨槽漂移
        var aff = _affinity;
        bool didKvRestore = false;
        if (aff != null && p.Contains("completion", StringComparison.OrdinalIgnoreCase))
        {
            (routedSlot, routedKey, didKvRestore) = await ApplySlotAffinityAsync(req, aff, root, agentRole);
        }

        // Token Guard（仅 chat/completions）：计量 + 裁剪，防 "exceeds context size" 400
        // MeasureAsync：每次调用强制输出 [TOKEN-GUARD] 计量日志（消除排查盲区），再执行裁剪
        // KV restore 后强制重跑校验：saved_n 残留 + 新 prompt 叠加可能击穿窗口（本次故障根因之一）
        if (RequestProcessor.IsChatCompletions(p) && _cfg.TokenGuardEnabled && root != null)
        {
            var budget = _cfg.GetInputBudget(); // 多槽均分总容量：CtxSize ÷ Parallel − 输出预留 − Prompt头部开销预留
            var (ok, _, note) = await TokenGuard.MeasureAsync(root, Backend, budget, _cfg.ReservedOutputTokens, _cfg.ReservedPromptOverhead);
            if (!ok)
            {
                Log?.Invoke($"Token Guard 拒绝：{note}");
                // P2 修复项 7：Token Guard 拒绝统一走 WriteErrorV2 嵌套错误格式 {"error":{"code":GatewayErrorCodes.ContextOverflow,"message":"..."}}
                WriteErrorV2(ctx, 400, GatewayErrorCodes.ContextOverflow, note ?? "上下文超长");
                return null;
            }
            if (note != null) Log?.Invoke(note);
            if (didKvRestore)
            {
                Log?.Invoke("[TOKEN-GUARD] KV restore 后重跑校验通过（saved_n 残留 + 新 prompt 未超预算）");
                // v2.23.11（fix）：不再在 restore 命中时标记"新鲜"——restore 时磁盘快照本就滞后于 RAM（restore 的是旧版本），
                // 标记新鲜会导致条件式后台 save 永不触发，长全量 prefill 后快照不落盘（实测 saved_n 卡在首存档值）。
                // 本轮完成后由条件式 save 落盘最新 KV（后台异步不阻塞，_inflightSaves 按 key 去重）。
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
                    // P1-H-02 修复：正则改写后增加二次验证，确保改写后的 body 仍为合法 JSON
                    var rewritten = RequestProcessor.EnsureStreamTrue(body);
                    if (rewritten != null)
                    {
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(rewritten);
                            bodyBytes = System.Text.Encoding.UTF8.GetBytes(rewritten);
                            Log?.Invoke("警告：强制流式改写成功（正则降级路径），已验证 JSON 合法性。");
                        }
                        catch
                        {
                            // 改写后仍非合法 JSON：透传原始请求，禁止下发损坏数据
                            Log?.Invoke($"警告：强制流式正则改写后仍非合法 JSON，透传原始请求。body={body.Length}");
                        }
                    }
                    else
                    {
                        Log?.Invoke("警告：强制流式改写失败（请求体不是合法 JSON），已透传原始请求。");
                    }
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

        return (bodyBytes, body, streaming || _cfg.ForceStream, routedSlot, routedKey, root, agentRole);
    }

    /// <summary>槽位亲和阶段：指纹绑定（LRU 驱逐 / §4.2 自动强占）→ §4.5 Tool 链锁定 → 驱逐前 KV save → restore 自愈 → n_slots 注入。
    /// E-1：直接操作调用方持有的同一棵 DOM（root=null 时跳过 DOM 步骤，等价旧实现 parse 失败透传）。
    /// 返回（路由槽位、绑定 key、是否执行了 KV restore——restore 后需重跑 TokenGuard 校验）。</summary>
    private async Task<(int? RoutedSlot, string? RoutedKey, bool DidRestore)> ApplySlotAffinityAsync(
        HttpListenerRequest req, SlotAffinity aff, JsonObject? root, string? agentRole)
    {
        // §4.2 自动冻结：应用类型前缀在 AutoPreemptiveApps → 绑定强制强占（暂停 LRU 驱逐）
        var autoPre = ParseAutoPreemptivePrefixes();
        // v2.30：主从槽位隔离——secondary 请求跳过 PrimarySlotIndex（预留槽位），并使用独立亲和 key 避免复用主代理绑定
        bool isSecondary = agentRole != null && agentRole.Equals("sub", StringComparison.OrdinalIgnoreCase);
        int reservedSlot = (isSecondary && _cfg.SlotMode == SlotModeType.DualPrimarySecondary) ? _cfg.PrimarySlotIndex : -1;
        string? keySuffix = (isSecondary && _cfg.SlotMode == SlotModeType.DualPrimarySecondary) ? "_sub" : null;
        var (slot, key, isNew, evicted, evictedSlot, evictedKvCache) = aff.GetSlot(req.Headers, autoPre, reservedSlot, keySuffix);
        int? routedSlot = slot;
        string? routedKey = key;

        // ① §4.5 Tool 链会话锁定：末条消息 role=tool → 锁槽位防驱逐；循环结束自动解锁
        HandleToolLoopLock(aff, root, key, slot);

        // v2.30：子代理自动关闭强占——secondary 请求强制 preempt=false，任务完成后立即释放槽位
        if (isSecondary && _cfg.SecondaryAutoDisablePreempt && key != null && aff.IsPreemptive(key))
        {
            aff.SetPreemptive(key, false);
            Log?.Invoke($"[AGENT-TAG] secondary 请求自动关闭强占：{key} → slot{slot}（任务完成后立即释放）");
        }

        var kv = _kvCache;

        // ② KV Cache 生命周期：驱逐前 save（仅被驱逐者 KvCache=true）→ restore 自愈（isNew 重绑定 / 进程重启后首次使用）
        bool didRestore = await HandleEvictAndRestoreAsync(kv, evicted, evictedSlot, evictedKvCache, key, slot, isNew);

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

    /// <summary>§4.5 Tool 链会话锁定（ApplySlotAffinityAsync 子段①）：末条消息 role=tool →
    /// 锁槽位防驱逐（强占），循环结束自动解锁。O-15：锁内只做 _toolLockedKeys 集合判定；
    /// aff 调用（自带内部锁 + 文件 I/O）全部移出，消除锁嵌套。</summary>
    private void HandleToolLoopLock(SlotAffinity aff, JsonObject? root, string? key, int slot)
    {
        if (key == null || root == null) return;
        bool inToolLoop = RequestProcessor.DetectToolLoop(root);
        // v2.23.7：单槽位（parallel=1 → cap=slotCount-1=0）跳过 Tool 链加锁——SetPreemptive(true)
        // 会独占唯一槽位，违反"至少 1 槽给非强占新任务"不变量，其他 key 任务最长排队 30s（实测）；
        // 单槽位无多槽驱逐竞争，锁定无保护意义。解锁分支保留以清理可能的残留锁定。
        if (aff.ShouldSkipToolLoopLock()) inToolLoop = false;
        bool didLock = false, didUnlock = false;
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

    /// <summary>KV Cache 生命周期（ApplySlotAffinityAsync 子段②）：驱逐前 save（仅被驱逐者 KvCache=true；
    /// evicted != null 已蕴含 evictedSlot 有效，SlotAffinity 仅驱逐时置位）→ restore 自愈
    /// （① isNew 重绑定；② 进程重启后该 key 首次使用——休眠唤醒 KV 自愈）。
    /// 无论是否命中 restore，都把 key 记入 _servedKeysThisRun：本进程服务过即不再 restore，防误用磁盘旧快照回退内存新状态。
    /// 返回是否执行了 KV restore（restore 后需重跑 TokenGuard 校验）。</summary>
    private async Task<bool> HandleEvictAndRestoreAsync(KvCacheManager? kv, string? evicted, int evictedSlot, bool evictedKvCache, string? key, int slot, bool isNew)
    {
        // 驱逐前 save（仅当被驱逐者的 KvCache=true）
        if (evicted != null && kv != null && evictedKvCache)
        {
            try
            {
                var saveTask = kv.SaveAsync(evictedSlot, evicted);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await saveTask;
                EmitSlot($"KV Cache 保存：{evicted} → slot{evictedSlot}（{sw.Elapsed.TotalSeconds:F1}s）");
                KvEvents.Record(new PerfEvent("kv", "save", sw.Elapsed.TotalMilliseconds, evicted)); // v2.22 可观测
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

        // restore
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
                    KvEvents.Record(new PerfEvent("kv", "restore", sw.Elapsed.TotalMilliseconds, key)); // v2.22 可观测
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
        return didRestore;
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

    /// <summary>判定亲和 key 是否匹配任一自动快照前缀（1.1 首请求存档 / 3.2 Warming eager restore 条件；不参与强占/驱逐拒绝，public 供测试）。
    /// v2.23.8：unknown_ 前缀（未知应用自动兜底）在 UnknownAppKvSnapshot 开启时视为自动快照——新应用无需配规则即可独立 KV 持久化。</summary>
    public bool IsAutoSnapshotKey(string key)
    {
        if (_cfg.UnknownAppKvSnapshot && key.StartsWith("unknown_", StringComparison.OrdinalIgnoreCase)) return true;
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
        // v2.23.11（P1-4）：改用分段指纹（system/tools/messages 三段落点），MISS 时精确定位哪一段漂移
        var hash = root != null ? RequestProcessor.SegmentPrefixHash(root) : null;
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
                    // MISS 分支：区分 HitByDelta 虚假 MISS vs 真实 MISS；P1-4 差异定位
                    var diff = RequestProcessor.DescribePrefixDrift(prev, hash);
                    if (!string.IsNullOrEmpty(diff)) _lastDriftDiff[key] = diff; // 供 [KV-DRIFT] 告警补充定位
                    var lj = _restoreStats?.LastJudgeResult;
                    if (lj != null && lj.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && lj.Reason == "HitByDelta")
                        Log?.Invoke($"[KV-MISS-DEBUG] {key}：消息指纹变更，HitByDelta 增量复用，增量 prefill={lj.PromptEvalTokens} tokens");
                    else
                        Log?.Invoke(string.IsNullOrEmpty(diff)
                            ? $"[KV-MISS] {key}：前缀变更 → 全量重算"
                            : $"[KV-MISS] {key}：前缀变更 → 全量重算（差异定位：{diff}）");
                }
                _prefixHashes[key] = hash;
                return hit;
            }
            _prefixHashes[key] = hash;
            return null; // 该 key 首次请求：无历史指纹可比
        }
    }

    /// <summary>P2 修复项 3：<thinking> 标签剥离（闭合对 + 半截开头兜底）。
    /// 正则语义：
    /// - 闭合对：(?is)&lt;thinking&gt;.*?&lt;/thinking&gt; —— 非贪婪 + 跨行 + 忽略大小写，
    ///   覆盖 &lt;thinking&gt; 跨行内容（多行推理痕迹）、以及嵌套的最外层闭合对（如 &lt;thinking&gt;A&lt;thinking&gt;B&lt;/thinking&gt;C&lt;/thinking&gt; 一次剥到最外对）。
    /// - 半截开头：(?s)&lt;thinking&gt;.*$ —— 客户端流式中断/网络丢包导致只发出开头未闭合的情况，剥到字符串末尾。
    /// 遍历 messages 数组中每条字符串 content（含 assistant/tool/system/user），原地改写 DOM。
    /// 返回 true 表示至少改动了 1 处，供调用方决定是否打点日志。
    /// 注意：仅处理纯字符串 content，不处理 multimodal content 数组（数组形式中每个 part 内的 text 字段也按字符串处理）。</summary>
    private bool StripThinkingTags(JsonObject root)
    {
        bool changed = false;
        if (!root.TryGetPropertyValue("messages", out var messagesNode) || messagesNode is not JsonArray messages)
            return false;

        // 编译期常量正则（Compile 提升重复执行性能；本次 pipeline 每请求跑一次，收益有限但成本零）
        var closedRe = sThinkClosed;
        var openRe = sThinkOpen;

        foreach (var item in messages)
        {
            if (item is not JsonObject msg) continue;

            // ① 简单字符串 content：直接替换（仅 JsonValue 走此分支；数组/对象型 content 走 ②，
            //    防 GetValue<string>() 对 JsonArray 抛 "The node must be of type 'JsonValue'"）
            if (msg.TryGetPropertyValue("content", out var contentNode)
                && contentNode is System.Text.Json.Nodes.JsonValue cv
                && cv.TryGetValue<string>(out var contentStr)
                && contentStr.Contains("<thinking", StringComparison.OrdinalIgnoreCase))
            {
                var stripped = closedRe.Replace(contentStr, "");
                // 若闭合对未匹配到任何一处（Length 相同），继续用半截开头正则兜底
                if (stripped.Length == contentStr.Length)
                    stripped = openRe.Replace(stripped, "");
                if (stripped != contentStr)
                {
                    msg["content"] = stripped;
                    changed = true;
                }
            }
            // ② multimodal content 数组：{"type":"text","text":"..."} 逐元素处理
            else if (msg.TryGetPropertyValue("content", out var contentArr) && contentArr is JsonArray parts)
            {
                foreach (var part in parts)
                {
                    if (part is not JsonObject pObj) continue;
                    if (!pObj.TryGetPropertyValue("text", out var textNode)
                        || textNode is not System.Text.Json.Nodes.JsonValue tv
                        || !tv.TryGetValue<string>(out var textStr))
                        continue;
                    if (!textStr.Contains("<thinking", StringComparison.OrdinalIgnoreCase)) continue;
                    var stripped = closedRe.Replace(textStr, "");
                    if (stripped.Length == textStr.Length)
                        stripped = openRe.Replace(stripped, "");
                    if (stripped != textStr)
                    {
                        pObj["text"] = stripped;
                        changed = true;
                    }
                }
            }
        }
        return changed;
    }


    /// <summary>v2.29：子代理身份标记识别与剥离。
    /// 扫描 messages：找到 role=user 且 content 以 "AGENT_TAG::" 开头的消息 → 识别身份（SUB/MAIN）→ 剥离标记行（不污染模型上下文）。
    /// 标记协议（Trae 全局规则注入）：主 agent 调用 Task 时在 query 首行插入 "AGENT_TAG::SUB::<task_id>"，
    /// 该 query 即子 agent 的第一条 user 消息，因此标记确定性地出现在子 agent 请求体中。
    /// 返回："sub" / "main" / null（无标记）。</summary>
    private static string? DetectAndStripAgentTag(JsonObject root)
    {
        if (!root.TryGetPropertyValue("messages", out var messagesNode) || messagesNode is not JsonArray messages)
            return null;

        foreach (var item in messages)
        {
            if (item is not JsonObject msg) continue;
            if (!msg.TryGetPropertyValue("role", out var roleNode)
                || roleNode is not System.Text.Json.Nodes.JsonValue rv
                || !rv.TryGetValue<string>(out var role)
                || !role.Equals("user", StringComparison.OrdinalIgnoreCase))
                continue;

            // ① 字符串 content：直接检查首行是否为标记
            if (msg.TryGetPropertyValue("content", out var contentNode)
                && contentNode is System.Text.Json.Nodes.JsonValue cv
                && cv.TryGetValue<string>(out var contentStr))
            {
                var m = sAgentTag.Match(contentStr);
                if (m.Success)
                {
                    var agentRole = m.Groups[1].Value.ToLowerInvariant();
                    // 剥离标记行 + 后面的空行（标记独占一行，规则要求标记后空一行再写内容）
                    var stripped = sAgentTag.Replace(contentStr, "", 1).TrimStart('\r', '\n');
                    msg["content"] = stripped;
                    return agentRole;
                }
            }
            // ② multimodal content 数组：{"type":"text","text":"..."} 逐元素检查
            else if (msg.TryGetPropertyValue("content", out var contentArr) && contentArr is JsonArray parts)
            {
                foreach (var part in parts)
                {
                    if (part is not JsonObject pObj) continue;
                    if (!pObj.TryGetPropertyValue("text", out var textNode)
                        || textNode is not System.Text.Json.Nodes.JsonValue tv
                        || !tv.TryGetValue<string>(out var textStr))
                        continue;
                    var m = sAgentTag.Match(textStr);
                    if (m.Success)
                    {
                        var agentRole = m.Groups[1].Value.ToLowerInvariant();
                        var stripped = sAgentTag.Replace(textStr, "", 1).TrimStart('\r', '\n');
                        pObj["text"] = stripped;
                        return agentRole;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>v2.29：子代理身份标记正则（编译期常量）。
    /// 匹配 "AGENT_TAG::SUB" 或 "AGENT_TAG::SUB::<task_id>"，独占一行（允许前后空白）。
    /// 捕获组 1 = 身份（SUB/MAIN），捕获组 2 = 可选任务 ID。</summary>
    private static readonly Regex sAgentTag = new(
        @"^\s*AGENT_TAG::(SUB|MAIN)(::[^\r\n]*)?\s*(\r?\n|$)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>P2 修复项 3：闭合 &lt;thinking&gt;...&lt;/thinking&gt; 标签正则（编译期常量，跨行 + 忽略大小写 + 非贪婪）。</summary>
    private static readonly Regex sThinkClosed = new(
        @"<thinking>.*?</thinking>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>P2 修复项 3：半截 &lt;thinking&gt;... 开头正则（客户端流式中断/网络丢包时剥到末尾）。</summary>
    private static readonly Regex sThinkOpen = new(
        @"<thinking>.*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
}