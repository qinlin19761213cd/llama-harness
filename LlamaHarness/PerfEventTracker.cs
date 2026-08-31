namespace LlamaHarness;

/// <summary>性能事件会话聚合（按 Category+Op 归组）。</summary>
public sealed record PerfEventStats(int Count, double SumMs, double MaxMs)
{
    /// <summary>平均耗时（ms）。</summary>
    public double AvgMs => Count > 0 ? SumMs / Count : 0;
}

/// <summary>
/// 性能事件追踪器（v2.22 可观测）：事件型指标（KV save/restore、调度 slot_select/wakeup 等）的通用通道。
/// 最近 N 环形缓冲 + 会话聚合（Category:Op 归组）+ Completed 事件。
/// Completed 在锁外触发（防订阅方回调重入死锁，与 RequestTimingTracker 同模式）。
/// 线程安全；Record 开销常量级，不进请求热路径的序列化。
/// </summary>
public sealed class PerfEventTracker
{
    private readonly object _gate = new();
    private readonly List<PerfEvent> _recent = new();
    private readonly Dictionary<string, PerfEventStats> _byOp = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _capacity;

    /// <summary>事件完成通知（锁外触发；供 perf.log 事件行与告警订阅）。</summary>
    public event Action<PerfEvent>? Completed;

    public PerfEventTracker(int capacity = 500)
    {
        _capacity = Math.Max(1, capacity);
    }

    /// <summary>投递一个已完成的事件（调用方已计时）。</summary>
    public void Record(PerfEvent e)
    {
        var ev = e.Ts == default ? e with { Ts = DateTime.Now } : e;
        lock (_gate)
        {
            _recent.Add(ev);
            if (_recent.Count > _capacity) _recent.RemoveAt(0);
            var key = CategoryKey(ev.Category, ev.Op);
            var cur = _byOp.TryGetValue(key, out var old) ? old : new PerfEventStats(0, 0, 0);
            _byOp[key] = new PerfEventStats(cur.Count + 1, cur.SumMs + ev.DurationMs, Math.Max(cur.MaxMs, ev.DurationMs));
        }
        Completed?.Invoke(ev);
    }

    /// <summary>最近 N 个事件快照（按投递顺序，最新在尾部）。</summary>
    public IReadOnlyList<PerfEvent> Recent(int n)
    {
        lock (_gate)
        {
            var take = Math.Min(Math.Max(n, 0), _recent.Count);
            return _recent.Skip(_recent.Count - take).ToList();
        }
    }

    /// <summary>某 Category:Op 的会话聚合；无记录返回 null。</summary>
    public PerfEventStats? Stats(string category, string op)
    {
        lock (_gate)
            return _byOp.TryGetValue(CategoryKey(category, op), out var s) ? s : null;
    }

    /// <summary>清空全部事件与聚合（会话重启时使用）。</summary>
    public void Clear()
    {
        lock (_gate) { _recent.Clear(); _byOp.Clear(); }
    }

    private static string CategoryKey(string category, string op) => category + ":" + op;
}
