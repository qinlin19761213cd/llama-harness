namespace LlamaHarness;

/// <summary>
/// 性能事件（v2.22 可观测）：事件型指标的统一模型——单次瞬时值（耗时/计数），
/// 与周期采样（<see cref="PerfPoint"/>）形态互补。调用方自行 Stopwatch 计时后一次性投递。
/// Category 如 "kv"（缓存）/ "sched"（调度）；Op 如 "save"/"restore"/"slot_select"/"wakeup"。
/// </summary>
public sealed record PerfEvent(
    string Category,
    string Op,
    double DurationMs,
    string? Key = null,
    double? Value = null,
    DateTime Ts = default);
