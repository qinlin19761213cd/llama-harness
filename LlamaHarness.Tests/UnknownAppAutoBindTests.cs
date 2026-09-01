using System.Collections.Specialized;
using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// 未知应用自动兜底识别（v2.23.8）：
/// - TryAutoBindUnknown 纯函数：UA 稳定哈希生成 unknown_{hash12}；无 UA / 超长 / 达上限 → null
/// - SlotAffinity 集成：开关开 → 未知请求建 unknown 绑定（非轮转）；同 UA 复用；达上限 → 轮转；
///   正式规则永远优先；unknown 键可 LRU 驱逐；UnknownBindEvent 触发（绑定 + 达上限）。
/// </summary>
public class UnknownAppAutoBindTests
{
    private static string BindingsPath => Path.Combine(AppContext.BaseDirectory, "config", "slot_bindings.json");

    private static void Cleanup()
    {
        try { if (File.Exists(BindingsPath)) File.Delete(BindingsPath); } catch { /* 忽略 */ }
    }

    private static NameValueCollection Headers(params (string Name, string Value)[] kv)
    {
        var h = new NameValueCollection();
        foreach (var (n, v) in kv) h[n] = v;
        return h;
    }

    // ── TryAutoBindUnknown 纯函数 ──
    [Fact]
    public void TryAutoBindUnknown_NoUa_ReturnsNull()
        => Assert.Null(AffinityRuleMatcher.TryAutoBindUnknown(Headers(("X-Random", "1")), 16, 0));

    [Fact]
    public void TryAutoBindUnknown_SameUa_SameKey()
    {
        var k1 = AffinityRuleMatcher.TryAutoBindUnknown(Headers(("User-Agent", "kouzi-agent/1.0")), 16, 0);
        var k2 = AffinityRuleMatcher.TryAutoBindUnknown(Headers(("User-Agent", "kouzi-agent/1.0")), 16, 0);
        Assert.NotNull(k1);
        Assert.Equal(k1, k2); // UA 稳定 → key 稳定（KV 可跨请求复用）
        Assert.StartsWith("unknown_", k1!);
        Assert.Equal(12, k1!.Length - "unknown_".Length); // 12 位 hex
    }

    [Fact]
    public void TryAutoBindUnknown_DifferentUa_DifferentKey()
    {
        var k1 = AffinityRuleMatcher.TryAutoBindUnknown(Headers(("User-Agent", "app-a/1.0")), 16, 0);
        var k2 = AffinityRuleMatcher.TryAutoBindUnknown(Headers(("User-Agent", "app-b/1.0")), 16, 0);
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void TryAutoBindUnknown_OverlongUa_ReturnsNull()
    {
        var longUa = new string('a', 513); // > 512 上限
        Assert.Null(AffinityRuleMatcher.TryAutoBindUnknown(Headers(("User-Agent", longUa)), 16, 0));
    }

    [Fact]
    public void TryAutoBindUnknown_AtLimit_ReturnsNull()
        => Assert.Null(AffinityRuleMatcher.TryAutoBindUnknown(Headers(("User-Agent", "x/1")), maxUnknownKeys: 16, existingUnknownCount: 16));

    // ── SlotAffinity 集成 ──
    [Fact]
    public void UnknownApp_Enabled_UnknownRequest_GetsStableUnknownKey()
    {
        Cleanup();
        var aff = new SlotAffinity(1, unknownAutoBind: true);
        var a = aff.GetSlot(Headers(("User-Agent", "kouzi-agent/1.0")));
        Assert.NotNull(a.Key);
        Assert.StartsWith("unknown_", a.Key!);
        Assert.True(a.NewBinding); // 建绑定（非轮转）
        var b = aff.GetSlot(Headers(("User-Agent", "kouzi-agent/1.0")));
        Assert.Equal(a.Key, b.Key); // 同 UA 复用同 key
        Assert.False(b.NewBinding);
    }

    [Fact]
    public void UnknownApp_Disabled_UnknownRequest_StaysRoundRobin()
    {
        Cleanup();
        var aff = new SlotAffinity(1, unknownAutoBind: false); // 默认关（向后兼容）
        var a = aff.GetSlot(Headers(("User-Agent", "x/1")));
        Assert.Null(a.Key); // 保持现状：轮转不绑定
    }

    [Fact]
    public void UnknownApp_MaxKeys_NewUnknownFallsBackToRoundRobin()
    {
        Cleanup();
        var aff = new SlotAffinity(1, unknownAutoBind: true, maxUnknownKeys: 1);
        var a = aff.GetSlot(Headers(("User-Agent", "app1/1")));
        Assert.NotNull(a.Key);
        Assert.StartsWith("unknown_", a.Key!);
        var b = aff.GetSlot(Headers(("User-Agent", "app2/1"))); // 第二个未知 UA → 已达上限
        Assert.Null(b.Key); // 走随机槽，不建绑定（防 KV 磁盘膨胀）
    }

    [Fact]
    public void UnknownApp_KnownRule_WinsOverUnknown()
    {
        Cleanup();
        var aff = new SlotAffinity(1, unknownAutoBind: true);
        var a = aff.GetSlot(Headers(("x-model-provider", "custom_openai_compatible")));
        Assert.Equal("trae_global", a.Key); // 正式规则永远优先于 unknown 兜底
    }

    [Fact]
    public void UnknownApp_UnknownKey_LruEvictable()
    {
        Cleanup();
        var aff = new SlotAffinity(1, unknownAutoBind: true);
        var a = aff.GetSlot(Headers(("User-Agent", "evict-me/1")));
        Assert.StartsWith("unknown_", a.Key!);
        var b = aff.GetSlot(Headers(("x-model-provider", "custom_openai_compatible"))); // 正式应用驱逐 unknown
        Assert.Equal("trae_global", b.Key);
        Assert.Equal(a.Key, b.Evicted); // unknown 非强占，LRU 可驱逐
    }

    [Fact]
    public void UnknownApp_BindEvent_FiresOnBindAndLimit()
    {
        Cleanup();
        var aff = new SlotAffinity(1, unknownAutoBind: true, maxUnknownKeys: 1);
        var events = new List<string>();
        aff.UnknownBindEvent = (key, ua) => events.Add(key ?? "<limit>");
        _ = aff.GetSlot(Headers(("User-Agent", "app1/1")));
        _ = aff.GetSlot(Headers(("User-Agent", "app2/1"))); // 达上限 → 告警事件
        Assert.Equal(2, events.Count);
        Assert.StartsWith("unknown_", events[0]); // 首次绑定
        Assert.Equal("<limit>", events[1]);        // 达上限
    }
}