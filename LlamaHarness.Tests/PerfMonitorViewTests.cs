using System.Windows.Forms;
using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// PerfMonitorView 监控页布局冒烟测试（v2.21.1）：验证固定高度卡片在 TableLayoutPanel
/// 中不塌陷、垂直不重叠——回归防护：曾因 UiTheme.MakeCardPanel 自带 AutoSize=true，
/// TableLayoutPanel 的 AutoSize 行按子控件 PreferredSize 计算行高，固定 Height 的
/// 趋势图/实时数字/请求时延三卡片塌陷为 0 导致显示重叠。
/// </summary>
public class PerfMonitorViewTests
{
    private static void RunSta(Action body)
    {
        Exception? ex = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { ex = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (ex != null) throw new InvalidOperationException("STA 线程内异常: " + ex, ex);
    }

    /// <summary>构建监控页并强制布局（给定足够大的高度使所有行完成布局）。</summary>
    private static PerfMonitorView BuildLayouted()
    {
        var sampler = new PerfSampler(() => 8080, () => 0);
        var timing = new RequestTimingTracker();
        var view = (PerfMonitorView)new PerfMonitorView(sampler, timing, PerfThresholdRule.Defaults()).BuildPage();
        view.Size = new Size(900, 2600);
        view.PerformLayout();
        return view;
    }

    private static TableLayoutPanel? FindLayout(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is TableLayoutPanel tlp) return tlp;
            var sub = FindLayout(c);
            if (sub != null) return sub;
        }
        return null;
    }

    [Fact]
    public void BuildPage_固定高度卡片不塌陷()
    {
        RunSta(() =>
        {
            var view = BuildLayouted();
            try
            {
                var layout = FindLayout(view);
                Assert.NotNull(layout);
                var cards = layout!.Controls.Cast<Control>()
                    .Where(x => x is Panel && x.BackColor == UiTheme.C_Card)
                    .ToList();
                Assert.True(cards.Count >= 4, $"卡片数量应为至少 4（趋势图/数字/时延/告警/摘要），实际 {cards.Count}");
                // 全部卡片高度 > 20（不塌陷为 0）
                Assert.All(cards, c => Assert.True(c.Height > 20, $"卡片高度塌陷: {c.Height}"));
                // 趋势图卡片应保持约 300 高（曾塌陷为 0）；Dock=Fill 时高度 = 行高 - 默认 Margin(3×2) = 294
                var chart = cards.First(x => x.Height >= 200);
                Assert.True(chart.Height >= 290 && chart.Height <= 300, $"趋势图卡片高度异常: {chart.Height}");
            }
            finally { view.Shutdown(); }
        });
    }

    [Fact]
    public void BuildPage_卡片垂直不重叠()
    {
        RunSta(() =>
        {
            var view = BuildLayouted();
            try
            {
                var layout = FindLayout(view);
                Assert.NotNull(layout);
                var cards = layout!.Controls.Cast<Control>()
                    .Where(x => x is Panel && x.BackColor == UiTheme.C_Card)
                    .OrderBy(x => x.Top)
                    .ToList();
                for (int i = 1; i < cards.Count; i++)
                {
                    var prev = cards[i - 1];
                    var cur = cards[i];
                    Assert.True(cur.Top >= prev.Top + prev.Height - 1,
                        $"卡片重叠: [{i - 1}] top={prev.Top} h={prev.Height} vs [{i}] top={cur.Top}");
                }
            }
            finally { view.Shutdown(); }
        });
    }
}
