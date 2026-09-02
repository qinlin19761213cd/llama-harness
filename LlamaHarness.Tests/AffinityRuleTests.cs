using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// AffinityRule.UiPrefixOf 推导测试（v2.16 Step 4 UI 动态化）：
/// - UI checkbox 动态化正确性核心：规则 → AutoPreemptiveApps/AutoSnapshotKeys 前缀的推导；
/// - 默认 4 规则 UiPrefix 与重构前 4 前缀集合（dsh_rule/webui/trae_global/dsh_agent_global）逐字一致；
/// - 新增业务 = 配置追加一条规则即自动出 UI checkbox（前缀/显示名由规则派生），零代码改动；
/// - 显式 UiPrefix 优先、空值回退 Id。
/// </summary>
public class AffinityRuleTests
{
    private static readonly IReadOnlyList<AffinityRule> DefaultRules = AppConfig.DefaultAffinityRules();

    [Fact]
    public void DefaultRules_UiPrefix_MatchesLegacyHardcodedPrefixes()
    {
        // 与重构前 AutoPreemptiveApps/AutoSnapshotKeys 硬编码前缀集合逐字等价（checkbox 勾选映射的前缀来源）
        var actual = DefaultRules.Select(r => r.UiPrefixOf()).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "dsh_agent_global", "dsh_rule", "trae_global", "webui" }, actual);
    }

    [Theory]
    [InlineData("dsh_rule", "dsh_rule")]
    [InlineData("webui", "webui")]
    [InlineData("trae_global", "trae_global")]
    [InlineData("dsh_agent", "dsh_agent_global")]
    public void DefaultRules_Each_UiPrefixIsExpected(string id, string expected)
    {
        var rule = DefaultRules.First(r => r.Id == id);
        Assert.Equal(expected, rule.UiPrefixOf());
    }

    [Fact]
    public void HeaderRule_KeyTemplate_DerivesPrefixFromPlaceholder()
    {
        var r = new AffinityRule { Id = "mybiz", Match = AffinityMatchType.Header, KeyTemplate = "mybiz_{value}" };
        Assert.Equal("mybiz", r.UiPrefixOf());
    }

    [Fact]
    public void FixedKeyRule_UiPrefixFallsBackToKey()
    {
        var r = new AffinityRule { Id = "svc", Match = AffinityMatchType.HeaderValue, Key = "svc_global" };
        Assert.Equal("svc_global", r.UiPrefixOf());
    }

    [Fact]
    public void ExplicitUiPrefix_TakesPrecedenceOverDerivation()
    {
        var r = new AffinityRule { Id = "x", UiPrefix = "custom_prefix", Match = AffinityMatchType.Header, KeyTemplate = "other_{value}" };
        Assert.Equal("custom_prefix", r.UiPrefixOf());
    }

    [Fact]
    public void NoKeyNoTemplate_UiPrefixFallsBackToId()
    {
        var r = new AffinityRule { Id = "fallback", Match = AffinityMatchType.Header };
        Assert.Equal("fallback", r.UiPrefixOf());
    }

    [Fact]
    public void CustomRule_ConfigOnly_NewBusinessGetsUiPrefix_NoCodeChange()
    {
        // 新增业务：配置追加一条规则即可自动出 UI checkbox（前缀/显示名由规则派生），零代码改动证据
        var rules = new List<AffinityRule>(DefaultRules)
        {
            new() { Id = "newbiz", Name = "新业务", UiPrefix = "newbiz", Match = AffinityMatchType.Header, Header = "x-newbiz", KeyTemplate = "newbiz_{value}", Priority = 0 },
        };
        var newbiz = rules.First(r => r.Id == "newbiz");
        Assert.Equal("newbiz", newbiz.UiPrefixOf());
        Assert.Contains("newbiz", rules.Select(r => r.UiPrefixOf()));
    }
}
