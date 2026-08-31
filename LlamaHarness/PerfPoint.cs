namespace LlamaHarness;

/// <summary>
/// 性能采样点（统一指标模型，v2.21）：一个时间戳承载系统层 + llama.cpp 层 + 网关层可空指标。
/// 全部字段可空——采集器按可用性填充（如无 nvidia-smi 时 Vram 为 null，UI/分析层显示 "—" 或跳过）。
/// 周期采样（系统/cpp）与点值快照（inflight）统一落此模型，进环形缓冲 + perf.log。
/// </summary>
public readonly struct PerfPoint
{
    /// <summary>采样时间。</summary>
    public DateTime Ts { get; init; }

    // —— 系统层（SystemMetrics，1s 采样）——
    /// <summary>整机 CPU 占用百分比（0~100）。</summary>
    public double? CpuPercent { get; init; }
    /// <summary>物理内存已用（GB）。</summary>
    public double? MemUsedGb { get; init; }
    /// <summary>物理内存总量（GB）。</summary>
    public double? MemTotalGb { get; init; }
    /// <summary>显存已用（MB）；无 nvidia-smi 时为 null。</summary>
    public double? VramUsedMb { get; init; }
    /// <summary>显存总量（MB）；无 nvidia-smi 时为 null。</summary>
    public double? VramTotalMb { get; init; }

    // —— llama.cpp 层（LlamaCppMonitor，5s 采样；多槽取平均/合计）——
    /// <summary>Prompt 处理吞吐（token/s），多槽取平均；无槽位数据为 null。</summary>
    public double? PpTps { get; init; }
    /// <summary>生成吞吐（token/s），多槽取平均；无槽位数据为 null。</summary>
    public double? TgTps { get; init; }
    /// <summary>KV 缓存累计 token 数（Σtokens_cached）。</summary>
    public long? TokensCached { get; init; }
    /// <summary>上下文占用率（Σtokens_cached ÷ ctx_size，0~1）。</summary>
    public double? CtxUsedPct { get; init; }
    /// <summary>正在处理的槽位数。</summary>
    public int? SlotsProcessing { get; init; }

    // —— 网关层（SmartScheduler 点值，1s）——
    /// <summary>在途请求数（含排队等待唤醒）。</summary>
    public int? Inflight { get; init; }

    // —— KV 缓存累积型（RestoreStats 会话计数快照，1s；增量 = 相邻采样点差）——
    /// <summary>KV 命中累计次数（HitByDelta）。</summary>
    public int? KvHitDelta { get; init; }
    /// <summary>非预期 miss 累计次数（前缀无变更却 miss）。</summary>
    public int? KvFalseMiss { get; init; }
    /// <summary>该会话最大 token 偏移量（KV 快照 token 数）。</summary>
    public int? SavedN { get; init; }

    // —— 调度累积型（SlotAffinity 会话计数快照，1s；增量 = 相邻采样点差）——
    /// <summary>驱逐事件累计次数。</summary>
    public int? EvictCount { get; init; }
    /// <summary>强占触发累计次数（autoPre 冻结槽位）。</summary>
    public int? PreemptTrigger { get; init; }

    // —— 日志管道累积型（LogPipeline 会话计数快照，1s）——
    /// <summary>队列丢弃累计行数。</summary>
    public long? LogDroppedLines { get; init; }
    /// <summary>flush 平均耗时（ms）。</summary>
    public double? LogFlushCostMs { get; init; }

    /// <summary>是否含累积型指标（调度驱逐/强占 或 日志管道丢弃/flush），决定是否写 count 行。</summary>
    public bool HasCumulative => EvictCount != null || PreemptTrigger != null || LogDroppedLines != null || LogFlushCostMs != null;

    /// <summary>该点是否含系统层有效指标。</summary>
    public bool HasSystem => CpuPercent != null || MemUsedGb != null || VramUsedMb != null;
    /// <summary>该点是否含 llama.cpp 层有效指标。</summary>
    public bool HasCpp => PpTps != null || TgTps != null || TokensCached != null;
    /// <summary>该点是否含网关层有效指标。</summary>
    public bool HasGateway => Inflight != null;
}
