namespace LlamaHarness;

/// <summary>
/// 性能指标键注册表（v2.22）：与《docs/可观测体系设计方案.md》§3 指标分层注册表一一对应，
/// 是全项目指标命名的唯一权威。任何新增/修改指标必须同时改本类与注册表，禁止各处随意变体命名。
/// 分层：① KV 缓存（会话维度） ② 调度器（全局+槽） ③ llama-server 推理（task）
///       ④ 日志管道（全局） ⑤ 网关时延（请求级） ⑥ 系统资源（周期采样）
/// </summary>
public static class MetricKeys
{
    // ── ① KV 缓存指标（会话维度） ──
    /// <summary>HitByDelta 命中累计次数。</summary>
    public const string KvHitDelta = "kv_hit_delta";
    /// <summary>非预期 miss 累计次数（前缀无变更却 miss；真实前缀变更属正常 MISS 不计入）。</summary>
    public const string KvFalseMiss = "kv_false_miss";
    /// <summary>单次 eager/lazy restore 耗时（ms）。</summary>
    public const string KvRestoreMs = "kv_restore_ms";
    /// <summary>单次保存耗时（ms，首请求存档/休眠快照/条件式后台 save）。</summary>
    public const string KvSaveMs = "kv_save_ms";
    /// <summary>该会话最大 token 偏移量。</summary>
    public const string SavedN = "saved_n";
    /// <summary>全量 prefill 累计次数（前缀漂移观测，v2.23.10）。</summary>
    public const string KvFullPrefill = "kv_full_prefill";
    /// <summary>KV 复用累计 token 数（ROI，v2.23.11）。</summary>
    public const string KvReuseTokens = "kv_reuse_tokens";
    /// <summary>KV 复用累计节省 prefill 时间 ms（ROI，v2.23.11）。</summary>
    public const string KvReuseSavedMs = "kv_reuse_saved_ms";

    // ── ② 调度器指标（全局 + 槽维度） ──
    /// <summary>槽路由选择总耗时（ms，GetSlot 从进入排队到分配完成）。</summary>
    public const string SlotSelectMs = "slot_select_ms";
    /// <summary>驱逐事件累计次数。</summary>
    public const string EvictCount = "evict_count";
    /// <summary>服务完整唤醒耗时（ms，Standby→Running）。</summary>
    public const string WakeupTotalMs = "wakeup_total_ms";
    /// <summary>强占触发累计次数（autoPre 抢占锁槽）。</summary>
    public const string PreemptTrigger = "preempt_trigger";

    // ── ③ llama-server 推理指标（task 维度） ──
    /// <summary>prefill 吞吐（统一名，旧键 pp_tps）。</summary>
    public const string PromptEvalTps = "prompt_eval_tps";
    /// <summary>生成 token 吞吐（统一名，旧键 tg_tps）。</summary>
    public const string GenTps = "gen_tps";
    /// <summary>投机解码接受率（0~1，条件采集：后端暴露时才写）。</summary>
    public const string DraftAcceptRate = "draft_accept_rate";

    // ── ④ 日志管道指标（全局） ──
    /// <summary>队列丢弃累计行数。</summary>
    public const string LogDroppedLines = "log_dropped_lines";
    /// <summary>flush 单次耗时（ms）。</summary>
    public const string LogFlushCostMs = "log_flush_cost_ms";

    // ── ⑤ 网关层性能指标（请求级） ──
    public const string WakeWaitMs = "wake_wait_ms";
    public const string GatewayMs = "gateway_ms";
    public const string BackendMs = "backend_ms";
    public const string TotalMs = "total_ms";
    /// <summary>在途请求数。</summary>
    public const string Inflight = "req_inflight";

    // ── ⑥ 系统资源采样指标 ──
    public const string Cpu = "cpu";
    public const string Mem = "mem";
    public const string MemTotal = "mem_total";
    public const string Vram = "vram";
    public const string VramTotal = "vram_total";

    // ── 兼容别名（v2.21 旧键 → 注册表键，迁移期供 PerfAnalyzer.ValueOf / 默认阈值使用） ──
    /// <summary>v2.21 旧键 pp_tps → <see cref="PromptEvalTps"/>。</summary>
    public const string PpTpsLegacy = "pp_tps";
    /// <summary>v2.21 旧键 tg_tps → <see cref="GenTps"/>。</summary>
    public const string TgTpsLegacy = "tg_tps";
    /// <summary>v2.21 旧键 vram_mb → <see cref="Vram"/>。</summary>
    public const string VramMbLegacy = "vram_mb";

    /// <summary>全部注册表键（供唯一性校验测试与遍历使用，不含兼容别名）。</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        KvHitDelta, KvFalseMiss, KvRestoreMs, KvSaveMs, SavedN, KvFullPrefill, KvReuseTokens, KvReuseSavedMs,
        SlotSelectMs, EvictCount, WakeupTotalMs, PreemptTrigger,
        PromptEvalTps, GenTps, DraftAcceptRate,
        LogDroppedLines, LogFlushCostMs,
        WakeWaitMs, GatewayMs, BackendMs, TotalMs, Inflight,
        Cpu, Mem, MemTotal, Vram, VramTotal,
    };
}
