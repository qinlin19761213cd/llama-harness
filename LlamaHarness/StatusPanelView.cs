namespace LlamaHarness;

/// <summary>
/// 右侧状态面板 + 状态机 Controller：服务阶段/模块状态/系统资源/运行时长/Token统计/槽位绑定/Restore/思考模式
/// 八卡片渲染、ApplyPhase 控件启停状态机、思考模式标签、崩溃熔断红色告警状态。
/// 自持右侧面板控件；外部操作按钮/参数控件由 MainForm 构建后 BindUi 注入（避免跨类散布控件）。
/// </summary>
public sealed class StatusPanelView : UserControl
{
    private readonly AppConfig _config;
    private readonly SmartScheduler _scheduler;
    private readonly Action<string> _appendLog;

    private Label _lblStatus = null!;       // 服务阶段卡片：调度器状态文本（运行中 · N个在途任务…）
    private Label _lblModuleState = null!;  // 模块状态（网关 运行中绿 / 已停止红）
    private Label _lblResSummary = null!;   // 系统资源单行摘要（CPU/内存/显存）
    private Label _lblRunTime = null!;      // 运行时长（自本次唤醒起）
    private Label _lblTokenSummary = null!; // Token 统计摘要（请求数/速度/命中率）
    private Label _lblSlotSummary = null!;  // 槽位绑定摘要
    private Label _lblRestoreHit = null!;   // Restore 命中率卡片（3.1 可观测）
    private Label _lblThinking = null!;     // 思考模式标签（四档颜色）

    private DateTime? _wakeTime;            // 本次唤醒时刻（非 Running 为 null）
    private bool _crashAlertShown;          // 崩溃熔断红色告警状态（防重复告警）

    // 外部注入（v2.17.2 并入 BuildPage 参数，消除 BindUi 两步模式）：操作按钮 + 参数控件数组 + 端口 + 参数 CheckBox
    private Control[] _paramControls = null!;
    private NumericUpDown _numPort = null!;
    private Button _btnStart = null!, _btnStop = null!, _btnThinkOn = null!, _btnTurbo = null!;
    private Button _btnClearLog = null!, _btnClearCache = null!, _btnExportCfg = null!, _btnImportCfg = null!;
    private CheckBox[] _paramCheckBoxes = null!;

    public StatusPanelView(AppConfig config, SmartScheduler scheduler, Action<string> appendLog)
    {
        _config = config;
        _scheduler = scheduler;
        _appendLog = appendLog;
    }


    /// <summary>构建右侧状态面板（30% 列）：八卡片纵向等高堆叠 + 一次性注入外部控件（操作按钮/参数控件/端口/参数CheckBox），
    /// 消除先构造后注入的 BindUi 两步模式（v2.17.2）：ApplyPhase 据此启停，注入点唯一、由调用方签名强制。</summary>
    public Control BuildPage(Control[] paramControls, NumericUpDown numPort,
        Button start, Button stop, Button thinkOn, Button turbo,
        Button clearLog, Button clearCache, Button exportCfg, Button importCfg,
        CheckBox[] paramCheckBoxes)
    {
        _paramControls = paramControls;
        _numPort = numPort;
        _btnStart = start;
        _btnStop = stop;
        _btnThinkOn = thinkOn;
        _btnTurbo = turbo;
        _btnClearLog = clearLog;
        _btnClearCache = clearCache;
        _btnExportCfg = exportCfg;
        _btnImportCfg = importCfg;
        _paramCheckBoxes = paramCheckBoxes;

        // 容器自身填满 Panel2（30% 列）：缺失此设置 UserControl 以默认 150x150 挂在左上角，8 卡片被压扁不可见
        Dock = DockStyle.Fill;
        BackColor = UiTheme.C_Frame;
        Padding = new Padding(12);
        AutoScroll = true;
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = Color.Transparent,
        };

        Panel MakeCard(string title, Label content)
        {
            var card = new Panel
            {
                BackColor = UiTheme.C_Card,
                Padding = new Padding(12),
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, 8),
            };
            var lblTitle = new Label
            {
                Text = title,
                Dock = DockStyle.Top,
                Height = 26,
                ForeColor = UiTheme.C_Title,
                Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            content.Dock = DockStyle.Fill;
            content.TextAlign = ContentAlignment.MiddleCenter;
            card.Controls.Add(content);
            card.Controls.Add(lblTitle);
            return card;
        }

