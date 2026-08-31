using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// PerfEventTracker 性能事件追踪器单测（v2.22 可观测）：最近 N 环形 / 会话聚合 / Completed 事件（锁外）/ Clear。
/// </summary>
public class PerfEventTrackerTests
{
    [Fact]
    public void Record_KeepsRecentWindow_DropsOldest()
    {
        var t = new PerfEventTracker(capacity: 3);
        t.Record(new PerfEvent("kv", "save", 1));
        t.Record(new PerfEvent("kv", "save", 2));
        t.Record(new PerfEvent("kv", "save", 3));
        t.Record(new PerfEvent("kv", "save", 4));

        var recent = t.Recent(10);
        Assert.Equal(3, recent.Count); // 满容量：最旧的 1ms 被淘汰
        Assert.Equal(2, recent[0].DurationMs);
        Assert.Equal(4, recent[^1].DurationMs);
    }

    [Fact]
    public void Record_AggregatesStats_ByCategoryAndOp()
    {
        var t = new PerfEventTracker();
        t.Record(new PerfEvent("kv", "save", 10));
        t.Record(new PerfEvent("kv", "save", 30));
        t.Record(new PerfEvent("kv", "restore", 100));
        t.Record(new PerfEvent("sched", "slot_select", 5));

        var save = t.Stats("kv", "save");
        Assert.NotNull(save);
        Assert.Equal(2, save!.Count);
        Assert.Equal(40, save.SumMs);
        Assert.Equal(20, save.AvgMs);
        Assert.Equal(30, save.MaxMs);

        Assert.Equal(1, t.Stats("kv", "restore")!.Count);
        Assert.Equal(100, t.Stats("kv", "restore")!.MaxMs);
        Assert.Null(t.Stats("kv", "nonexist")); // 无记录 → null
    }

    [Fact]
    public void Record_RaisesCompleted_OutsideLock()
    {
        var t = new PerfEventTracker();
        PerfEvent? received = null;
        int calls = 0;
        t.Completed += e => { received = e; calls++; };
        t.Record(new PerfEvent("kv", "restore", 42, "app-x"));

        Assert.Equal(1, calls);
        Assert.Equal("kv", received!.Category);
        Assert.Equal("restore", received.Op);
        Assert.Equal(42, received.DurationMs);
        Assert.Equal("app-x", received.Key);
        Assert.True(received.Ts > DateTime.MinValue, "Ts 应回填为当前时间");
    }

    [Fact]
    public void Clear_ResetsAll()
    {
        var t = new PerfEventTracker();
        t.Record(new PerfEvent("kv", "save", 1));
        t.Clear();
        Assert.Empty(t.Recent(10));
        Assert.Null(t.Stats("kv", "save"));
    }
}
