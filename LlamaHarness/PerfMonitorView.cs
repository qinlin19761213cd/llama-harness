namespace LlamaHarness;

/// <summary>
/// 性能监控页 Controller（v2.21）：左侧「性能监控」页签内容。
/// 实时趋势图（自绘）+ 实时数字摘要 + 请求时延统计 + 阈值告警列表 + perf.log 会话摘要。
/// 数据源：PerfSampler（周期采样环形缓冲）+ RequestTimingTracker（请求时延）+ PerfAnalyzer（双源共享分析内核）。
/// 1s 定时刷新（UI 线程 System.Windows.Forms.Timer）；告警按 metric+level 5 分钟去重防刷屏。
/// </summary>
public sealed class PerfMonitorView : UserControl
{
    private readonly PerfSampler _sampler;
    private readonly RequestTimingTracker _timing;
    private readonly IReadOnlyList<PerfThresholdRule> _rules;
    private readonly Action<string>? _appendLog;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Dictionary<string, DateTime> _lastAlarmByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PerfAlarm> _alarmLog = new();

    /// <summary>告警同键去重窗口（分钟）。</summary>
    private const double AlarmDedupMinutes = 5;
    /// <summary>趋势图数据点数（1s 采样 × 300 ≈ 5 分钟窗口）。</summary>
    private const int ChartPoints = 300;
    /// <summary>阈值检测窗口点数（覆盖最长持续 60s 规则）。</summary>
    private const int AlarmWindowPoints = 120;

    private PerfTrendChart _chart = null!;
    private string _currentMetric = "vram_mb";
    private Label _lblCpu = null!, _lblMem = null!, _lblVram = null!, _lblTg = null!, _lblCtx = null!, _lblInflight = null!;
    private Label _lblReqTotal = null!, _lblReqFail = null!, _lblAvgTotal = null!, _lblMaxTotal = null!, _lblAvgBackend = null!, _lblFailRate = null!;
    private Label _lblAlarms = null!;
    private Label _lblLogSummary = null!;
    private Label _lblPerfTimestamp = null!;
    private readonly Button[] _metricBtns = new Button[4];

    public PerfMonitorView(PerfSampler sampler, RequestTimingTracker timing,
        IReadOnlyList<PerfThresholdRule> rules, Action<string>? appendLog = null)
    {
        _sampler = sampler;
        _timing = timing;
        _rules = rules ?? PerfThresholdRule.Defaults();
        _appendLog = appendLog;
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => OnTick();
    }

