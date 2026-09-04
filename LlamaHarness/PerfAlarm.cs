namespace LlamaHarness;

/// <summary>告警级别：Warn = 警告（黄色），Crit = 严重（红色）。</summary>
public enum PerfAlarmLevel { Warn, Crit }

/// <summary>性能告警记录（v2.21）：阈值/趋势检测触发的一次告警，供监控页标色与日志追溯。</summary>
public sealed class PerfAlarm
{
    /// <summary>触发时间。</summary>
    public DateTime Ts { get; init; }
    /// <summary>指标键（cpu/vram_mb/tg_tps/ctx/total_ms...）。</summary>
    public string Metric { get; init; } = "";
    /// <summary>告警级别。</summary>
    public PerfAlarmLevel Level { get; init; }
    /// <summary>触发时的指标值。</summary>
    public double Value { get; init; }
    /// <summary>触发阈值（Crit 级取 CritValue，Warn 级取 WarnValue）。</summary>
    public double Threshold { get; init; }
    /// <summary>人类可读描述（中文）。</summary>
    public string Message { get; init; } = "";
}

/// <summary>
/// 告警事件参数（P3-A）：AlarmRaised / AlarmRecovered 事件的载荷。
/// PreviousLevel 表示状态机迁移前的级别（Raise 时通常 None，Recovered 时通常 Warn/Crit）。
/// </summary>
public sealed class PerfAlarmEventArgs : EventArgs
{
    public string Metric { get; }
    public PerfAlarmLevel Level { get; }
    public double Value { get; }
    /// <summary>触发阈值（Crit 级取 CritValue，Warn 级取 WarnValue）。</summary>
    public double Threshold { get; }
    public DateTime Timestamp { get; }
    /// <summary>迁移前的告警级别（Raise 时 None；Recovered 时 Warn/Crit）。</summary>
    public PerfAlarmLevel? PreviousLevel { get; }

    public PerfAlarmEventArgs(string metric, PerfAlarmLevel level, double value, double threshold, DateTime timestamp, PerfAlarmLevel? previousLevel)
    {
        Metric = metric;
        Level = level;
        Value = value;
        Threshold = threshold;
        Timestamp = timestamp;
        PreviousLevel = previousLevel;
    }
}
