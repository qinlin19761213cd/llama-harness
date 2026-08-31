using System.Collections.Specialized;
using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// AffinityRuleMatcher 指纹规则引擎测试（v2.16）：
/// - 默认 4 规则对 4 类请求产出的 key 与重构前 GetAffinityKey 硬编码逐字等价；
/// - 优先级顺序、大小写不敏感、未知请求返回 null；
/// - 新增业务 = 配置追加规则即可识别（零代码改动）的证据测试；
/// - AppNameOf 显示名派生。
/// </summary>
public class AffinityRuleMatcherTests
{
    private static readonly IReadOnlyList<AffinityRule> DefaultRules = AppConfig.DefaultAffinityRules();

    private static NameValueCollection Headers(params (string Name, string Value)[] kv)
    {
        var h = new NameValueCollection();
        foreach (var (n, v) in kv) h[n] = v;
        return h;
    }

    [Fact]
    public void DefaultRules_DshRule_HeaderMatch_ProducesDshRuleKey()
    {
        var h = Headers(("x-deepseek-harness-user-id", "u1"));
        Assert.Equal("dsh_rule_u1", AffinityRuleMatcher.Match(h, DefaultRules));
    }

    [Fact]
    public void DefaultRules_Webui_HeaderMatch_ProducesWebuiKey()
    {
        var h = Headers(("X-Conversation-Id", "c1"));
        Assert.Equal("webui_c1", AffinityRuleMatcher.Match(h, DefaultRules));
    }

    [Fact]
    public void DefaultRules_TraeGlobal_HeaderValueMatch()
    {
        var h = Headers(("x-model-provider", "custom_openai_compatible"));
        Assert.Equal("trae_global", AffinityRuleMatcher.Match(h, DefaultRules));
    }

    [Fact]
    public void DefaultRules_DshAgent_UaAndHeaderPrefixMatch()
    {
        var h = Headers(("User-Agent", "deepseek-harness-sdk/1.0"), ("X-Stainless-Lang", "python"));
        Assert.Equal("dsh_agent_global", AffinityRuleMatcher.Match(h, DefaultRules));
    }

    [Fact]
    public void DefaultRules_Unknown_ReturnsNull()
    {
        var h = Headers(("X-Random", "abc"));
        Assert.Null(AffinityRuleMatcher.Match(h, DefaultRules));
    }

    [Fact]
    public void PriorityOrder_FirstMatchingRuleWins()
    {
        // 同时命中 dsh_rule（优先级1）与 trae_global（优先级3）→ 取 dsh_rule
        var h = Headers(("x-deepseek-harness-user-id", "u1"), ("x-model-provider", "custom_openai_compatible"));
        Assert.Equal("dsh_rule_u1", AffinityRuleMatcher.Match(h, DefaultRules));
    }

    [Fact]
    public void HeaderValueMatch_IsCaseInsensitive()
    {
        var h = Headers(("x-model-provider", "CUSTOM_OPENAI_COMPATIBLE"));
        Assert.Equal("trae_global", AffinityRuleMatcher.Match(h, DefaultRules));
    }

    [Fact]
    public void DshAgent_RequiresBothUaAndStainlessHeader()
    {
        // 只有 UA 无 X-Stainless → 不命中
        var h1 = Headers(("User-Agent", "deepseek-harness-sdk/1.0"));
        Assert.Null(AffinityRuleMatcher.Match(h1, DefaultRules));
        // 有 X-Stainless 但 UA 不含 → 不命中
        var h2 = Headers(("User-Agent", "curl/8.0"), ("X-Stainless-Lang", "python"));
        Assert.Null(AffinityRuleMatcher.Match(h2, DefaultRules));
    }

    [Fact]
    public void CustomRule_ConfigOnly_NewBusinessRecognized()
    {
        // 证据：新增业务只需在配置追加一条规则，无需改代码
        var rules = new List<AffinityRule>(DefaultRules)
        {
            new() { Id = "my_agent", Name = "我的Agent", Match = AffinityMatchType.Header,
                    Header = "X-My-Agent", KeyTemplate = "my_agent_{value}", Priority = 10 },
        };
        var h = Headers(("X-My-Agent", "a1"));
        Assert.Equal("my_agent_a1", AffinityRuleMatcher.Match(h, rules));
    }

    [Fact]
    public void CustomRule_OrderIndependentOfPriority_WhenTie()
    {
        // 相同 Priority 时按列表顺序：后插入的同优先级规则排在默认之后
        var rules = new List<AffinityRule>(DefaultRules)
        {
            new() { Id = "dup", Name = "Dup", Match = AffinityMatchType.Header,
                    Header = "x-deepseek-harness-user-id", KeyTemplate = "dup_{value}", Priority = 1 },
        };
        var h = Headers(("x-deepseek-harness-user-id", "u1"));
        // 默认 dsh_rule 在列表前，仍先命中
        Assert.Equal("dsh_rule_u1", AffinityRuleMatcher.Match(h, rules));
    }

    [Theory]
    [InlineData("dsh_rule_u1", "DSH 规则引擎")]
    [InlineData("webui_c1", "WebUI")]
    [InlineData("trae_global", "Trae Work")]
    [InlineData("dsh_agent_global", "DSH 主 Agent")]
    [InlineData("TRAE_GLOBAL", "Trae Work")]
    [InlineData("unknown_key", "未知应用")]
    public void AppNameOf_DefaultRules(string key, string expected)
    {
        Assert.Equal(expected, AffinityRuleMatcher.AppNameOf(key, DefaultRules));
    }

    [Fact]
    public void AppNameOf_CustomRule_DerivesCustomName()
    {
        var rules = new List<AffinityRule>(DefaultRules)
        {
            new() { Id = "my_agent", Name = "我的Agent", Match = AffinityMatchType.Header,
                    Header = "X-My-Agent", KeyTemplate = "my_agent_{value}", Priority = 10 },
        };
        Assert.Equal("我的Agent", AffinityRuleMatcher.AppNameOf("my_agent_a1", rules));
    }

    [Fact]
    public void SlotAffinity_DefaultCtor_UsesDefaultRules_Equivalence()
    {
        // 端到端：new SlotAffinity(3) 不传 rules，用默认 4 条，GetSlot 识别 dsh_rule_ 与重构前等价
        var aff = new SlotAffinity(3);
        var (slot, key, _, _, _, _) = aff.GetSlot(Headers(("x-deepseek-harness-user-id", "uA")));
        Assert.Equal("dsh_rule_uA", key);
        Assert.InRange(slot, 0, 2);
    }

    [Fact]
    public void SlotAffinity_CustomRulesViaCtor_AppliesCustomMatch()
    {
        var rules = new List<AffinityRule>
        {
            new() { Id = "c", Name = "Custom", Match = AffinityMatchType.Header,
                    Header = "X-Custom", KeyTemplate = "c_{value}", Priority = 1 },
        };
        var aff = new SlotAffinity(2, rules: rules);
        var (_, key, _, _, _, _) = aff.GetSlot(Headers(("X-Custom", "x1")));
        Assert.Equal("c_x1", key);
    }
}
