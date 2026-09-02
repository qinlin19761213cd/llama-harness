using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// PerfSeries 环形缓冲单测（v2.21）：容量边界 / 环绕覆盖最旧 / 快照时间序 / Last 取最近 / 清空 / 并发冒烟。
/// 性能监控模块的基础数据结构——保证滑动窗口语义正确，采样器与趋势图依赖它。
/// </summary>
public class PerfSeriesTests
{
    [Fact]
    public void Ctor_NonPositiveCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PerfSeries<int>(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PerfSeries<int>(-1));
    }

    [Fact]
    public void AddBeforeFull_OrderPreserved()
    {
        var s = new PerfSeries<int>(5);
        for (int i = 0; i < 5; i++) s.Add(i);
        Assert.Equal(5, s.Count);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, s.Snapshot());
    }

    [Fact]
    public void AddAfterFull_OverwritesOldest()
    {
        var s = new PerfSeries<int>(5);
        for (int i = 0; i < 8; i++) s.Add(i); // 0,1,2 被覆盖
        Assert.Equal(5, s.Count); // 满容量后 Count 恒定
        Assert.Equal(new[] { 3, 4, 5, 6, 7 }, s.Snapshot());
    }

    [Fact]
    public void Snapshot_IsDetachedCopy_UnaffectedByLaterAdds()
    {
        var s = new PerfSeries<int>(3);
        s.Add(1); s.Add(2);
        var snap = s.Snapshot();
        s.Add(3); // 满容量 3：1,2,3 全保留（未覆盖）
        Assert.Equal(new[] { 1, 2 }, snap); // 快照不受后续写入影响
        Assert.Equal(new[] { 1, 2, 3 }, s.Snapshot());
    }

    [Fact]
    public void Last_N_ReturnsMostRecentOrdered()
    {
        var s = new PerfSeries<int>(10);
        for (int i = 0; i < 10; i++) s.Add(i);
        Assert.Equal(new[] { 6, 7, 8, 9 }, s.Last(4));
        Assert.Empty(s.Last(0));
        Assert.Empty(s.Last(-3));
    }

    [Fact]
    public void Last_LargerThanCount_ReturnsAll()
    {
        var s = new PerfSeries<int>(10);
        s.Add(1); s.Add(2); s.Add(3);
        Assert.Equal(new[] { 1, 2, 3 }, s.Last(99));
        Assert.Equal(new[] { 1, 2, 3 }, s.Last(3));
    }

    [Fact]
    public void Clear_ResetsCountAndBuffer()
    {
        var s = new PerfSeries<int>(5);
        for (int i = 0; i < 6; i++) s.Add(i);
        s.Clear();
        Assert.Equal(0, s.Count);
        Assert.Empty(s.Snapshot());
        s.Add(42);
        Assert.Equal(new[] { 42 }, s.Snapshot());
    }

    [Fact]
    public void WrappedTail_LastAcrossOverwrite_IsCorrect()
    {
        var s = new PerfSeries<int>(4);
        for (int i = 0; i < 10; i++) s.Add(i); // 满：6,7,8,9
        Assert.Equal(new[] { 7, 8, 9 }, s.Last(3));
        Assert.Equal(new[] { 6, 7, 8, 9 }, s.Last(4));
        Assert.Equal(new[] { 9 }, s.Last(1));
    }

    [Fact]
    public void ConcurrentAdds_NoException_CountBounded()
    {
        var s = new PerfSeries<int>(64);
        var tasks = new System.Threading.Tasks.Task[8];
        for (int t = 0; t < tasks.Length; t++)
        {
            int seed = t;
            tasks[t] = System.Threading.Tasks.Task.Run(() =>
            {
                for (int i = 0; i < 500; i++) s.Add(seed * 1000 + i);
            });
        }
        System.Threading.Tasks.Task.WaitAll(tasks);
        Assert.Equal(64, s.Count); // 并发写入后仍在容量上限内
        var snap = s.Snapshot();
        Assert.Equal(64, snap.Length);
    }
}
