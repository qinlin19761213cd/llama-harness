using Xunit;

namespace LlamaHarness.Tests;

/// <summary>/props 模型参数中文说明（v2.25）：PropDesc 归一化匹配——下划线/连字符/空格/大小写变体都能命中，未知参数返回空。</summary>
public class MonitorPropsDescTests
{
    [Theory]
    [InlineData("top_k", "Top-K")]
    [InlineData("repeat penalty", "重复惩罚")]
    [InlineData("dry-multiplier", "DRY")]
    [InlineData("temperature", "采样温度")]
    [InlineData("mirostat_tau", "Mirostat")]
    [InlineData("IgnoreEos", "忽略结束符")]
    public void PropDesc_已知参数_命中中文说明(string fieldName, string expectKeyword)
    {
        var desc = MonitorPanelView.PropDesc(fieldName);
        Assert.Contains(expectKeyword, desc);
    }

    [Fact]
    public void PropDesc_未知参数_返回空()
    {
        Assert.Equal("", MonitorPanelView.PropDesc("no_such_param_xyz"));
    }

    [Fact]
    public void PropDesc_空输入_返回空()
    {
        Assert.Equal("", MonitorPanelView.PropDesc(""));
    }

    [Theory]
    [InlineData("samplers_0", "top_k", "Top-K")]
    [InlineData("samplers[3]", "dry", "DRY")]
    [InlineData("stream", "", "流式")]
    public void PropDescEx_samplers按值动态说明_其余走字典(string fieldName, string value, string expectKeyword)
    {
        var desc = MonitorPanelView.PropDescEx(fieldName, value);
        Assert.Contains(expectKeyword, desc);
    }

    [Theory]
    [InlineData("penalties", "惩罚")]
    [InlineData("top_k", "Top-K")]
    [InlineData("temperature", "温度")]
    [InlineData("no_such_sampler", "no_such_sampler")]
    public void SamplerDesc_已知映射_未知返回原值(string value, string expect)
    {
        Assert.Contains(expect, MonitorPanelView.SamplerDesc(value));
    }
}
