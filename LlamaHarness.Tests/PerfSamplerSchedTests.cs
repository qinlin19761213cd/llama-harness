using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// PerfSampler 调度累积型快照测试（v2.22 可观测）：schedStatsProvider 注入后，采样点填充
/// EvictCount / PreemptTrigger（SlotAffinity 驱逐/强占会话计数快照）。
/// </summary>
public class PerfSamplerSchedTests
{
    [Fact]
    public async Task SchedStatsProvider_FillsCumulativeFields()
    {
        var sched = (Evict: 3, Preempt: 1);
        using var s = new PerfSampler(() => 0, () => 0, schedStatsProvider: () => sched);
        s.Start();
        await Task.Delay(2300);
        s.Stop();

        Assert.True(s.Series.Count >= 2, $"期望至少 2 个采样点，实际 {s.Series.Count}");
        var last = s.Series.Snapshot()[^1];
        Assert.Equal(3, last.EvictCount);
        Assert.Equal(1, last.PreemptTrigger);
    }

    [Fact]
    public async Task NullProvider_LeavesSchedFieldsNull()
    {
        using var s = new PerfSampler(() => 0, () => 0); // 不传 schedStatsProvider
        s.Start();
        await Task.Delay(1300);
        s.Stop();
        var last = s.Series.Snapshot()[^1];
        Assert.Null(last.EvictCount);
        Assert.Null(last.PreemptTrigger);
    }
}
