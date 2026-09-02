using System.Drawing.Drawing2D;

namespace LlamaHarness;

/// <summary>
/// 自绘折线趋势图（v2.21，零第三方依赖）：注入采样点数组 + 指标键，OnPaint 绘制网格/折线/边界与最新值标签。
/// 指标键对应 <see cref="PerfAnalyzer.ValueOf"/>；Y 轴范围按指标语义自动（cpu 0~100、ctx 0~1、其余动态 0~峰值×1.15）。
/// DoubleBuffered 防闪烁；SetData 后 Invalidate 重绘。
/// </summary>
public sealed class PerfTrendChart : Control
{
    private PerfPoint[] _points = Array.Empty<PerfPoint>();
    private string _metric = "vram_mb";
    private string _title = "显存占用 (MB)";
    private Color _lineColor = UiTheme.C_Primary;

    /// <summary>左侧 Y 轴标签区宽度（px）。</summary>
    private const int YAxisWidth = 56;
    /// <summary>底部时间/标签区高度（px）。</summary>
    private const int BottomPad = 18;
    /// <summary>顶部标题区高度（px）。</summary>
    private const int TopPad = 20;

    public PerfTrendChart()
    {
        DoubleBuffered = true;
        BackColor = UiTheme.C_TextBg;
        MinimumSize = new Size(240, 180);
    }

    /// <summary>更新数据与指标并重绘。</summary>
    public void SetData(PerfPoint[] points, string metric)
    {
        _points = points;
        _metric = metric;
        switch (metric)
        {
            case "vram_mb": _title = "显存占用 (MB)"; _lineColor = UiTheme.C_Primary; break;
            case "cpu": _title = "CPU 占用 (%)"; _lineColor = UiTheme.C_Green; break;
            case "tg_tps": _title = "生成吞吐 (token/s)"; _lineColor = Color.FromArgb(0x4F, 0xA3, 0xFF); break;
            case "ctx": _title = "KV 上下文占用"; _lineColor = Color.FromArgb(0xFF, 0xCC, 0x4D); break;
        case "mem_gb": _title = "内存占用 (GB)"; _lineColor = Color.FromArgb(0x7B, 0xC9, 0xA6); break;
            default: _title = metric; _lineColor = UiTheme.C_Primary; break;
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 绘图区（去掉轴与标题留白）
        var plot = new Rectangle(YAxisWidth, TopPad, Math.Max(10, Width - YAxisWidth - 8), Math.Max(10, Height - TopPad - BottomPad));

        // 标题
        using (var titleFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold))
            g.DrawString(_title, titleFont, Brushes.White, new PointF(8, 3));

        // 网格（5 行水平线）
        using var gridPen = new Pen(Color.FromArgb(0x33, 0x88, 0x88, 0x88), 1);
        (double yMin, double yMax) = GetYRange();
        for (int i = 0; i <= 4; i++)
        {
            float y = plot.Top + plot.Height * i / 4f;
            g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            double val = yMax - (yMax - yMin) * i / 4.0;
            string label = FormatAxis(val, _metric);
            using var axisFont = new Font("Consolas", 7.5F);
            using var brush = new SolidBrush(Color.FromArgb(0xAA, 0xCC, 0xCC, 0xCC));
            g.DrawString(label, axisFont, brush, new PointF(2, y - 6));
        }

        // 折线
        if (_points.Length == 0)
        {
            using var hintFont = new Font("Microsoft YaHei UI", 9F);
            g.DrawString("等待采样数据…", hintFont, Brushes.Gray, plot.Left + 12, plot.Top + 10);
            return;
        }
        double span = yMax - yMin;
        if (span <= 0) span = 1;
        int n = _points.Length;
        var pts = new List<PointF>(n);
        for (int i = 0; i < n; i++)
        {
            var v = PerfAnalyzer.ValueOf(_points[i], _metric);
            if (v == null)
            {
                // 空值：断开当前线
                DrawPolyline(g, pts, plot, yMin, span, n);
                pts.Clear();
                continue;
            }
            float x = plot.Left + plot.Width * i / (float)Math.Max(1, n - 1);
            float y = plot.Bottom - (float)((v.Value - yMin) / span) * plot.Height;
            pts.Add(new PointF(x, Math.Clamp(y, plot.Top, plot.Bottom)));
        }
        DrawPolyline(g, pts, plot, yMin, span, n);

        // 最新值标签（右上）
        var last = _points[^1];
        var lv = PerfAnalyzer.ValueOf(last, _metric);
        if (lv != null)
        {
            using var curFont = new Font("Consolas", 9F, FontStyle.Bold);
            using var brush = new SolidBrush(_lineColor);
            string text = $"{FormatAxis(lv.Value, _metric)}";
            var size = g.MeasureString(text, curFont);
            g.DrawString(text, curFont, brush, new PointF(plot.Right - size.Width, plot.Top - 2));
        }

        // 底部时间标签（首/末）
        using (var tsFont = new Font("Consolas", 7F))
        using (var tsBrush = new SolidBrush(Color.FromArgb(0x88, 0xCC, 0xCC, 0xCC)))
        {
            g.DrawString(_points[0].Ts.ToString("HH:mm:ss"), tsFont, tsBrush, plot.Left, plot.Bottom + 3);
            g.DrawString(_points[^1].Ts.ToString("HH:mm:ss"), tsFont, tsBrush, plot.Right - 56, plot.Bottom + 3);
        }
    }

    private void DrawPolyline(Graphics g, List<PointF> pts, Rectangle plot, double yMin, double span, int n)
    {
        if (pts.Count < 2) return;
        using var pen = new Pen(_lineColor, 1.6f);
        g.DrawLines(pen, pts.ToArray());
    }

    /// <summary>按指标语义取 Y 轴范围：cpu 固定 0~100、ctx 0~1、其余动态 0~峰值×1.15（下限 1 防除零）。</summary>
    private (double min, double max) GetYRange()
    {
        if (_metric == "cpu") return (0, 100);
        if (_metric == "ctx") return (0, 1);
        double peak = 0;
        foreach (var p in _points)
        {
            var v = PerfAnalyzer.ValueOf(p, _metric);
            if (v != null && v.Value > peak) peak = v.Value;
        }
        double max = Math.Max(1, peak * 1.15);
        return (0, max);
    }

    private static string FormatAxis(double v, string metric)
    {
        if (metric == "vram_mb") return v >= 1024 ? $"{v / 1024:F1}G" : $"{v:F0}M";
        if (metric == "ctx") return $"{v * 100:F0}%";
        if (metric == "tg_tps" || metric == "pp_tps") return $"{v:F0}";
        if (metric == "cpu") return $"{v:F0}%";
    if (metric == "mem_gb") return $"{v:F1}G";
        return $"{v:F1}";
    }
}
