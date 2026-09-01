using System.Collections.Specialized;
using LlamaHarness;
using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// SlotAffinity E-5 并发单测：排队等待（全槽强占）不再阻塞其他请求的槽位操作。
/// 旧实现 Sleep-in-lock：一个请求排队 → GetSlot/SetPreemptive/Snapshot 全部被卡最长 30s。
/// </summary>
public class SlotAffinityConcurrencyTests
{
    private static string BindingsPath => Path.Combine(AppContext.BaseDirectory, "config", "slot_bindings.json");

    /// <summary>测试隔离：清除共享持久化文件，避免跨用例串扰。</summary>
    private static void Cleanup()
    {
        try { if (File.Exists(BindingsPath)) File.Delete(BindingsPath); } catch { /* 忽略 */ }
    }

    private static NameValueCollection Headers(string userId) => new() { { "x-deepseek-harness-user-id", userId } };

    [Fact]
    public void WaitQueue_DoesNotBlockOtherSlotOperations_AndAcquiresAfterRelease()
    {
        Cleanup();
        var aff = new SlotAffinity(2, maxWaitSeconds: 3);

        // 占满两槽并强占
        var a = aff.GetSlot(Headers("uA"));
        var b = aff.GetSlot(Headers("uB"));
        Assert.Equal(0, a.Slot);
        Assert.Equal(1, b.Slot);
        aff.SetPreemptive(a.Key!, true);
        aff.SetPreemptive(b.Key!, true);

        // C 进入排队（全槽强占）
        var cTask = Task.Run(() => aff.GetSlot(Headers("uC")));
        Thread.Sleep(200); // 让 C 进入等待循环

        // C 等待期间：其他槽位操作必须不被阻塞（旧实现被 Sleep-in-lock 卡住 ≥1s/轮）
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _ = aff.Snapshot();
        bool aStillPreemptive = aff.IsPreemptive(a.Key!);
        aff.SetPreemptive(b.Key!, false); // 释放 B
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 500, $"槽位操作耗时 {sw.ElapsedMilliseconds}ms，被排队阻塞");
        Assert.True(aStillPreemptive);

        // C 应在 ≤4s 内拿到 B 的槽位（驱逐非强占 B）
        Assert.True(cTask.Wait(TimeSpan.FromSeconds(4)), "C 未在 4s 内获得槽位");
        var c = cTask.Result;
        Assert.Equal(b.Slot, c.Slot);
        Assert.Equal("dsh_rule_uC", c.Key);
    }

    [Fact]
    public void WaitQueue_TimeoutDegradesToRandomSlotWithoutBinding()
    {
        Cleanup();
        var aff = new SlotAffinity(2, maxWaitSeconds: 1);
        var a = aff.GetSlot(Headers("vA"));
        var b = aff.GetSlot(Headers("vB"));
        aff.SetPreemptive(a.Key!, true);
        aff.SetPreemptive(b.Key!, true);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var c = aff.GetSlot(Headers("vC"));
        sw.Stop();

        Assert.Null(c.Key); // 超时降级：随机槽，不建绑定
        Assert.True(sw.ElapsedMilliseconds >= 900, $"超时路径仅耗时 {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void ExistingBinding_RefreshesAndReturnsSameSlot()
    {
        Cleanup();
        var aff = new SlotAffinity(2);
        var first = aff.GetSlot(Headers("wA"));
        var second = aff.GetSlot(Headers("wA"));
        Assert.Equal(first.Slot, second.Slot);
        Assert.False(second.NewBinding);
    }

    [Fact]
    public void ShouldSkipToolLoopLock_SingleSlotOnly()
    {
        // v2.23.7：单槽位（parallel=1，cap=0）必须跳过 Tool 链会话锁定——否则 SetPreemptive(true)
        // 独占唯一槽位，其他 key 任务最长排队 30s；多槽位保留锁定保护。
        Assert.True(new SlotAffinity(1).ShouldSkipToolLoopLock());
        Assert.False(new SlotAffinity(2).ShouldSkipToolLoopLock());
        Assert.False(new SlotAffinity(4).ShouldSkipToolLoopLock());
    }

    [Fact]
    public void SingleSlot_LockedKey_ForcesOtherTaskToWait_NonPreemptiveEvictsImmediately()
    {
        // v2.23.7 语义记录：单槽位唯一绑定若被强占（Tool 锁副作用），新 key 只能排队（最长 maxWaitSeconds
        // 后超时降级）；保持非强占则新 key 立即 LRU 驱逐——验证"至少 1 槽给非强占新任务"不变量。
        Cleanup();

        // ① 唯一绑定被强占 → 新 key 排队超时降级（不建绑定）
        var locked = new SlotAffinity(1, maxWaitSeconds: 1);
        var a = locked.GetSlot(Headers("uA"));
        locked.SetPreemptive(a.Key!, true); // 模拟 Tool 链锁定副作用
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var c = locked.GetSlot(Headers("uB"));
        sw.Stop();
        Assert.Null(c.Key); // 排队超时降级：随机槽，不建绑定
        Assert.True(sw.ElapsedMilliseconds >= 900, $"超时路径仅耗时 {sw.ElapsedMilliseconds}ms");

        // ② 唯一绑定非强占 → 新 key 立即驱逐（无需排队）
        var free = new SlotAffinity(1, maxWaitSeconds: 30);
        var a2 = free.GetSlot(Headers("uC"));
        Assert.False(free.IsPreemptive(a2.Key!)); // 单槽位默认非强占
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        var c2 = free.GetSlot(Headers("uD"));
        sw2.Stop();
        Assert.True(c2.NewBinding);
        Assert.Equal(a2.Key, c2.Evicted); // 驱逐原绑定，立即拿到槽位
        Assert.True(sw2.ElapsedMilliseconds < 500, $"非强占驱逐耗时 {sw2.ElapsedMilliseconds}ms，异常排队");
    }
}
