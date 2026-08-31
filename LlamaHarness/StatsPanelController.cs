namespace LlamaHarness;

/// <summary>
/// 统计区 Controller：实时解析 print_timing 的表格（时间/输入/输出/命中率/f_sim_best/总耗时）+ 累计汇总。
/// 自持 _statsParser/_gridStats/_lblSummary/_btnClearStats；解析器事件来自进程输出线程，经 uiReady/invokeOnUi
/// 委托切回 UI 线程（不直接依赖 MainForm 类型，便于测试）。
/// </summary>
public sealed class StatsPanelController
{
    private readonly SmartScheduler _scheduler;
    private readonly StatusPanelController _status; // 汇总同步到右侧 Token 统计 + Restore 卡片
    private readonly Func<bool> _uiReady;
    private readonly Action<Action> _invokeOnUi;

    private readonly LlamaStatsParser _statsParser = new();
    private readonly Label _lblSummary = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = UiTheme.C_Primary,
        Font = new Font("Consolas", 9F),
        Margin = new Padding(4, 4, 4, 4),
    };
    private readonly Button _btnClearStats = new()
    {
        Text = "清空统计",
        Size = new Size(80, 26),
        FlatStyle = FlatStyle.Flat,
        BackColor = UiTheme.C_Btn,
        ForeColor = Color.White,
    };
    private readonly DataGridView _gridStats = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToResizeRows = false,
        BorderStyle = BorderStyle.None,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = UiTheme.C_TextBg,
        ForeColor = UiTheme.C_TextFg,
        GridColor = UiTheme.C_Card,
        RowHeadersVisible = false,
        RowTemplate = new DataGridViewRow { Height = 22 },
    };
    private readonly Dictionary<long, DataGridViewRow> _statsRowIdx = new();

    public StatsPanelController(SmartScheduler scheduler, StatusPanelController status,
        Func<bool> uiReady, Action<Action> invokeOnUi)
    {
        _scheduler = scheduler;
        _status = status;
        _uiReady = uiReady;
        _invokeOnUi = invokeOnUi;
        _statsParser.RoundUpdated += OnRoundUpdated;
        _statsParser.RoundRemoved += OnRoundRemoved;
        _statsParser.SessionReset += OnSessionReset;
    }

    /// <summary>构建统计面板（汇总行 + 表格 + 清空按钮）。</summary>
    public Control BuildPage()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(4),
            BackColor = UiTheme.C_Bg,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(_lblSummary, 0, 0);
        panel.Controls.Add(_btnClearStats, 1, 0);
        panel.Controls.Add(_gridStats, 0, 1);
        panel.SetColumnSpan(_gridStats, 2);

        UiTheme.ApplyStatsGridStyle(_gridStats);
        _gridStats.Columns.AddRange(
            UiTheme.MakeGridCol("时间"),
            UiTheme.MakeGridCol("输入tokens"),
            UiTheme.MakeGridCol("输入速度(t/s)"),
            UiTheme.MakeGridCol("输出tokens"),
            UiTheme.MakeGridCol("输出速度(t/s)"),
            UiTheme.MakeGridCol("命中率"),
            UiTheme.MakeGridCol("f_sim_best"),
            UiTheme.MakeGridCol("总耗时(s)"));

        _btnClearStats.Click += (_, _) => Reset();
        return panel;
    }

    /// <summary>会话重置（C-007：由调度器状态机 Waking 驱动；清空按钮同入口）。</summary>
    public void Reset() => _statsParser.Reset();

    /// <summary>日志行喂给解析器（由 MainForm 在调度器 Log 事件中调用）。</summary>
    public void FeedLine(string line) => _statsParser.Feed(line);

    /// <summary>一轮统计更新（进程输出线程）→ 表格行增量刷新 + 汇总；新行自动滚到底部。</summary>
    private void OnRoundUpdated(LlamaStatsParser.RoundStats s)
    {
        if (!_uiReady()) return;
        _invokeOnUi(() =>
        {
            var row = FindStatRow(s.Id);
            bool isNew = row == null;
            if (isNew)
            {
                int idx = _gridStats.Rows.Add();
                row = _gridStats.Rows[idx];
                row.Tag = s.Id;
                _statsRowIdx[s.Id] = row;
            }
            if (row != null)
                FillStatRow(row, s);
            UpdateSummary();
            if (isNew && row != null)
                _gridStats.CurrentCell = row.Cells[0];
        });
    }

    /// <summary>超出 50 轮上限、最旧轮次被淘汰（解析器线程）→ 删除对应表格行。</summary>
    private void OnRoundRemoved(LlamaStatsParser.RoundStats s)
    {
        if (!_uiReady()) return;
        _invokeOnUi(() =>
        {
            var row = FindStatRow(s.Id);
            if (row != null)
            {
                _gridStats.Rows.Remove(row);
                _statsRowIdx.Remove(s.Id);
            }
            UpdateSummary();
        });
    }

    /// <summary>会话重置（解析器线程）→ 清空表格。</summary>
    private void OnSessionReset()
    {
        if (!_uiReady()) return;
        _invokeOnUi(() =>
        {
            _gridStats.Rows.Clear();
            _statsRowIdx.Clear();
            _lblSummary.Text = "请求: 0";
        });
    }

    private DataGridViewRow? FindStatRow(long id)
        => _statsRowIdx.TryGetValue(id, out var r) ? r : null;

    private static void FillStatRow(DataGridViewRow row, LlamaStatsParser.RoundStats s)
    {
        row.Cells[0].Value = s.Time.ToString("HH:mm:ss");
        row.Cells[1].Value = s.PromptTokens.ToString();
        row.Cells[2].Value = s.PromptSpeed.ToString("F1");
        row.Cells[3].Value = s.EvalTokens.ToString();
        row.Cells[4].Value = s.EvalSpeed.ToString("F1");
        row.Cells[5].Value = s.HasDraft
            ? $"{s.DraftAccepted}/{s.DraftGenerated} ({(s.DraftGenerated > 0 ? s.DraftAccepted * 100.0 / s.DraftGenerated : 0):F1}%)"
            : "—";
        row.Cells[6].Value = s.FSimBest?.ToString("F3") ?? "—";
        row.Cells[7].Value = (s.TotalMs / 1000.0).ToString("F2");
    }

    /// <summary>累计汇总：请求数、总 tokens、平均速度、加权命中率。同步更新侧边 Token 统计 + Restore 卡片。</summary>
    private void UpdateSummary()
    {
        _status.UpdateRestoreCard();
        var rounds = _statsParser.GetRounds();
        if (rounds.Count == 0)
        {
            _lblSummary.Text = "请求: 0";
            _status.SetTokenSummary("请求: 0");
            return;
        }
        double inTok = rounds.Sum(r => r.PromptTokens);
        double outTok = rounds.Sum(r => r.EvalTokens);
        double inMs = rounds.Sum(r => r.PromptMs);
        double outMs = rounds.Sum(r => r.EvalMs);
        long acc = rounds.Where(r => r.HasDraft).Sum(r => r.DraftAccepted);
        long gen = rounds.Where(r => r.HasDraft).Sum(r => r.DraftGenerated);
        string summary = $"请求: {rounds.Count} | " +
            $"输入: {(long)inTok} @ {(inMs > 0 ? inTok / (inMs / 1000.0) : 0):F1} t/s | " +
            $"输出: {(long)outTok} @ {(outMs > 0 ? outTok / (outMs / 1000.0) : 0):F1} t/s | " +
            (gen > 0 ? $"命中: {acc}/{gen}" : "");
        _lblSummary.Text = summary;
        _status.SetTokenSummary(summary);
    }
}
