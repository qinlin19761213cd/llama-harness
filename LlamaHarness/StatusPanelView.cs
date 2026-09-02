namespace LlamaHarness;

/// <summary>
/// 右侧状态面板 + 状态机 Controller：服务阶段/模块状态/系统资源/Token统计/槽位绑定/Restore/思考模式（v2.18 删运行时长卡片，资源/Token/槽位多行化）
/// 八卡片渲染、ApplyPhase 控件启停状态机、思考模式标签、崩溃熔断红色告警状态。
/// 自持右侧面板控件；外部操作按钮/参数控件由 MainForm 构建后 BindUi 注入（避免跨类散布控件）。
/// </summary>
public sealed class StatusPanelView : UserControl
{
    private readonly AppConfig _config;
    private readonly SmartScheduler _scheduler;
    private readonly Action<string> _appendLog;

    private Label _lblStatus = null!;       // 服务阶段卡片：调度器状态文本（运行中 · N个在途任务…）
    private FlowLayoutPanel _inFlightPanel = null!; // 服务阶段卡片内：在途任务明细列表（v2.18，每个任务一行）
    /// <summary>详情正常状态统一色（v2.19）：亮绿 Color.Lime，对齐 Restore 命中率卡片（原 C_TextFg/C_Primary/C_Aux 等统一收敛）。</summary>
    private static readonly Color C_DetailOk = Color.Lime;
    private Label _lblModuleState = null!;  // 模块状态（网关 运行中绿 / 已停止红）
    private Label _lblResSummary = null!;   // 系统资源单行摘要（CPU/内存/显存）
    private Label _lblTokenSummary = null!; // Token 统计摘要（请求数/速度/命中率）
    private Label _lblSlotSummary = null!;  // 槽位绑定摘要
    private Label _lblRestoreHit = null!;   // Restore 命中率卡片（3.1 可观测）
    private Label _lblThinking = null!;     // 思考模式标签（四档颜色）

    private DateTime? _wakeTime;            // 本次唤醒时刻（非 Running 为 null）
    private bool _crashAlertShown;          // 崩溃熔断红色告警状态（防重复告警）

    // 外部注入（v2.17.2 并入 BuildPage 参数，消除 BindUi 两步模式）：操作按钮 + 参数控件数组 + 端口 + 参数 CheckBox
    private Control[] _paramControls = null!;
    private TextBox _numPort = null!;
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
    public Control BuildPage(Control[] paramControls, TextBox numPort,
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
                BackColor = Color.Black, // 黑色标题栏条（v2.19 层次感）
                Dock = DockStyle.Top,
                Height = 26,
                ForeColor = UiTheme.C_Primary,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            content.Dock = DockStyle.Fill;
            content.TextAlign = ContentAlignment.MiddleLeft;
            card.Controls.Add(content);
            card.Controls.Add(lblTitle);
            return card;
        }

        _lblStatus = new Label
        {
            Text = "空闲",
            Font = new Font("Consolas", 9F),
            ForeColor = C_DetailOk,
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
            Text = "CPU: —\n内存: —\n显存: —",
            Font = new Font("Consolas", 9F),
            ForeColor = C_DetailOk,
        };
        _lblTokenSummary = new Label
        {
            Text = "请求: 0\n输入: —\n输出: —",
            Font = new Font("Consolas", 9F),
            ForeColor = C_DetailOk,
        };
        _lblSlotSummary = new Label
        {
            Text = "槽位: —",
            Font = new Font("Consolas", 9F),
            ForeColor = C_DetailOk,
        };
        _lblRestoreHit = new Label
        {
            Text = "Restore: 未启用",
            Font = new Font("Consolas", 9F),
            ForeColor = C_DetailOk,
        };
        _lblThinking = new Label
        {
            Text = "思考: 极速",
            Font = new Font("Consolas", 9F),
            ForeColor = C_DetailOk,
        };