    /// <summary>构建监控页：可滚动布局 + 趋势图 + 三卡片 + 摘要卡。</summary>
    public Control BuildPage()
    {
        Dock = DockStyle.Fill;
        BackColor = UiTheme.C_Bg;
        Padding = new Padding(10);
        AutoScroll = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            BackColor = UiTheme.C_Bg,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0),
        };

        // 行0：工具栏（标题 + 指标切换按钮 + 时间戳）
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        var toolbar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = UiTheme.C_Bg };
        var title = new Label
        {
            Text = "性能监控（1s 采样 · 5min 趋势 · 阈值告警）",
            Dock = DockStyle.Left,
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            ForeColor = UiTheme.C_TextFg,
            Padding = new Padding(0, 6, 8, 0),
        };
        _lblPerfTimestamp = new Label
        {
            Text = "",
            Dock = DockStyle.Right,
            AutoSize = true,
            ForeColor = Color.FromArgb(0x88, 0x88, 0x88),
            Font = new Font("Microsoft YaHei UI", 8F),
            Padding = new Padding(0, 10, 4, 0),
        };
        string[] metrics = { "显存", "CPU", "吞吐", "KV占用" };
        string[] keys = { "vram_mb", "cpu", "tg_tps", "ctx" };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = UiTheme.C_Bg,
            Padding = new Padding(4, 4, 4, 0),
        };
        for (int i = 0; i < metrics.Length; i++)
        {
            int idx = i;
            var b = new Button
            {
                Text = metrics[i],
                Width = 64,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Font = new Font("Microsoft YaHei UI", 8.5F),
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += (_, _) => { _currentMetric = keys[idx]; RefreshMetricBtns(); _chart.SetData(_sampler.Series.Last(ChartPoints), _currentMetric); };
            _metricBtns[i] = b;
            flow.Controls.Add(b);
        }
        toolbar.Controls.Add(_lblPerfTimestamp);
        toolbar.Controls.Add(flow);
        toolbar.Controls.Add(title);
        layout.Controls.Add(toolbar, 0, 0);
        RefreshMetricBtns();

        // 行1：趋势图卡片（固定 300 高；禁 AutoSize——TLP AutoSize 行按 PreferredSize 塌陷固定高度子控件）
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 300));
        var chartCard = MakePerfCard();
        _chart = new PerfTrendChart { Dock = DockStyle.Fill, Margin = new Padding(4) };
        chartCard.Controls.Add(_chart);
        layout.Controls.Add(chartCard, 0, 1);

        // 行2：实时数字卡片（固定 120 高；禁 AutoSize）
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
        var numCard = MakePerfCard();
        var numGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.C_Card,
            ColumnCount = 3,
            RowCount = 2,
            Padding = new Padding(4),
        };
        for (int c = 0; c < 3; c++) numGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        for (int r = 0; r < 2; r++) numGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
        AddNumCell(numGrid, 0, 0, "CPU", out _lblCpu);
        AddNumCell(numGrid, 1, 0, "内存", out _lblMem);
        AddNumCell(numGrid, 2, 0, "显存", out _lblVram);
        AddNumCell(numGrid, 0, 1, "生成吞吐", out _lblTg);
        AddNumCell(numGrid, 1, 1, "KV 上下文", out _lblCtx);
        AddNumCell(numGrid, 2, 1, "在途请求", out _lblInflight);
        numCard.Controls.Add(numGrid);
        layout.Controls.Add(numCard, 0, 2);

        // 行3：请求时延卡片（固定 120 高；禁 AutoSize）
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
        var timingCard = MakePerfCard();
        var tGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.C_Card,
            ColumnCount = 3,
            RowCount = 2,
            Padding = new Padding(4),
        };
        for (int c = 0; c < 3; c++) tGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        for (int r = 0; r < 2; r++) tGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
        AddNumCell(tGrid, 0, 0, "请求数", out _lblReqTotal);
        AddNumCell(tGrid, 1, 0, "失败", out _lblReqFail);
        AddNumCell(tGrid, 2, 0, "失败率", out _lblFailRate);
        AddNumCell(tGrid, 0, 1, "平均总时延", out _lblAvgTotal);
        AddNumCell(tGrid, 1, 1, "最大总时延", out _lblMaxTotal);
        AddNumCell(tGrid, 2, 1, "平均后端时延", out _lblAvgBackend);
        timingCard.Controls.Add(tGrid);
        layout.Controls.Add(timingCard, 0, 3);

        // 行4：告警卡片
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var alarmTitle = UiTheme.MakeCardTitle("告警（阈值持续窗口）");
        layout.Controls.Add(alarmTitle, 0, 4);
        var alarmCard = UiTheme.MakeCardPanel();
        _lblAlarms = new Label
        {
            Dock = DockStyle.Fill,
            Text = "暂无告警",
            TextAlign = ContentAlignment.TopLeft,
            Font = new Font("Consolas", 9F),
            ForeColor = UiTheme.C_TextFg,
            Padding = new Padding(8, 4, 8, 4),
            AutoSize = true,
        };
        alarmCard.Controls.Add(_lblAlarms);
        layout.Controls.Add(alarmCard, 0, 5);

        // 行6：perf.log 会话摘要卡片（固定 220 高；按钮 Dock=Bottom 需固定卡片底）
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 220));
        var logTitle = UiTheme.MakeCardTitle("perf.log 会话摘要（离线分析）");
        layout.Controls.Add(logTitle, 0, 6);
        var logCard = MakePerfCard();
        _lblLogSummary = new Label
        {
            Dock = DockStyle.Fill,
            Text = "尚未读取",
            TextAlign = ContentAlignment.TopLeft,
            Font = new Font("Consolas", 9F),
            ForeColor = UiTheme.C_TextFg,
            Padding = new Padding(8, 4, 8, 4),
            AutoSize = true,
        };
        var btnRefreshLog = new Button
        {
            Text = "刷新日志摘要",
            Dock = DockStyle.Bottom,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            BackColor = UiTheme.C_Btn,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            Font = new Font("Microsoft YaHei UI", 9F),
        };
        btnRefreshLog.FlatAppearance.BorderSize = 0;
        btnRefreshLog.Click += (_, _) => RefreshLogSummary();
        logCard.Controls.Add(_lblLogSummary);
        logCard.Controls.Add(btnRefreshLog);
        layout.Controls.Add(logCard, 0, 7);

        Controls.Add(layout);

        // 订阅请求时延告警（事件驱动）
        _timing.Completed += OnTimingCompleted;

        // 启动 1s 刷新（首帧立即刷新一次）
        RefreshLogSummary();
        OnTick();
        _timer.Start();
        return this;
    }

    /// <summary>创建固定高度卡片容器（Dock=Fill + 禁 AutoSize）。</summary>
    /// <remarks>不能用 UiTheme.MakeCardPanel：其 AutoSize=true，在 TableLayoutPanel 的 AutoSize 行里行高按 PreferredSize 计算，固定 Height 的卡片会塌陷为 0。</remarks>
    private static Panel MakePerfCard() => new()
    {
        Dock = DockStyle.Fill,
        BackColor = UiTheme.C_Card,
        Padding = new Padding(8),
    };

    /// <summary>释放：停止定时器并取消事件订阅（防泄漏）。</summary>
    public void Shutdown()
    {
        _timer.Stop();
        _timer.Dispose();
        _timing.Completed -= OnTimingCompleted;
    }

    // —— 刷新 ——

    private void OnTick()
    {
        if (IsDisposed) return;
        // 趋势图
        _chart.SetData(_sampler.Series.Last(ChartPoints), _currentMetric);
        // 实时数字
        var summary = PerfAnalyzer.ComputeSummary(_sampler.Series.Snapshot());
        _lblCpu.Text = summary.AvgCpu is double c ? $"{c:F1}%" : "—";
        var last = _sampler.LastPoint;
        if (last is PerfPoint lp)
        {
            _lblMem.Text = lp.MemUsedGb is double mu && lp.MemTotalGb is double mt ? $"{mu:F1}/{mt:F0} GB" : "—";
            _lblCtx.Text = lp.CtxUsedPct is double cx ? $"{cx * 100:F0}%" : "—";
            _lblInflight.Text = lp.Inflight?.ToString() ?? "—";
        }
        else
        {
            _lblCtx.Text = "—";
            _lblInflight.Text = "—";
        }
        _lblVram.Text = summary.LastVramMb is double lv ? $"{lv / 1024:F1}G" : "—";
        _lblTg.Text = summary.AvgTgTps is double tg ? $"{tg:F1} t/s" : "—";

        // 请求时延统计
        var stats = _timing.Stats();
        _lblReqTotal.Text = stats.Total.ToString();
        _lblReqFail.Text = stats.Failed.ToString();
        _lblFailRate.Text = stats.Total > 0 ? $"{100.0 * stats.Failed / stats.Total:F1}%" : "—";
        _lblAvgTotal.Text = $"{stats.AvgTotalMs:F0} ms";
        _lblMaxTotal.Text = $"{stats.MaxTotalMs:F0} ms";
        _lblAvgBackend.Text = $"{stats.AvgBackendMs:F0} ms";

        // 周期指标阈值告警（对最近窗口检测 + 去重）
        CheckAlarms();
        _lblPerfTimestamp.Text = $"更新于 {DateTime.Now:HH:mm:ss}";
    }

    private void CheckAlarms()
    {
        var pts = _sampler.Series.Last(AlarmWindowPoints);
        if (pts.Length < 2) return;
        var alarms = PerfAnalyzer.EvaluatePoints(pts, _rules);
        foreach (var a in alarms) AddAlarm(a);
    }

    private void OnTimingCompleted(RequestTiming t)
    {
        var alarms = PerfAnalyzer.EvaluateTiming(t, _rules);
        if (alarms.Count == 0) return;
        // Timing.Completed 可能来自后台线程：切回 UI 线程改控件（handle 未创建/关闭中则丢弃）
        if (!IsHandleCreated) return;
        try { BeginInvoke(new Action(() => { foreach (var a in alarms) AddAlarm(a); })); }
        catch { /* 窗体关闭中 */ }
    }

    private void AddAlarm(PerfAlarm a)
    {
        string key = $"{a.Metric}:{a.Level}";
        lock (_lastAlarmByKey)
        {
            if (_lastAlarmByKey.TryGetValue(key, out var prev)
                && (DateTime.Now - prev).TotalMinutes < AlarmDedupMinutes)
                return;
            _lastAlarmByKey[key] = DateTime.Now;
        }
        lock (_alarmLog)
        {
            _alarmLog.Add(a);
            while (_alarmLog.Count > 20) _alarmLog.RemoveAt(0);
        }
        _appendLog?.Invoke($"[PERF-ALARM] {a.Message}");
        RenderAlarms();
    }

    private void RenderAlarms()
    {
        if (IsDisposed) return;
        List<PerfAlarm> snap;
        lock (_alarmLog) snap = new List<PerfAlarm>(_alarmLog);
        if (snap.Count == 0)
        {
            _lblAlarms.Text = "暂无告警";
            _lblAlarms.ForeColor = UiTheme.C_TextFg;
            return;
        }
        var sb = new System.Text.StringBuilder();
        foreach (var a in snap)
        {
            string color = a.Level == PerfAlarmLevel.Crit ? "Red" : "Yellow";
            sb.AppendLine($"{a.Ts:HH:mm:ss}  {a.Message}");
        }
        _lblAlarms.Text = sb.ToString();
        _lblAlarms.ForeColor = Color.White;
    }

    private void RefreshLogSummary()
    {
        try
        {
            var s = PerfAnalyzer.ParsePerfLog(AppPaths.PerfLog);
            if (s.IsEmpty)
            {
                _lblLogSummary.Text = $"perf.log 为空或不存在（{s.Path}）";
                return;
            }
            _lblLogSummary.Text =
                $"文件: {s.Path}\n" +
                $"范围: {s.FirstTs:MM-dd HH:mm:ss} ~ {s.LastTs:MM-dd HH:mm:ss}  共 {s.TotalLines} 行\n" +
                $"采样: system {s.SystemCount} / cpp {s.CppCount} / timing {s.TimingCount}\n" +
                $"请求: {s.Requests} 次（失败 {s.FailedRequests}，失败率 {s.FailureRate * 100:F1}%）\n" +
                $"时延: 平均 {s.AvgTotalMs:F0} ms / 最大 {s.MaxTotalMs:F0} ms\n" +
                $"峰值显存: {(s.MaxVramMb is double mv ? $"{mv / 1024:F1}G" : "—")}  最低吞吐: {(s.MinTgTps is double mt ? $"{mt:F1} t/s" : "—")}";
        }
        catch (Exception ex)
        {
            _lblLogSummary.Text = $"perf.log 读取失败：{ex.Message}";
        }
    }

    // —— 控件辅助 ——

    private void RefreshMetricBtns()
    {
        for (int i = 0; i < _metricBtns.Length; i++)
        {
            string[] keys = { "vram_mb", "cpu", "tg_tps", "ctx" };
            bool sel = keys[i] == _currentMetric;
            _metricBtns[i].BackColor = sel ? UiTheme.C_Primary : UiTheme.C_Btn;
            _metricBtns[i].ForeColor = sel ? Color.Black : Color.White;
        }
    }

    private static void AddNumCell(TableLayoutPanel grid, int col, int row, string title, out Label valueLabel)
    {
        var cell = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.C_TextBg, Margin = new Padding(3) };
        var t = new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = 18,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(0xAA, 0xAA, 0xAA),
            Font = new Font("Microsoft YaHei UI", 8F),
            Padding = new Padding(6, 2, 2, 0),
        };
        valueLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "—",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.C_Green, // 正常状态统一亮绿（v2.19 规范）
            Font = new Font("Consolas", 11F, FontStyle.Bold),
            Padding = new Padding(6, 0, 2, 2),
        };
        cell.Controls.Add(valueLabel);
        cell.Controls.Add(t);
        grid.Controls.Add(cell, col, row);
    }
}
