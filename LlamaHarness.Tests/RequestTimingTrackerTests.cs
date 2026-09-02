using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// RequestTimingTracker 单测（v2.21）：四段打点组装 / 幂等 / 顺序缺失容错 / 会话聚合 / 事件 / 环形容量 / 并发。
/// 时间用真实 Stopwatch——断言关系而非具体值（四段之和 ≈ Total、计数/极值单调）。
/// </summary>
public class RequestTimingTrackerTests
{
    [Fact]
    public void FullSequence_ProducesConsistentSegments()
    {
        var t = new RequestTimingTracker();
        string id = t.Begin("trae_global", "/v1/chat/completions");
        t.MarkReady(id);
        t.MarkSent(id);
        t.Complete(id, success: true);

        var rt = Assert.Single(t.Recent());
        Assert.True(rt.WakeWaitMs >= 0);
        Assert.True(rt.GatewayMs >= 0);
        Assert.True(rt.BackendMs >= 0);
        Assert.True(rt.TotalMs > 0, "Total 应 > 0");
        Assert.Equal("trae_global", rt.App);
        Assert.Equal("/v1/chat/completions", rt.Path);
        Assert.True(rt.Success);
        // 三段时间之和 ≈ Total（四舍五入误差 < 1ms）
        double sum = rt.WakeWaitMs + rt.GatewayMs + rt.BackendMs;
        Assert.True(Math.Abs(sum - rt.TotalMs) < 1.0, $"wake+gateway+backend={sum:F3} 应≈ total={rt.TotalMs:F3}");
    }

    [Fact]
    public void Complete_IsIdempotent()
    {
        var t = new RequestTimingTracker();
        string id = t.Begin("app", "/v1/chat/completions");
        t.Complete(id, success: true);
        t.Complete(id, success: true); // 二次调用应被忽略
        Assert.Single(t.Recent());
    }

    [Fact]
    public void MissingMarks_AreTolerated()
    {
        var t = new RequestTimingTracker();
        string id = t.Begin("app", "/v1/chat/completions");
        // 不打 MarkReady/MarkSent：Gateway=0，Backend 回退为 Total
        t.Complete(id, success: false);
        var rt = Assert.Single(t.Recent());
        Assert.Equal(0, rt.GatewayMs);
        Assert.False(rt.Success);
        Assert.True(rt.BackendMs >= 0);
        Assert.True(Math.Abs(rt.BackendMs - rt.TotalMs) < 1.0, "未打 sent 时 Backend 应回退为 Total");
    }

    [Fact]
    public void Stats_AggregateCountsAndExtremes()
    {
        var t = new RequestTimingTracker();
        // 3 成功 + 1 失败
        for (int i = 0; i < 3; i++) { var id = t.Begin("a", "/v1/chat/completions"); t.Complete(id, true); }
        var fid = t.Begin("a", "/v1/chat/completions");
        t.Complete(fid, false);
        var s = t.Stats();
        Assert.Equal(3, s.Completed);
        Assert.Equal(1, s.Failed);
        Assert.Equal(4, s.Total);
        Assert.True(s.AvgTotalMs >= 0);
        Assert.True(s.MaxTotalMs >= s.AvgTotalMs, "Max ≥ Avg");
    }

    [Fact]
    public void Completed_EventFiresPerRequest()
    {
        var t = new RequestTimingTracker();
        int fired = 0;
        t.Completed += rt => fired++;
        for (int i = 0; i < 5; i++)
        {
            var id = t.Begin("a", "/v1/chat/completions");
            t.Complete(id, true);
        }
        Assert.Equal(5, fired);
    }

    [Fact]
    public void Recent_IsBoundedRingBuffer()
    {
        var t = new RequestTimingTracker();
        for (int i = 0; i < 250; i++)
        {
            var id = t.Begin("a", "/v1/chat/completions");
            t.Complete(id, true);
        }
        Assert.Equal(RequestTimingTracker.MaxRecent, t.Recent().Count);
    }

    [Fact]
    public void Clear_ResetsAll()
    {
        var t = new RequestTimingTracker();
        for (int i = 0; i < 3; i++)
        {
            var id = t.Begin("a", "/v1/chat/completions");
            t.Complete(id, true);
        }
        t.Clear();
        Assert.Empty(t.Recent());
        var s = t.Stats();
        Assert.Equal(0, s.Total);
        Assert.Equal(0, s.MaxTotalMs);
    }

    [Fact]
    public void ConcurrentRequests_NoException_AllRecorded()
    {
        var t = new RequestTimingTracker();
        var tasks = new System.Threading.Tasks.Task[8];
        for (int i = 0; i < tasks.Length; i++)
        {
            tasks[i] = System.Threading.Tasks.Task.Run(() =>
            {
                for (int j = 0; j < 50; j++)
                {
                    var id = t.Begin("c", "/v1/chat/completions");
                    t.MarkReady(id);
                    t.MarkSent(id);
                    t.Complete(id, j % 5 == 0);
                }
            });
        }
        System.Threading.Tasks.Task.WaitAll(tasks);
        Assert.Equal(RequestTimingTracker.MaxRecent, t.Recent().Count); // 400 请求 > 200 容量：环形缓冲保留最近 200
        var s = t.Stats();
        Assert.Equal(400, s.Total); // 会话统计不受环形容量限制
    }
}
