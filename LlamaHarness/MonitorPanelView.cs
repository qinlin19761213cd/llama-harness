namespace LlamaHarness;

/// <summary>
/// 系统资源页 Controller：本地采集（CPU/内存/显存）+ llama.cpp 三接口（/slots /props /metrics）卡片 + Raw 折叠。
/// 手动刷新触发（无轮询）；采集完成后回调 StatusPanelView 更新右侧摘要/运行时长/崩溃熔断告警。
/// 自持全部卡片控件与布局，MainForm 仅把 BuildPage() 结果挂入系统资源页签。
/// </summary>
public sealed class MonitorPanelView : UserControl
{
    private readonly AppConfig _config;
    private readonly StatusPanelView _status;
    private readonly Func<bool> _isDisposed;
    private readonly Action<string> _appendLog;

    private readonly SystemMetrics _metrics = new();
    private LlamaCppMonitorCollector? _monitorCollector; // llama.cpp 采集器（懒初始化，端口确定后创建）
    private int _metricsBusy;

    // 卡片控件（BuildPage 中创建）
    private Button _btnRefreshRes = null!;
    private Label _lblResTimestamp = null!;
    private Panel _sysCard = null!;
    private Label _lblSysRes = null!;
    private Panel _slotsCard = null!;
    private Label _lblSlotsTitle = null!;
    private Label _lblSlotsBody = null!;
    private Button _btnRawSlots = null!;
    private TextBox _rawSlotsBox = null!;
    private Panel _propsCard = null!;
    private Label _lblPropsTitle = null!;
    private TableLayoutPanel _tblPropsBody = null!;
    private Button _btnRawProps = null!;
    private TextBox _rawPropsBox = null!;
    private Panel _metricsCard = null!;
    private Label _lblMetricsTitle = null!;
    private Label _lblMetricsBody = null!;
    private Button _btnRawMetrics = null!;
    private TextBox _rawMetricsBox = null!;

    public MonitorPanelView(AppConfig config, StatusPanelView status,
        Func<bool> isDisposed, Action<string> appendLog)
    {
        _config = config;
        _status = status;
        _isDisposed = isDisposed;
        _appendLog = appendLog;
    }

    /// <summary>构建系统资源页：可滚动 Panel + TableLayoutPanel 纵向布局（工具栏 + 4×标题/卡片）。</summary>
    public Control BuildPage()
    {
        Dock = DockStyle.Fill;
        BackColor = UiTheme.C_Bg;
        Padding = new Padding(10);
        AutoScroll = true;

        var resLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            BackColor = UiTheme.C_Bg,
            ColumnCount = 1,
            RowCount = 9,
            Padding = new Padding(0),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        for (int r = 0; r < 9; r++)
            resLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // 行0：顶部工具栏（[手动刷新] + 上次采集时间）
        var toolbarPanel = new Panel { Dock = DockStyle.Fill, Height = 52, BackColor = UiTheme.C_Bg };
        _btnRefreshRes = new Button
        {
            Text = "手动刷新",
            Dock = DockStyle.Top,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            BackColor = UiTheme.C_Primary,
            ForeColor = Color.Black,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand,
        };
        _btnRefreshRes.FlatAppearance.BorderSize = 0;
        _lblResTimestamp = new Label
        {
            Text = "尚未采集",
            Dock = DockStyle.Top,
            Height = 20,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Color.FromArgb(0x88, 0x88, 0x88),
            Font = new Font("Microsoft YaHei UI", 8F),
        };
        toolbarPanel.Controls.Add(_lblResTimestamp);
        toolbarPanel.Controls.Add(_btnRefreshRes);
        resLayout.Controls.Add(toolbarPanel, 0, 0);

        // 行1：系统资源标题 + 行2：系统资源卡片（本地采集：CPU / 内存 / 显存）
        resLayout.Controls.Add(UiTheme.MakeCardTitle("系统资源"), 0, 1);
        _sysCard = UiTheme.MakeCardPanel();
        _lblSysRes = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Consolas", 10F),
            ForeColor = UiTheme.C_TextFg,
            Padding = new Padding(8, 4, 8, 4),
            AutoSize = true,
            MaximumSize = new Size(0, 0),
        };
        _sysCard.Controls.Add(_lblSysRes);
        resLayout.Controls.Add(_sysCard, 0, 2);

