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

    /// <summary>左侧 Y 轴标签区基准宽度（px @96DPI），按 Graphics.DpiX 缩放。</summary>
    private const int YAxisWidthBase = 60;
    /// <summary>底部时间/标签区基准高度（px @96DPI）。</summary>
    private const int BottomPadBase = 18;
    /// <summary>顶部标题区基准高度（px @96DPI）。</summary>
    private const int TopPadBase = 20;

    /// <summary>问题 21 修复：长时间轴降采样阈值。当点数超过可用绘图宽度时可读性下降（相邻点间距 &lt;1px），
    /// 按桶取最小/最大值绘制下/上包络线，避免"糊成一条"。同时减少 DrawLines 顶点数。</summary>
    private const int MaxVisiblePoints = 400;

    /// <summary>问题 32 修复：图例/标题最大显示字符数（超出追加省略号，避免长 metric 名溢出绘图区）。</summary>
    private const int MaxTitleChars = 24;

    // —— C1 修复：原 OnPaint 每次重绘 new 5 个 Font（GDI 句柄泄漏）。提升为实例字段，构造期创建一次、Dispose 释放；仅 UI 线程使用 ——
    private readonly Font _titleFont = new("Microsoft YaHei UI", 9F, FontStyle.Bold);
    private readonly Font _axisFont = new("Consolas", 7.5F);
    private readonly Font _hintFont = new("Microsoft YaHei UI", 9F);
    private readonly Font _curFont = new("Consolas", 9F, FontStyle.Bold);
    private readonly Font _tsFont = new("Consolas", 7F);

    public PerfTrendChart()
    {
        DoubleBuffered = true;
        BackColor = UiTheme.C_TextBg;
        MinimumSize = new Size(240, 180);
    }

    /// <summary>C1 修复：Control 销毁时释放 Font，避免 GDI 句柄泄漏。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont.Dispose();
            _axisFont.Dispose();
            _hintFont.Dispose();
            _curFont.Dispose();
            _tsFont.Dispose();
        }
        base.Dispose(disposing);
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

        // 问题 20 修复：DPI 感知缩放。原实现以 96DPI 像素硬编码，高 DPI 下文字/边距偏小、绘图区错位。
        // 使用 Graphics.DpiX 与 96 的比值作为缩放因子，YAxisWidth/TopPad/BottomPad 与字体一并等比缩放。
        float scale = Math.Max(1f, g.DpiX / 96f);
        int yAxisW = (int)Math.Round(YAxisWidthBase * scale);
        int topPad = (int)Math.Round(TopPadBase * scale);
        int bottomPad = (int)Math.Round(BottomPadBase * scale);

        // 绘图区（去掉轴与标题留白）
        var plot = new Rectangle(yAxisW, topPad, Math.Max(10, Width - yAxisW - 8), Math.Max(10, Height - topPad - bottomPad));

        // 标题（问题 32 修复：长 metric 名截断至 MaxTitleChars，避免越出绘图区）
        string displayTitle = Truncate(_title, MaxTitleChars);
        g.DrawString(displayTitle, _titleFont, Brushes.White, new PointF(8, 3));

        // 网格（5 行水平线）
        using var gridPen = new Pen(Color.FromArgb(0x33, 0x88, 0x88, 0x88), 1);
        using var axisBrush = new SolidBrush(Color.FromArgb(0xAA, 0xCC, 0xCC, 0xCC));
        (double yMin, double yMax) = GetYRange();
        for (int i = 0; i <= 4; i++)
        {
            float y = plot.Top + plot.Height * i / 4f;
            g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            double val = yMax - (yMax - yMin) * i / 4.0;
            string label = FormatAxis(val, _metric);
            g.DrawString(label, _axisFont, axisBrush, new PointF(2, y - 6));
        }

        // 折线
        if (_points.Length == 0)
        {
            g.DrawString("等待采样数据…", _hintFont, Brushes.Gray, plot.Left + 12, plot.Top + 10);
            return;
        }
        double span = yMax - yMin;
        if (span <= 0) span = 1;

        // 问题 21 修复：长时间轴（>1h 采样）点数过密，按桶降采样取每桶 min/max，避免糊成一条。
        // 采样后仍保留全部原始点用于末点标签/首末时间标签。
        PerfPoint[] displayPoints = Downsample(_points, plot.Width);
        int n = displayPoints.Length;
        var pts = new List<PointF>(n);
        for (int i = 0; i < n; i++)
        {
            var v = PerfAnalyzer.ValueOf(displayPoints[i], _metric);
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
        // 问题 20 单点保护：_points.Length ≥ 1 已由上方 guard 保证；使用 _points[^1] 安全。
        var last = _points[^1];
        var lv = PerfAnalyzer.ValueOf(last, _metric);
        if (lv != null)
        {
            using var brush = new SolidBrush(_lineColor);
            string text = $"{FormatAxis(lv.Value, _metric)}";
            var size = g.MeasureString(text, _curFont);
            g.DrawString(text, _curFont, brush, new PointF(plot.Right - size.Width, plot.Top - 2));
        }

        // 底部时间标签（首/末）
        using var tsBrush = new SolidBrush(Color.FromArgb(0x88, 0xCC, 0xCC, 0xCC));
        g.DrawString(_points[0].Ts.ToString("HH:mm:ss"), _tsFont, tsBrush, plot.Left, plot.Bottom + 3);
        // 时间标签预留宽度按 scale 缩放，避免高 DPI 下压线；宽度不足时右移防重叠
        int tsW = (int)Math.Round(56 * scale);
        g.DrawString(_points[^1].Ts.ToString("HH:mm:ss"), _tsFont, tsBrush, plot.Right - tsW, plot.Bottom + 3);
    }

    /// <summary>问题 32 修复：文本截断到指定字符数，超出追加省略号。用于图例/标题等固定宽度显示位。</summary>
    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        return s[..(max - 1)] + "…";
    }

    /// <summary>问题 21 修复：长时间轴降采样。
    /// 若点数 ≤ MaxVisiblePoints 或桶数不足以产生差异，返回原数组。
    /// 否则按桶宽分桶，每桶输出 min、max 两点（保形），时间戳取桶首点。
    /// </summary>
    private PerfPoint[] Downsample(PerfPoint[] src, int plotWidthPx)
    {
        if (src.Length <= MaxVisiblePoints || plotWidthPx <= 0) return src;
        int buckets = Math.Max(2, Math.Min(src.Length, MaxVisiblePoints));
        int bucketSize = (src.Length + buckets - 1) / buckets;
        if (bucketSize <= 1) return src;

        var outPts = new List<PerfPoint>(buckets * 2);
        for (int start = 0; start < src.Length; start += bucketSize)
        {
            int end = Math.Min(src.Length, start + bucketSize);
            double minV = double.PositiveInfinity, maxV = double.NegativeInfinity;
            PerfPoint minP = src[start], maxP = src[start];
            for (int i = start; i < end; i++)
            {
                var v = PerfAnalyzer.ValueOf(src[i], _metric);
                if (v == null) continue;
                if (v.Value < minV) { minV = v.Value; minP = src[i]; }
                if (v.Value > maxV) { maxV = v.Value; maxP = src[i]; }
            }
            // 桶内全空 → 至少保留一个 None 点让上层断开折线；用首点承载 Ts
            if (double.IsPositiveInfinity(minV)) { outPts.Add(src[start]); continue; }
            // 桶内 min/max 数值相等（同一时刻或全等值）→ 仅输出一个点，避免上层断开折线
            // 注意：PerfPoint 是 struct，不能用 ReferenceEquals 判等（值类型比较无意义），改用数值判等
            if (minV == maxV) outPts.Add(minP);
            else { outPts.Add(minP); outPts.Add(maxP); }
        }
        return outPts.ToArray();
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

    /// <summary>
    /// 问题 31 修复：单位统一。原实现在 vram_mb 上 v&gt;=1024 显示 G、否则 M，同一曲线不同刻度不同单位，可读性差；
    /// mem_gb 与 vram_mb 混用 G/M 前缀也易混淆。现改为固定单位 + 数字分级：
    ///   - vram_mb 一律 M（值直接输出，允许出现 4096M 等，避免 G/M 切换）
    ///   - mem_gb 一律 G
    ///   - tg_tps/pp_tps/cpu/ctx 按指标语义保留 %/裸数
    /// 分级精度：≥100 → 0 位小数；≥10 → 1 位；否则 2 位。
    /// </summary>
    private static string FormatAxis(double v, string metric)
    {
        switch (metric)
        {
            case "vram_mb": return $"{FormatNum(v)}M";
            case "mem_gb": return $"{FormatNum(v)}G";
            case "ctx": return $"{v * 100:F0}%";
            case "cpu": return $"{v:F0}%";
            case "tg_tps":
            case "pp_tps":
            case "tg_tps_total":
            case "pp_tps_total":
                return FormatNum(v);
            default: return FormatNum(v);
        }
    }

    /// <summary>分级格式化数字（≥100 无小数、≥10 一位、&lt;10 两位），避免长小数与 0 值混乱。</summary>
    private static string FormatNum(double v)
    {
        double a = Math.Abs(v);
        if (a >= 100) return $"{v:F0}";
        if (a >= 10) return $"{v:F1}";
        return $"{v:F2}";
    }
}
