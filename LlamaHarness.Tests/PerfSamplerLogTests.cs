using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// PerfSampler 日志管道累积型快照测试（v2.22 可观测）：logStatsProvider 注入后，采样点填充
/// LogDroppedLines / LogFlushCostMs。
/// </summary>
public class PerfSamplerLogTests
{
    [Fact]
    public async Task LogStatsProvider_FillsCumulativeFields()
    {
        var log = (Dropped: 7L, FlushAvgMs: 2.5);
        using var s = new PerfSampler(() => 0, () => 0, logStatsProvider: () => log);
        s.Start();
        await Task.Delay(2300);
        s.Stop();

        Assert.True(s.Series.Count >= 2, $"期望至少 2 个采样点，实际 {s.Series.Count}");
        var last = s.Series.Snapshot()[^1];
        Assert.Equal(7, last.LogDroppedLines);
        Assert.Equal(2.5, last.LogFlushCostMs);
    }

    [Fact]
    public async Task NullProvider_LeavesLogFieldsNull()
    {
        using var s = new PerfSampler(() => 0, () => 0);
        s.Start();
        await Task.Delay(1300);
        s.Stop();
        var last = s.Series.Snapshot()[^1];
        Assert.Null(last.LogDroppedLines);
        Assert.Null(last.LogFlushCostMs);
    }
}
