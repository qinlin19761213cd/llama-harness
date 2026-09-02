using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// MetricKeys 指标键注册表测试（v2.22）：校验注册表键非空、全局唯一，
/// 保证《可观测体系设计方案》§3 注册表在代码侧的权威落地不漂移。
/// </summary>
public class MetricKeysTests
{
    [Fact]
    public void 注册表键非空且唯一()
    {
        Assert.NotEmpty(MetricKeys.All);
        Assert.All(MetricKeys.All, k => Assert.False(string.IsNullOrWhiteSpace(k)));
        Assert.Equal(MetricKeys.All.Count, MetricKeys.All.Distinct().Count());
    }

    [Fact]
    public void 兼容别名与注册表键值对应()
    {
        // v2.21 旧键 → 注册表键的迁移映射（PerfAnalyzer.ValueOf 归一依据）
        Assert.Equal("pp_tps", MetricKeys.PpTpsLegacy);
        Assert.Equal("tg_tps", MetricKeys.TgTpsLegacy);
        Assert.Equal("vram_mb", MetricKeys.VramMbLegacy);
        Assert.Equal("prompt_eval_tps", MetricKeys.PromptEvalTps);
        Assert.Equal("gen_tps", MetricKeys.GenTps);
        Assert.Equal("vram", MetricKeys.Vram);
    }
}
