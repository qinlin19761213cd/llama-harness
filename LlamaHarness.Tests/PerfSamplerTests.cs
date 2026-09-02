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
    [Fact]
    public void ParseMetricsPrometheus_ReadsThroughput()
    {
        var text =
            "# HELP llamacpp:predicted_tokens_seconds Average generation throughput in tokens/s\n" +
            "# TYPE llamacpp:predicted_tokens_seconds gauge\n" +
            "llamacpp:predicted_tokens_seconds 65.2\n" +
            "# HELP llamacpp:prompt_tokens_seconds Average prompt throughput in tokens/s\n" +
            "# TYPE llamacpp:prompt_tokens_seconds gauge\n" +
            "llamacpp:prompt_tokens_seconds 1234.5\n";

        var (tg, pp) = PerfSampler.ParseMetricsPrometheus(text);

        Assert.NotNull(tg);
        Assert.NotNull(pp);
        Assert.Equal(65.2, tg!.Value, 2);
        Assert.Equal(1234.5, pp!.Value, 2);
    }

    [Fact]
    public void ParseMetricsPrometheus_EmptyOrMissing_ReturnsNull()
    {
        var (t1, p1) = PerfSampler.ParseMetricsPrometheus("");
        Assert.Null(t1);
        Assert.Null(p1);

        var (t2, p2) = PerfSampler.ParseMetricsPrometheus("# 只有注释\nother_metric 1\n");
        Assert.Null(t2);
        Assert.Null(p2);
    }

    [Fact]
    public void ParseVramText_CommaAndSlashFormats()
    {
        // nvidia-smi 原生逗号格式（csv,noheader,nounits）
        var (u1, t1) = PerfSampler.ParseVramText("16546, 20480");
        Assert.Equal(16546, u1);
        Assert.Equal(20480, t1);

        // GetVramTextAsync 斜杠格式（"used/total MB"）——v2.23.2 修复前恒 null 的格式
        var (u2, t2) = PerfSampler.ParseVramText("16546/20480 MB");
        Assert.Equal(16546, u2);
        Assert.Equal(20480, t2);
    }

    [Fact]
    public void ParseVramText_Garbage_ReturnsNull()
    {
        var (u, t) = PerfSampler.ParseVramText("garbage-not-a-number");
        Assert.Null(u);
        Assert.Null(t);
    }
}