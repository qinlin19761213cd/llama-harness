using System.Diagnostics;

namespace LlamaHarness;

/// <summary>
/// 请求时延追踪器（v2.21）：Begin 开启一次请求计时，MarkReady/MarkSent/Complete 分段打点；
/// Complete 后组装 <see cref="RequestTiming"/> 存入最近环形缓冲 + 更新会话聚合统计 + 触发 Completed 事件。
/// 线程安全（并发请求各自独立 id）；时间用 Stopwatch ticks（不受系统时钟跳变影响）。
/// 幂等：Complete 后再次调用忽略；MarkReady/MarkSent 未打点时对应时延按 0 计（容错顺序缺失）。
/// </summary>
public sealed class RequestTimingTracker
{
    /// <summary>最近请求时延缓冲容量。</summary>
    public const int MaxRecent = 200;

    /// <summary>Stopwatch ticks → 毫秒换算因子。</summary>
    private static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;

    private readonly PerfSeries<RequestTiming> _recent = new(MaxRecent);
    private readonly object _gate = new();
    private readonly Dictionary<string, OpenEntry> _open = new();
    private long _completedCount, _failedCount;
    private long _sumTotalTicks, _sumBackendTicks;
    private long _maxTotalTicks;

    /// <summary>每次请求完成触发（lock 外，订阅方可安全回调）。</summary>
    public event Action<RequestTiming>? Completed;

    /// <summary>进行中请求的分段打点（T* = Stopwatch 时刻；0 = 未打点）。</summary>
    private sealed class OpenEntry
    {
        public long TRecv;
        public long TReady;
        public long TSent;
        public string App = "";
        public string Path = "";
    }

    /// <summary>开启一次请求计时，返回本次请求 id（幂等 Complete 用）。</summary>
    public string Begin(string app, string path)
    {
        var id = Guid.NewGuid().ToString("N");
        lock (_gate) _open[id] = new OpenEntry { TRecv = Stopwatch.GetTimestamp(), App = app, Path = path };
        return id;
    }

    /// <summary>打点：唤醒/排队完成（后端就绪）。</summary>
    public void MarkReady(string id)
    {
        lock (_gate)
        {
            if (_open.TryGetValue(id, out var e) && e.TReady == 0) e.TReady = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>打点：发向后端（网关预处理完成）。</summary>
    public void MarkSent(string id)
    {
        lock (_gate)
        {
            if (_open.TryGetValue(id, out var e) && e.TSent == 0) e.TSent = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>结束计时并记录（幂等：已结束的 id 忽略）。</summary>
    public void Complete(string id, bool success)
    {
        RequestTiming? rt = null;
        lock (_gate)
        {
            if (!_open.Remove(id, out var e)) return; // 已 Complete/不存在：忽略
            long now = Stopwatch.GetTimestamp();
            long wake = e.TReady > 0 ? e.TReady - e.TRecv : 0;
            long gateway = e.TReady > 0 && e.TSent > 0 ? e.TSent - e.TReady : 0;
            long backend = e.TSent > 0 ? now - e.TSent : now - e.TRecv;
            long total = now - e.TRecv;
            if (success) _completedCount++; else _failedCount++;
            _sumTotalTicks += total;
            _sumBackendTicks += backend;
            if (total > _maxTotalTicks) _maxTotalTicks = total;
            rt = new RequestTiming
            {
                Ts = MonotonicClock.Now(), // P1-3/M-07：单调时钟，与 PerfSampler 采样点同源
                App = e.App,
                Path = e.Path,
                Success = success,
                WakeWaitMs = wake / TicksPerMs,
                GatewayMs = gateway / TicksPerMs,
                BackendMs = backend / TicksPerMs,
                TotalMs = total / TicksPerMs,
            };
            _recent.Add(rt);
        }
        Completed?.Invoke(rt); // lock 外触发，防订阅方回调造成重入死锁
    }

    /// <summary>最近请求时延（时间升序，最多 MaxRecent 条）。</summary>
    public IReadOnlyList<RequestTiming> Recent() => _recent.Snapshot();

    /// <summary>会话聚合统计。</summary>
    public RequestTimingStats Stats()
    {
        lock (_gate)
        {
            long ok = _completedCount, fail = _failedCount;
            long total = ok + fail;
            double avgTotal = total > 0 ? (_sumTotalTicks / (double)total) / TicksPerMs : 0;
            double avgBackend = total > 0 ? (_sumBackendTicks / (double)total) / TicksPerMs : 0;
            return new RequestTimingStats
            {
                Completed = ok,
                Failed = fail,
                AvgTotalMs = Math.Round(avgTotal, 1),
                MaxTotalMs = Math.Round(_maxTotalTicks / TicksPerMs, 1),
                AvgBackendMs = Math.Round(avgBackend, 1),
            };
        }
    }

    /// <summary>清空最近记录与会话统计。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _recent.Clear();
            _open.Clear();
            _completedCount = _failedCount = 0;
            _sumTotalTicks = _sumBackendTicks = 0;
            _maxTotalTicks = 0;
        }
    }
}
