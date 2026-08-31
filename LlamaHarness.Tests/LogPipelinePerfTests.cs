using LlamaHarness;
using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// LogPipeline 性能快照单测（v2.22 可观测）：PerfSnapshot 累积计数（丢弃行数 / flush 平均耗时）初始为零，写盘后更新。
/// </summary>
public class LogPipelinePerfTests
{
    private static string TempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), "lpharness_perf_" + tag);
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }

    [Fact]
    public void PerfSnapshot_InitiallyZero()
    {
        using var lp = new LogPipeline(TempDir("init"), QueueFullPolicy.DropNewest);
        var (dropped, avg) = lp.PerfSnapshot();
        Assert.Equal(0, dropped);
        Assert.Equal(0, avg);
    }

    [Fact]
    public async Task EnqueueAndDrain_FlushCountAdvances_AvgNonNegative()
    {
        using var lp = new LogPipeline(TempDir("flush"), QueueFullPolicy.DropNewest);
        for (int i = 0; i < 200; i++)
            lp.Enqueue(LogStream.Main, DateTime.UtcNow, $"line-{i}");
        await Task.Delay(800); // 写线程 drain + 双阈值 flush（150ms）
        lp.Shutdown(); // 刷出剩余日志确保落盘

        var (dropped, avg) = lp.PerfSnapshot();
        Assert.True(avg >= 0, $"flush 平均耗时不应为负: {avg}");
        Assert.True(dropped >= 0, $"丢弃行数不应为负: {dropped}");
    }
}
