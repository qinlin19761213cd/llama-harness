namespace LlamaHarness;

/// <summary>阈值告警方向：Above = 高于告警（cpu/显存/时延），Below = 低于告警（吞吐骤降）。</summary>
public enum PerfThresholdDirection { Above, Below }

/// <summary>
/// 性能阈值规则（v2.21 配置驱动）：指标键 + 方向 + 警告/严重阈值 + 最小持续秒数。
/// 指标键与 <see cref="PerfPoint"/> 字段对应：cpu / vram_mb / mem_gb / pp_tps / tg_tps / tok / ctx / slots / inflight，
/// 及请求级 total_ms（网关单请求总时延，MinDurationSeconds=1 表示单次即触发）。
/// 新增业务/改阈值 = 配置追加一条，零代码改动（沿用 v2.16 指纹规则配置化模式）。
/// </summary>
public sealed class PerfThresholdRule
{
    /// <summary>指标键（见类说明）。</summary>
    public string Metric { get; init; } = "";
    /// <summary>告警方向。</summary>
    public PerfThresholdDirection Direction { get; init; } = PerfThresholdDirection.Above;
    /// <summary>警告阈值（超过/低于触发 Warn）。</summary>
    public double WarnValue { get; init; } = 0;
    /// <summary>严重阈值（超过/低于触发 Crit）。</summary>
    public double CritValue { get; init; } = 0;
    /// <summary>最小持续秒数（连续 N 个采样点越过才触发，防毛刺；请求级指标用 1）。</summary>
    public int MinDurationSeconds { get; init; } = 1;

    /// <summary>
    /// 默认规则（基于本机 RTX 3080 20G / 64G 内存 / 单后端黄金配置的实测基线）：
    /// cpu 90%/97% 持续 30s、显存 15G/18.5G 持续 60s、tg_tps 低于 10/3 持续 30s、KV ctx 85%/95% 持续 30s、单请求 60s/180s。
    /// 可在 config.json perf_thresholds 覆盖或追加。
    /// </summary>
    public static List<PerfThresholdRule> Defaults() => new()
    {
        new() { Metric = "cpu", Direction = PerfThresholdDirection.Above, WarnValue = 90, CritValue = 97, MinDurationSeconds = 30 },
        new() { Metric = "vram_mb", Direction = PerfThresholdDirection.Above, WarnValue = 15000, CritValue = 18500, MinDurationSeconds = 60 },
        new() { Metric = "tg_tps", Direction = PerfThresholdDirection.Below, WarnValue = 10, CritValue = 3, MinDurationSeconds = 30 },
        new() { Metric = "ctx", Direction = PerfThresholdDirection.Above, WarnValue = 0.85, CritValue = 0.95, MinDurationSeconds = 30 },
        new() { Metric = "total_ms", Direction = PerfThresholdDirection.Above, WarnValue = 60000, CritValue = 180000, MinDurationSeconds = 1 },
    };
}
