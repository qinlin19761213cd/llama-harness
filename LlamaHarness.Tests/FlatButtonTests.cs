using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// FlatButton 禁用态文字颜色测试（v2.20）：原生 Button 在 Enabled=false 时用系统灰字绘制，
/// 深色背景下偏黑看不清；FlatButton 通过 DisabledForeColor 提供浅灰（#C0C0C0）自绘文字，
/// 与启用态白色文字明显区分。本组验证工厂类型与禁用色配置（自绘 OnPaint 需真实句柄，不在单测覆盖）。
/// </summary>
public class FlatButtonTests
{
    [Fact]
    public void Default_DisabledForeColor_IsCDisabledText()
    {
        var b = new UiTheme.FlatButton();
        Assert.Equal(UiTheme.C_DisabledText, b.DisabledForeColor);
    }

    [Fact]
    public void MakeBtn_Disabled_ReturnsFlatButtonDisabled()
    {
        var b = UiTheme.MakeBtn("停止", enabled: false);
        Assert.IsType<UiTheme.FlatButton>(b);
        Assert.False(b.Enabled);
        Assert.Equal(Color.White, b.ForeColor); // 启用态白字配置不变（禁用态由自绘接管）
    }

    [Fact]
    public void MakeBtn_Enabled_ReturnsFlatButtonEnabled()
    {
        var b = UiTheme.MakeBtn("启动 / 唤醒");
        Assert.IsType<UiTheme.FlatButton>(b);
        Assert.True(b.Enabled);
    }

    [Fact]
    public void ToggleEnabled_NoThrow_AndDisabledForeColorPersists()
    {
        var b = new UiTheme.FlatButton { Enabled = false };
        b.Enabled = true;
        b.Enabled = false;
        Assert.Equal(UiTheme.C_DisabledText, b.DisabledForeColor);
    }
}