        // 行3/4：/slots 标题 + 卡片
        _lblSlotsTitle = UiTheme.MakeCardTitle("/slots 槽位状态");
        resLayout.Controls.Add(_lblSlotsTitle, 0, 3);
        _slotsCard = UiTheme.MakeCardPanel();
        _lblSlotsBody = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            Font = new Font("Consolas", 9F),
            ForeColor = UiTheme.C_TextFg,
            Padding = new Padding(8, 4, 8, 4),
            AutoSize = true,
            MaximumSize = new Size(0, 0),
        };
        _btnRawSlots = UiTheme.MakeRawButton();
        _rawSlotsBox = UiTheme.MakeRawTextBox();
        _slotsCard.Controls.Add(_lblSlotsBody);
        _slotsCard.Controls.Add(_btnRawSlots);
        _slotsCard.Controls.Add(_rawSlotsBox);
        resLayout.Controls.Add(_slotsCard, 0, 4);

        // 行5/6：/props 标题 + 卡片（两列表格数据区 + Raw 按钮/TextBox）
        _lblPropsTitle = UiTheme.MakeCardTitle("/props 模型配置");
        resLayout.Controls.Add(_lblPropsTitle, 0, 5);
        _propsCard = UiTheme.MakeCardPanel();
        _tblPropsBody = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.C_Card,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(4),
        };
        _tblPropsBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30f));
        _tblPropsBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70f));
        _btnRawProps = UiTheme.MakeRawButton();
        _rawPropsBox = UiTheme.MakeRawTextBox();
        _propsCard.Controls.Add(_tblPropsBody);
        _propsCard.Controls.Add(_btnRawProps);
        _propsCard.Controls.Add(_rawPropsBox);
        resLayout.Controls.Add(_propsCard, 0, 6);

        // 行7/8：/metrics 标题 + 卡片
        _lblMetricsTitle = UiTheme.MakeCardTitle("/metrics 全局指标");
        resLayout.Controls.Add(_lblMetricsTitle, 0, 7);
        _metricsCard = UiTheme.MakeCardPanel();
        _lblMetricsBody = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            Font = new Font("Consolas", 9F),
            ForeColor = UiTheme.C_TextFg,
            Padding = new Padding(8, 4, 8, 4),
            AutoSize = true,
            MaximumSize = new Size(0, 0),
        };
        _btnRawMetrics = UiTheme.MakeRawButton();
        _rawMetricsBox = UiTheme.MakeRawTextBox();
        _metricsCard.Controls.Add(_lblMetricsBody);
        _metricsCard.Controls.Add(_btnRawMetrics);
        _metricsCard.Controls.Add(_rawMetricsBox);
        resLayout.Controls.Add(_metricsCard, 0, 8);

        Controls.Add(resLayout);

        _btnRefreshRes.Click += (_, _) => Refresh();
        _btnRawSlots.Click += (_, _) => ToggleRaw(_btnRawSlots, _rawSlotsBox);
        _btnRawProps.Click += (_, _) => ToggleRaw(_btnRawProps, _rawPropsBox);
        _btnRawMetrics.Click += (_, _) => ToggleRaw(_btnRawMetrics, _rawMetricsBox);
        return this;
    }

    /// <summary>手动刷新：采集系统资源（本地）+ llama.cpp 三接口（HTTP），更新页面 + 右侧摘要 + 崩溃熔断告警。</summary>
    public new async void Refresh()
    {
        if (Interlocked.Exchange(ref _metricsBusy, 1) == 1) return;
        try
        {
            // 1. 系统资源（本地采集，同步）
            double cpu = _metrics.GetCpuPercent();
            var (used, total) = _metrics.GetMemory();
            string? vram = await _metrics.GetVramTextAsync();

            // 2. llama.cpp 三接口（HTTP，懒初始化 collector）
            EnsureMonitorCollector();
            LlamaCppMonitorSnapshot? snap = null;
            if (_monitorCollector != null)
            {
                try
                {
                    snap = await _monitorCollector.CaptureSnapshotAsync();
                }
                catch
                {
                    // 采集失败（llama-server 未启动等），snap 保持 null
                }
            }

            if (_isDisposed()) return;

            // 3. 更新 UI
            _lblSysRes.Text =
                $"CPU:      {cpu:F0}%\n" +
                $"内存:     {used:F1} / {total:F1} GB\n" +
                $"显存:     {(vram ?? "—（未检测到 nvidia-smi）")}";

            UpdateSlotsCard(snap);
            UpdatePropsCard(snap);
            UpdateMetricsCard(snap);

            _lblResTimestamp.Text = $"上次采集: {DateTime.Now:HH:mm:ss}";

            // 右侧状态面板摘要（保持原有行为）
            // 右侧状态面板摘要（v2.18：CPU/内存/显存 三行，与本地监视卡同格式）
            _status.SetResSummary(
                $"CPU:  {cpu:F0}%\n" +
                $"内存: {used:F1}/{total:F1} GB\n" +
                $"显存: {vram ?? "—（未检测到 nvidia-smi）"}");
        }
        finally
        {
            Interlocked.Exchange(ref _metricsBusy, 0);
        }
    }

    /// <summary>懒初始化 llama.cpp 采集器（端口确定后创建一次）。</summary>
    private void EnsureMonitorCollector()
    {
        if (_monitorCollector != null) return;
        int port = _config.Port;
        _monitorCollector = new LlamaCppMonitorCollector($"http://127.0.0.1:{port}");
    }

    /// <summary>更新 /slots 卡片：槽位表格（ID/状态/cached/推理中）+ Raw 折叠。</summary>
    private void UpdateSlotsCard(LlamaCppMonitorSnapshot? snap)
    {
        if (snap == null || string.IsNullOrEmpty(snap.RawSlotsJson))
        {
            _lblSlotsTitle.Text = "  /slots 槽位状态  ✗ 不可用";
            _lblSlotsBody.Text = "llama-server 未启动或接口不可达";
            _btnRawSlots.Visible = false;
            _rawSlotsBox.Visible = false;
            return;
        }
        _lblSlotsTitle.Text = "  /slots 槽位状态  ✓";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Format("{0,-4} {1,-18} {2,8} {3,6} {4,8} {5,10}", "ID", "状态", "cached", "推理中", "spec", "n_ctx"));
        foreach (var s in snap.Slots)
        {
            sb.AppendLine(string.Format("{0,-4} {1,-18} {2,8} {3,6} {4,8} {5,10}", s.id, s.state_name, s.tokens_cached, s.is_processing ? "是" : "否", s.speculative ? "是" : "否", s.n_ctx));
        }
        if (snap.Slots.Count == 0) sb.AppendLine("（无槽位数据）");
        _lblSlotsBody.Text = sb.ToString();
        _btnRawSlots.Visible = true;
        _rawSlotsBox.Text = snap.RawSlotsJson;
    }

    /// <summary>更新 /props 卡片：模型全局配置（两列表格：左标签+右值）+ Raw 折叠。</summary>
    private void UpdatePropsCard(LlamaCppMonitorSnapshot? snap)
    {
        if (snap == null || string.IsNullOrEmpty(snap.RawPropsJson))
        {
            _lblPropsTitle.Text = "  /props 模型配置  ✗ 不可用";
            _tblPropsBody.Controls.Clear();
            var errLbl = new Label
            {
                Text = "llama-server 未启动或接口不可达",
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(0xFF, 0x66, 0x66),
                Font = new Font("Microsoft YaHei UI", 9F),
                Padding = new Padding(8, 4, 8, 4),
            };
            _tblPropsBody.Controls.Add(errLbl);
            _btnRawProps.Visible = false;
            _rawPropsBox.Visible = false;
            return;
        }
        _lblPropsTitle.Text = "  /props 模型配置  ✓";
        var p = snap.GlobalProps;

        _tblPropsBody.Controls.Clear();
        int rowIdx = 0;
        foreach (var kv in p.RawFields)
        {
            if (string.IsNullOrEmpty(kv.Value)) continue;
            if (kv.Key == "chat_template") continue;
            string fieldName = kv.Key.Contains('.') ? kv.Key.Split('.').Last() : kv.Key;
            string val = kv.Value.Length > 120 ? kv.Value[..120] + "…" : kv.Value;

            var lblKey = new Label
            {
                Text = fieldName,
                Dock = DockStyle.Fill,
                ForeColor = UiTheme.C_TextFg,
                Font = new Font("Microsoft YaHei UI", 9F),
                Padding = new Padding(8, 6, 4, 6),
                BorderStyle = BorderStyle.None,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            var lblVal = new Label
            {
                Text = val,
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(0xCC, 0xCC, 0xCC),
                Font = new Font("Consolas", 9F),
                Padding = new Padding(4, 6, 8, 6),
                BorderStyle = BorderStyle.None,
                TextAlign = ContentAlignment.MiddleLeft,
                MaximumSize = new Size(0, 0),
                AutoSize = true,
            };
            _tblPropsBody.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _tblPropsBody.Controls.Add(lblKey, 0, rowIdx);
            _tblPropsBody.Controls.Add(lblVal, 1, rowIdx);
            rowIdx++;
        }
        _btnRawProps.Visible = true;
        _rawPropsBox.Text = snap.RawPropsJson;
    }

    /// <summary>更新 /metrics 卡片：Prometheus 文本（显存/KV缓存/吞吐）+ Raw 折叠。</summary>
    private void UpdateMetricsCard(LlamaCppMonitorSnapshot? snap)
    {
        if (snap == null || string.IsNullOrEmpty(snap.RawMetricsText))
        {
            _lblMetricsTitle.Text = "  /metrics 全局指标  ✗ 不可用";
            _lblMetricsBody.Text = "llama-server 未启动或未带 --metrics 参数";
            _btnRawMetrics.Visible = false;
            _rawMetricsBox.Visible = false;
            return;
        }
        _lblMetricsTitle.Text = "  /metrics 全局指标  ✓";
        var lines = snap.RawMetricsText.Split('\n');
        var keyLines = lines.Where(l => l.Contains("memory") || l.Contains("kv_") || l.Contains("throughput") || l.Contains("tokens"))
                             .Take(10);
        _lblMetricsBody.Text = string.Join("\n", keyLines) + (keyLines.Count() < lines.Length ? "\n…（完整报文见下方折叠区）" : "");
        _btnRawMetrics.Visible = true;
        _rawMetricsBox.Text = snap.RawMetricsText;
    }

    /// <summary>切换 Raw 折叠区（TextBox）显示/隐藏，并强制重算布局。</summary>
    private static void ToggleRaw(Button btn, TextBox box)
    {
        bool show = !box.Visible;
        box.Visible = show;
        btn.Text = show ? "收起原始报文 ▴" : "查看原始报文 ▸";
        var parent = box.Parent as Panel;
        if (parent != null)
        {
            parent.Invalidate(true);
            parent.Update();
        }
    }
}
