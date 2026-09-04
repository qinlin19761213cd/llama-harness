using System.Diagnostics;

namespace LlamaHarness;

/// <summary>
/// 单调时钟合成（D3 修复抽取 / P1-3 M-07）：基准在类型首次加载时固定，
/// Now() = 基准本地时间 + Stopwatch 单调偏移，规避 DateTime.Now 受系统时钟回拨 / 夏令时切换 / NTP 校准导致相邻时间戳倒挂。
/// 用于采样点、请求时延记录等需要严格时间升序的时间戳（跨进程重启基准重置，仅保证进程内单调）。
/// </summary>
internal static class MonotonicClock
{
    private static readonly DateTime EpochLocal = DateTime.Now;
    private static readonly long EpochStopwatch = Stopwatch.GetTimestamp();
    private static readonly double TicksPerMs = (double)Stopwatch.Frequency / 1000.0;

    /// <summary>取当前单调递增的本地时间戳（相对进程启动的本地基准）。</summary>
    public static DateTime Now() =>
        EpochLocal.AddMilliseconds((long)((Stopwatch.GetTimestamp() - EpochStopwatch) / TicksPerMs));
}
