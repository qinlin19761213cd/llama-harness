using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// InFlightTracker 在途任务明细跟踪测试（v2.18 状态栏优化）：
/// 登记/移除/快照顺序/未知请求 App 为 null/并发安全（ConcurrentDictionary 热路径隔离）。
/// </summary>
public class InFlightTrackerTests
{
    [Fact]
    public void Register_And_Snapshot_ReturnsOrderedTasks()
    {
        var t = new InFlightTracker();
        t.Register("POST", "/v1/chat/completions", "DSH 主 Agent");
        t.Register("GET", "/v1/models", null);
        var snap = t.Snapshot();
        Assert.Equal(2, snap.Count);
        Assert.Equal("POST", snap[0].Method);
        Assert.Equal("/v1/chat/completions", snap[0].Path);
        Assert.Equal("DSH 主 Agent", snap[0].App);
        Assert.Equal("GET", snap[1].Method);
        Assert.Null(snap[1].App); // 未知请求 App 为 null
    }

    [Fact]
    public void Unregister_RemovesTask()
    {
        var t = new InFlightTracker();
        var seq1 = t.Register("POST", "/a", "A");
        var seq2 = t.Register("POST", "/b", "B");
        t.Unregister(seq1);
        var snap = t.Snapshot();
        Assert.Single(snap);
        Assert.Equal("/b", snap[0].Path);
        Assert.Equal(1, t.Count);
    }

    [Fact]
    public void Unregister_UnknownSeq_NoThrow()
    {
        var t = new InFlightTracker();
        t.Register("POST", "/a", null);
        t.Unregister(999); // 不存在的序号不抛异常
        Assert.Equal(1, t.Count);
    }

    [Fact]
    public void Empty_Snapshot_IsEmpty()
    {
        var t = new InFlightTracker();
        Assert.Empty(t.Snapshot());
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void Seq_StrictlyIncreasing()
    {
        var t = new InFlightTracker();
        var s1 = t.Register("POST", "/1", null);
        var s2 = t.Register("POST", "/2", null);
        var s3 = t.Register("POST", "/3", null);
        Assert.True(s1 < s2 && s2 < s3);
        var snap = t.Snapshot();
        Assert.Equal(new[] { s1, s2, s3 }, snap.Select(x => x.Seq).ToArray());
    }

    [Fact]
    public void Concurrent_RegisterUnregister_NoCorruption()
    {
        var t = new InFlightTracker();
        var seqs = new System.Collections.Concurrent.ConcurrentBag<int>();
        Parallel.For(0, 200, i => seqs.Add(t.Register("POST", $"/p{i}", null)));
        foreach (var s in seqs) t.Unregister(s);
        Assert.Equal(0, t.Count);
        Assert.Empty(t.Snapshot());
    }
}