        _lblStatus = new Label
        {
            Text = "空闲",
            Font = new Font("Microsoft YaHei UI", 9F),
            ForeColor = UiTheme.C_Aux,
        };
        _lblModuleState = new Label
        {
            Text = "网关 已停止",
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = UiTheme.C_Red,
            Padding = new Padding(8, 4, 8, 4),
        };
        _lblResSummary = new Label
        {
            Text = "CPU: — | 内存: —",
            Font = new Font("Consolas", 9F),
            ForeColor = UiTheme.C_TextFg,
        };
        _lblRunTime = new Label
        {
            Text = "—",
            Font = new Font("Consolas", 11F),
            ForeColor = UiTheme.C_Primary,
        };
        _lblTokenSummary = new Label
        {
            Text = "请求: 0",
            Font = new Font("Consolas", 11F),
            ForeColor = UiTheme.C_Primary,
        };
        _lblSlotSummary = new Label
        {
            Text = "槽位: —",
            Font = new Font("Consolas", 11F),
            ForeColor = UiTheme.C_TextFg,
        };
        _lblRestoreHit = new Label
        {
            Text = "Restore: 未启用",
            Font = new Font("Consolas", 11F),
            ForeColor = UiTheme.C_TextFg,
        };
        _lblThinking = new Label
        {
            Text = "思考: 极速",
            Font = new Font("Microsoft YaHei UI", 11F),
            ForeColor = Color.Silver,
        };