        // 服务阶段卡片（v2.18 多行化）：标题 + 状态文本 + 在途任务明细列表（与 MakeCard 单 Label 卡片区分）
        var statusCard = new Panel
        {
            BackColor = UiTheme.C_Card,
            Padding = new Padding(12),
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8),
        };
        var statusTitle = new Label
        {
            Text = "服务阶段",
            BackColor = Color.Black, // 黑色标题栏条（v2.19 层次感）
            Dock = DockStyle.Top,
            Height = 26,
            ForeColor = UiTheme.C_Primary,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _inFlightPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 2, 0, 0),
        };
        _lblStatus.Dock = DockStyle.Top;
        _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        statusCard.Controls.Add(_inFlightPanel); // 后加的 Dock=Top 排上：Title → 状态文本 → 明细
        statusCard.Controls.Add(_lblStatus);
        statusCard.Controls.Add(statusTitle);

        var cards = new[]
        {
            statusCard,
            MakeCard("模块状态", _lblModuleState),
            MakeCard("系统资源", _lblResSummary),
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
            SmartScheduler.Phase.Running => C_DetailOk,
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

    /// <summary>刷新服务阶段卡片在途任务明细（InFlightChanged 事件驱动，UI 线程）：每个在途任务一行「应用 · 方法 路径」。</summary>
    public void RefreshInFlightTasks()
    {
        _inFlightPanel.Controls.Clear();
        foreach (var t in _scheduler.GetInFlightTasks())
        {
            var lbl = new Label
            {
                AutoSize = true,
                Font = new Font("Consolas", 9F),
                ForeColor = C_DetailOk,
                Margin = new Padding(0, 1, 0, 1),
                Text = $"• {(t.App ?? "未知")} · {t.Method} {t.Path}",
            };
            _inFlightPanel.Controls.Add(lbl);
        }
    }

    /// <summary>更新思考模式标签文本和颜色（四档：极速/轻度/中度/深度）。</summary>
    public void UpdateThinkingLabel(SmartScheduler.ThinkingLevel level)
    {
        _lblThinking.Text = $"思考: {ThinkingMode.LabelOf(level)}";
        _lblThinking.ForeColor = level switch
        {
            SmartScheduler.ThinkingLevel.Off => Color.Silver, // 关闭=未激活灰；开启档位统一亮绿（v2.19）
            SmartScheduler.ThinkingLevel.Low => C_DetailOk,
            SmartScheduler.ThinkingLevel.Medium => C_DetailOk,
            _ => C_DetailOk, // XHigh 统一亮绿
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
            _lblRestoreHit.ForeColor = C_DetailOk;
            return;
        }
        var s = stats.Snapshot();
        if (s.TotalAttempts == 0)
        {
            _lblRestoreHit.Text = "Restore: 等待首次判定…";
            _lblRestoreHit.ForeColor = C_DetailOk;
            return;
        }
        double pct = s.HitRate * 100;
        _lblRestoreHit.ForeColor = pct < 50 ? Color.Red : pct < 80 ? Color.Gold : C_DetailOk;
        string last = s.Last != null
            ? $"\n最近: {s.Last.Key} {(s.Last.Hit ? "HIT" : "MISS")} Δ{s.Last.PromptEvalTokens}tok (saved {s.Last.SavedN})"
            : "";
        // v2.23.10 前缀漂移告警：出现即红色提醒（KV 增量复用失效，检查前缀稳定性）
        int drift = stats.DriftAlertCount;
        string driftText = drift > 0 ? $" | ⚠前缀漂移×{drift}" : "";
        if (drift > 0) _lblRestoreHit.ForeColor = Color.FromArgb(0xF5, 0x3F, 0x3F);
        // v2.23.11 ROI 量化：KV 复用累计 token 数 + 折算节省的 prefill 时间（回答"KV 复用值不值"）
        var roi = stats.PerfSnapshot();
        string roiText = roi.ReuseTokens > 0
            ? $" | 复用 {roi.ReuseTokens / 1000.0:F1}Ktok 省~{roi.ReuseSavedMs / 1000.0:F1}s"
            : "";
        _lblRestoreHit.Text = $"命中率: {pct:F1}% ({s.TotalHits}/{s.TotalAttempts}) | 误报: {s.FalseRate * 100:F1}%{driftText}{roiText}{last}";
    }

    /// <summary>系统资源摘要（CPU/内存）→ 右侧面板。</summary>
    public void SetResSummary(string text) => _lblResSummary.Text = text;

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
