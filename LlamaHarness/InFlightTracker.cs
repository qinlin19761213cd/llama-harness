using System.Collections.Concurrent;

namespace LlamaHarness;

/// <summary>
/// 在途任务明细跟踪（v2.18 状态栏优化）：并发登记/移除/快照，供右侧状态栏「服务阶段」卡片展示具体在途任务。
/// 与 _inflight 计数（Interlocked 热路径）分离：本类为低频读写字典；登记时派生亲和应用名（AffinityRule 匹配），
/// 未知请求 App 为 null（显示时回退到「方法 + 路径」）。
/// </summary>
public sealed class InFlightTracker
{
    private readonly ConcurrentDictionary<int, InFlightTask> _tasks = new();
    private int _seq;

    /// <summary>在途任务快照记录：序号（登记顺序）/HTTP 方法/请求路径/亲和应用名（可空）/开始时刻。</summary>
    public readonly record struct InFlightTask(int Seq, string Method, string Path, string? App, DateTime StartedAt);

    /// <summary>登记一个在途任务，返回任务序号（Unregister 用）。</summary>
    public int Register(string method, string path, string? app)
    {
        int seq = Interlocked.Increment(ref _seq);
        _tasks[seq] = new InFlightTask(seq, method, path, app, DateTime.Now);
        return seq;
    }

    /// <summary>按序号移除已完成的在途任务。</summary>
    public void Unregister(int seq) => _tasks.TryRemove(seq, out _);

    /// <summary>当前在途任务数。</summary>
    public int Count => _tasks.Count;

    /// <summary>按登记顺序返回快照（UI 渲染用）。</summary>
    public IReadOnlyList<InFlightTask> Snapshot()
        => _tasks.Values.OrderBy(t => t.Seq).ToArray();
}
