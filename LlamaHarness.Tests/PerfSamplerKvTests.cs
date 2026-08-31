using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// PerfSampler KV 累积型快照测试（v2.22 可观测）：kvStatsProvider 注入后，采样点填充
/// KvHitDelta / KvFalseMiss / SavedN（KV 缓存会话计数快照）。
/// </summary>
public class PerfSamplerKvTests
{
    [Fact]
    public async Task KvStatsProvider_FillsCumulativeFields()
    {
        var stats = (Hits: 5, FalseMiss: 2, SavedN: 4096);
        using var s = new PerfSampler(() => 0, () => 0, () => stats);
        s.Start();
        await Task.Delay(2300); // 至少 2 个快 tick
        s.Stop();

        Assert.True(s.Series.Count >= 2, $"期望至少 2 个采样点，实际 {s.Series.Count}");
        var last = s.Series.Snapshot()[^1];
        Assert.Equal(5, last.KvHitDelta);
        Assert.Equal(2, last.KvFalseMiss);
        Assert.Equal(4096, last.SavedN);
    }

    [Fact]
    public async Task NullProvider_LeavesCumulativeFieldsNull()
    {
        using var s = new PerfSampler(() => 0, () => 0); // 不传 kvStatsProvider
        s.Start();
        await Task.Delay(1300);
        s.Stop();
        Assert.True(s.Series.Count >= 1);
        var last = s.Series.Snapshot()[^1];
        Assert.Null(last.KvHitDelta);
        Assert.Null(last.KvFalseMiss);
        Assert.Null(last.SavedN);
    }
}
