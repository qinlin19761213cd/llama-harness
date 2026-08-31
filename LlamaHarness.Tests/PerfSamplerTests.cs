using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// PerfSampler 周期采集冒烟单测（v2.21）：真实短等待验证轻量指标入环形缓冲、LastPoint 更新、Stop 后停止增长。
/// 慢指标（显存/cpp）不在此测——依赖真实 nvidia-smi 与后端，由真实运行验证。
/// </summary>
public class PerfSamplerTests
{
    [Fact]
    public async Task Start_CollectsLightMetrics_IntoSeries()
    {
        using var s = new PerfSampler(() => 0, () => 3); // 后端端口 0 → 跳过 cpp 慢采样
        s.Start();
        await Task.Delay(2300); // 至少 2 个快 tick（1s）
        s.Stop();

        Assert.True(s.Series.Count >= 2, $"期望至少 2 个采样点，实际 {s.Series.Count}");
        var last = s.Series.Snapshot()[^1];
        Assert.True(last.CpuPercent >= 0, "CPU 采样应非负");
        Assert.True(last.MemTotalGb > 0, "内存总量应 > 0");
        Assert.Equal(3, last.Inflight); // 注入的在途计数
        Assert.NotNull(s.LastPoint);
        Assert.Equal(last.Ts, s.LastPoint!.Value.Ts);
    }

    [Fact]
    public async Task Stop_HaltsCollection()
    {
        using var s = new PerfSampler(() => 0, () => 0);
        s.Start();
        await Task.Delay(1300); // 确保首点已采
        s.Stop();
        int c1 = s.Series.Count;
        Assert.True(c1 >= 1, $"期望至少 1 个采样点，实际 {c1}");
        await Task.Delay(1300); // 停止后再等一个 tick 周期
        Assert.Equal(c1, s.Series.Count); // 停止后不再增长
    }

    [Fact]
    public async Task MultipleTicks_SeriesOrderedByTime()
    {
        using var s = new PerfSampler(() => 0, () => 1);
        s.Start();
        await Task.Delay(2500);
        s.Stop();
        var snap = s.Series.Snapshot();
        for (int i = 1; i < snap.Length; i++)
            Assert.True(snap[i].Ts >= snap[i - 1].Ts, "采样点应时间升序");
    }
}
