using System.Collections.Specialized;
using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// SlotAffinity 调度指标单测（v2.22 可观测）：slot_select 事件 / evict / preempt 累积计数。
/// 用自定义 AffinityRule（Header 匹配 x-app）构造可控的绑定与驱逐场景；Cleanup 隔离共享持久化文件。
/// </summary>
public class SlotAffinityPerfTests
{
    /// <summary>测试隔离：清除共享持久化文件，避免跨用例串扰（与 SlotAffinityConcurrencyTests 同模式）。</summary>
    private static void Cleanup()
    {
        try { if (File.Exists(Path.Combine(AppContext.BaseDirectory, "config", "slot_bindings.json"))) File.Delete(Path.Combine(AppContext.BaseDirectory, "config", "slot_bindings.json")); } catch { /* 忽略 */ }
    }

    private static List<AffinityRule> Rules() => new()
    {
        new AffinityRule { Match = AffinityMatchType.Header, Header = "x-app", KeyTemplate = "app_{value}", Priority = 1 }
    };

    private static NameValueCollection H(string app) => new() { ["x-app"] = app };

    [Fact]
    public void GetSlot_RecordsSlotSelectEvent()
    {
        Cleanup();
        var tracker = new PerfEventTracker();
        var aff = new SlotAffinity(slotCount: 4, rules: Rules()) { PerfEvents = tracker };
        aff.GetSlot(H("a"));
        aff.GetSlot(H("b"));
        aff.GetSlot(new NameValueCollection()); // 未知请求 → 轮转槽（不建绑定）

        var selects = tracker.Recent(10).Where(e => e.Op == "slot_select").ToList();
        Assert.Equal(3, selects.Count);
        Assert.All(selects, e => Assert.Equal("sched", e.Category));
        Assert.Equal("app_a", selects[0].Key);
        Assert.Null(selects[2].Key); // 未知请求 key 为 null
    }

    [Fact]
    public void Eviction_IncrementsEvictCount()
    {
        Cleanup();
        var aff = new SlotAffinity(slotCount: 1, rules: Rules());
        aff.GetSlot(H("a")); // 绑定 app_a
        aff.GetSlot(H("b")); // 单槽：驱逐 app_a，绑定 app_b

        var (evict, preempt) = aff.PerfSnapshot();
        Assert.Equal(1, evict);
        Assert.Equal(0, preempt);
    }

    [Fact]
    public void AutoPreemptive_IncrementsPreemptCount()
    {
        Cleanup();
        var aff = new SlotAffinity(slotCount: 3, rules: Rules()); // cap = slotCount-1 = 2，允许 2 个强占
        var autoPre = new[] { "app_" };
        aff.GetSlot(H("a"), autoPre); // 绑定 app_a → 强占冻结
        aff.GetSlot(H("b"), autoPre); // 绑定 app_b → 强占冻结

        var (evict, preempt) = aff.PerfSnapshot();
        Assert.Equal(2, preempt);
        Assert.Equal(0, evict); // 三槽无驱逐
    }

    [Fact]
    public void NoPerfEvents_NullInjection_NoThrow()
    {
        Cleanup();
        var aff = new SlotAffinity(slotCount: 4, rules: Rules()); // 不注入 PerfEvents
        aff.GetSlot(H("a"));
        aff.GetSlot(H("b"));
        var (evict, preempt) = aff.PerfSnapshot();
        Assert.Equal(0, evict);
        Assert.Equal(0, preempt);
    }
}