        var cards = new[]
        {
            MakeCard("服务阶段", _lblStatus),
            MakeCard("模块状态", _lblModuleState),
            MakeCard("系统资源", _lblResSummary),
            MakeCard("运行时长", _lblRunTime),
            MakeCard("Token 统计", _lblTokenSummary),
            MakeCard("槽位绑定", _lblSlotSummary),
            MakeCard("Restore 命中率", _lblRestoreHit),
            MakeCard("思考模式", _lblThinking),
        };
        for (int i = 0; i < cards.Length; i++)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / cards.Length));
            grid.Controls.Add(cards[i], 0, i);
        }

        Controls.Add(grid);
        return this;
    }

    /// <summary>阶段切换状态机：控件启停 + 状态颜色；唤醒 = 新会话。（UI 线程）</summary>
    public void ApplyPhase(SmartScheduler.Phase phase)
    {
        _wakeTime = phase == SmartScheduler.Phase.Running ? (_wakeTime ?? DateTime.Now) : null;

        bool busy = phase is SmartScheduler.Phase.Waking
                    or SmartScheduler.Phase.Running
                    or SmartScheduler.Phase.Sleeping;
        _btnStart.Enabled = !busy;
        _btnStop.Enabled = busy;
        _btnThinkOn.Enabled = _btnTurbo.Enabled = phase == SmartScheduler.Phase.Running;

        bool running = phase == SmartScheduler.Phase.Running;
        _lblModuleState.Text = running ? "网关 运行中" : (phase == SmartScheduler.Phase.Standby ? "网关 已停止" : "网关 过渡中");
        _lblModuleState.BackColor = running ? UiTheme.C_Green : (phase == SmartScheduler.Phase.Standby ? UiTheme.C_Red : UiTheme.C_Warn);

        foreach (var c in _paramControls)
            c.Enabled = !busy;
        if (_config.AutoMode)
            _numPort.Enabled = false;

        _lblStatus.Text = phase switch
        {
            SmartScheduler.Phase.Running => "运行",
            SmartScheduler.Phase.Waking => "唤醒中",
            SmartScheduler.Phase.Warming => "预热中",
            SmartScheduler.Phase.Sleeping => "休眠",
            _ => "空闲",
        };
        _lblStatus.ForeColor = phase switch
        {
            SmartScheduler.Phase.Running => Color.Green,
            SmartScheduler.Phase.Waking => Color.DarkOrange,
            SmartScheduler.Phase.Warming => Color.DarkOrange,
            SmartScheduler.Phase.Sleeping => Color.DarkOrange,
            _ => Color.Gray,
        };

        foreach (var b in new[] { _btnStart, _btnStop, _btnClearLog, _btnClearCache, _btnThinkOn, _btnTurbo, _btnExportCfg, _btnImportCfg })
            if (b != null) b.ForeColor = Color.White;

        var checkColor = busy ? Color.FromArgb(0x88, 0x88, 0x88) : Color.Black;
        foreach (var cb in _paramCheckBoxes)
            cb.ForeColor = checkColor;
    }

    /// <summary>调度器状态文本（在途任务等）覆盖服务阶段卡片文本（不改色）。</summary>
    public void SetStatusText(string text) => _lblStatus.Text = text;

    /// <summary>更新思考模式标签文本和颜色（四档：极速/轻度/中度/深度）。</summary>
    public void UpdateThinkingLabel(SmartScheduler.ThinkingLevel level)
    {
        _lblThinking.Text = $"思考: {ThinkingMode.LabelOf(level)}";
        _lblThinking.ForeColor = level switch
        {
            SmartScheduler.ThinkingLevel.Off => Color.Silver,
            SmartScheduler.ThinkingLevel.Low => Color.LightGreen,
            SmartScheduler.ThinkingLevel.Medium => Color.DodgerBlue,
            _ => Color.LightBlue, // XHigh
        };
    }

    /// <summary>按当前启动附加参数刷新思考模式标签（仅显示；权威重置在 SmartScheduler 唤醒时执行）。</summary>
    public void RefreshThinkingLabel()
        => UpdateThinkingLabel(ThinkingMode.DetermineInitialThinkingMode(_config.ExtraArgs));

    /// <summary>3.1 Restore 命中率卡片：总命中率 + 误报率 + 最近一次明细；颜色按阈值（≥80% 绿 / &lt;80% 黄 / &lt;50% 红）。</summary>
    public void UpdateRestoreCard()
    {
        var stats = _scheduler.GetRestoreStats();
        if (stats == null)
        {
            _lblRestoreHit.Text = "Restore: 未启用";
            _lblRestoreHit.ForeColor = UiTheme.C_TextFg;
            return;
        }
        var s = stats.Snapshot();
        if (s.TotalAttempts == 0)
        {
            _lblRestoreHit.Text = "Restore: 等待首次判定…";
            _lblRestoreHit.ForeColor = UiTheme.C_TextFg;
            return;
        }
        double pct = s.HitRate * 100;
        _lblRestoreHit.ForeColor = pct < 50 ? Color.Red : pct < 80 ? Color.Gold : Color.Lime;
        string last = s.Last != null
            ? $"\n最近: {s.Last.Key} {(s.Last.Hit ? "HIT" : "MISS")} Δ{s.Last.PromptEvalTokens}tok (saved {s.Last.SavedN})"
            : "";
        _lblRestoreHit.Text = $"命中率: {pct:F1}% ({s.TotalHits}/{s.TotalAttempts}) | 误报: {s.FalseRate * 100:F1}%{last}";
    }

    /// <summary>系统资源摘要（CPU/内存）→ 右侧面板。</summary>
    public void SetResSummary(string text) => _lblResSummary.Text = text;

    /// <summary>运行时长卡片（自本次唤醒起；非 Running 显示 —）。</summary>
    public void UpdateRunTime()
        => _lblRunTime.Text = _wakeTime is DateTime wt ? (DateTime.Now - wt).ToString(@"hh\:mm\:ss") : "—";

    /// <summary>Token 统计摘要 → 右侧面板（与统计页汇总同步）。</summary>
    public void SetTokenSummary(string text) => _lblTokenSummary.Text = text;

    /// <summary>槽位绑定摘要 → 右侧面板。</summary>
    public void SetSlotSummary(string text) => _lblSlotSummary.Text = text;

    /// <summary>崩溃熔断红色告警（系统资源采集后回调）：首次跳闸变红告警，恢复后按当前相位复位。</summary>
    public void CheckCrashCircuit(bool tripped)
    {
        if (tripped && !_crashAlertShown)
        {
            _crashAlertShown = true;
            _appendLog("⚠⚠ 崩溃熔断器已跳闸：10 分钟内 ≥3 次 bad_alloc，自动恢复已停止。请加内存 / 降上下文后手动重试！");
            _lblStatus.ForeColor = Color.FromArgb(0xF5, 0x3F, 0x3F);
            _lblStatus.Text = "⚠ 崩溃熔断：自动恢复已停止，需人工介入";
        }
        else if (!tripped && _crashAlertShown)
        {
            _crashAlertShown = false;
            ApplyPhase(_scheduler.CurrentPhase);
        }
    }
}
