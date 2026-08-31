using LlamaHarness;
using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// Warming 子状态单测（3.2）：
/// - PickWarmSlot 安全槽位选择（dummy 预热只碰未绑定 KV 快照的槽位，防污染已 eager restore 的 KV）
/// 注：RunWarmingAsync 主体依赖真实后端管道（eager restore + dummy 请求），属集成路径，此处覆盖可独立判定的槽位选择逻辑。
/// </summary>
public class WarmingTests
{
    [Fact]
    public void PickWarmSlot_FirstFreeSlot()
    {
        // slot0 绑定 KV 快照 → 选 slot1
        Assert.Equal(1, SchedulerUtils.PickWarmSlot(2, new[] { 0 }));
    }

    [Fact]
    public void PickWarmSlot_AllBound_ReturnsMinusOne()
    {
        // 全部槽位均绑定 KV 快照 → -1（跳过预热，防污染已恢复 KV）
        Assert.Equal(-1, SchedulerUtils.PickWarmSlot(2, new[] { 0, 1 }));
        Assert.Equal(-1, SchedulerUtils.PickWarmSlot(2, Enumerable.Range(0, 2)));
    }

    [Fact]
    public void PickWarmSlot_NoBindings_PicksZero()
    {
        // 无绑定（新进程首唤醒）→ slot0
        Assert.Equal(0, SchedulerUtils.PickWarmSlot(2, Array.Empty<int>()));
    }

    [Fact]
    public void PickWarmSlot_SkipsBoundInOrder()
    {
        // bound {0} → 1；bound {0,1} → 2（按槽位号顺序取第一个空闲）
        Assert.Equal(1, SchedulerUtils.PickWarmSlot(3, new[] { 0 }));
        Assert.Equal(2, SchedulerUtils.PickWarmSlot(3, new[] { 0, 1 }));
        Assert.Equal(0, SchedulerUtils.PickWarmSlot(3, new[] { 1, 2 }));
    }

    [Fact]
    public void PickWarmSlot_ZeroParallel_ReturnsMinusOne()
    {
        // 边界：parallel=0（无槽位）→ -1
        Assert.Equal(-1, SchedulerUtils.PickWarmSlot(0, Array.Empty<int>()));
    }
}
