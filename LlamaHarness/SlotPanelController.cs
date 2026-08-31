namespace LlamaHarness;

/// <summary>
/// 槽位区 Controller：槽位绑定表格（页签3）+ 槽位管理表格（页签4，强占/KV缓存可编辑）+ 槽位日志。
/// 数据来自 SmartScheduler.GetSlotBindings/SetSlotPreemptive/SetSlotKvCache；SlotBindingChanged/SlotLog
/// 事件来自后台线程，经 uiReady/invokeOnUi 切回 UI 线程。
/// </summary>
public sealed class SlotPanelController
{
    private readonly SmartScheduler _scheduler;
    private readonly Action<string> _appendLog;      // 主日志（槽位管理操作审计）
    private readonly Action<string> _setSlotSummary; // 右侧面板槽位绑定摘要
    private readonly Func<bool> _uiReady;
    private readonly Action<Action> _invokeOnUi;

    private DataGridView _gridSlots = null!;
    private Panel _tabSlotMgmt = null!;
    private DataGridView _gridSlotMgmt = null!;
    private RichTextBox _txtSlotLog = null!;
    private readonly Dictionary<string, int> _slotMgmtRowIdx = new(StringComparer.Ordinal);

    public SlotPanelController(SmartScheduler scheduler, Action<string> appendLog,
        Action<string> setSlotSummary, Func<bool> uiReady, Action<Action> invokeOnUi)
    {
        _scheduler = scheduler;
        _appendLog = appendLog;
        _setSlotSummary = setSlotSummary;
        _uiReady = uiReady;
        _invokeOnUi = invokeOnUi;
    }

    /// <summary>槽位绑定页（页签3）：上方绑定表格 + 下方槽位日志（独立持久化 slot.log）。</summary>
    public Control BuildBindingsPage()
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.C_Bg, Padding = new Padding(10) };
        _txtSlotLog = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = UiTheme.C_TextBg,
            ForeColor = UiTheme.C_TextFg,
            Font = new Font("Consolas", 9F),
            ScrollBars = RichTextBoxScrollBars.Vertical,
            WordWrap = false,
        };
        _gridSlots = UiTheme.MakeGrid();
        _gridSlots.Dock = DockStyle.Top;
        _gridSlots.Height = 260;
        UiTheme.ApplyStatsGridStyle(_gridSlots);
        _gridSlots.Columns.AddRange(
            UiTheme.MakeGridCol("亲和 Key"), UiTheme.MakeGridCol("应用"),
            UiTheme.MakeGridCol("槽位"), UiTheme.MakeGridCol("最后活跃"));
        page.Controls.Add(_txtSlotLog);
        page.Controls.Add(_gridSlots);
        return page;
    }

    /// <summary>槽位管理页（页签4）：DataGridView（强占/KV缓存 CheckBox 可编辑）。</summary>
    public Control BuildMgmtPage()
    {
        _tabSlotMgmt = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.C_Bg, Padding = new Padding(10) };
        _gridSlotMgmt = UiTheme.MakeGrid();
        _gridSlotMgmt.ReadOnly = false;
        UiTheme.ApplyStatsGridStyle(_gridSlotMgmt);
        _gridSlotMgmt.Columns.AddRange(
            UiTheme.MakeGridCol("亲和 Key"), UiTheme.MakeGridCol("应用"), UiTheme.MakeGridCol("槽位"),
            UiTheme.MakeCheckCol("强占"), UiTheme.MakeCheckCol("KV缓存"), UiTheme.MakeGridCol("最后活跃"));
        _gridSlotMgmt.CellValueChanged += OnSlotMgmtCellChanged;
        _tabSlotMgmt.Controls.Add(_gridSlotMgmt);
        return _tabSlotMgmt;
    }

    /// <summary>槽位绑定变更（非 UI 线程）→ 刷新槽位表格 + 管理表格。</summary>
    public void RefreshBindings()
    {
        if (!_uiReady()) return;
        _invokeOnUi(() =>
        {
            RefreshSlotGrid();
            RefreshSlotMgmtGrid();
        });
    }

    /// <summary>槽位日志事件（非 UI 线程）→ 显示到槽位页 RichTextBox + slot.log 持久化。</summary>
    public void OnSlotLog(string line)
    {
        LogFile.SlotAppend(line);
        if (!_uiReady()) return;
        _invokeOnUi(() => AppendSlotLog(line));
    }

    private void RefreshSlotGrid()
    {
        var bindings = _scheduler.GetSlotBindings();
        if (bindings == null || bindings.Count == 0)
        {
            _gridSlots.Rows.Clear();
            _setSlotSummary("槽位: 0 绑定");
            return;
        }
        _gridSlots.Rows.Clear();
        foreach (var (key, app, slot, lastActive, _, _) in bindings)
            _gridSlots.Rows.Add(key, app, $"slot {slot}", lastActive.ToString("HH:mm:ss"));
        _setSlotSummary($"槽位: {bindings.Count} 绑定");
    }

    /// <summary>填充槽位管理表格（强占/KV缓存 CheckBox 可编辑）；行 Key = 亲和 Key，Dictionary 索引避免 O(n²) 扫 Tag。</summary>
    private void RefreshSlotMgmtGrid()
    {
        var bindings = _scheduler.GetSlotBindings();
        if (bindings == null)
        {
            _gridSlotMgmt.Rows.Clear();
            _slotMgmtRowIdx.Clear();
            return;
        }
        foreach (var (key, app, slot, lastActive, preemptive, kvCache) in bindings)
        {
            int idx;
            if (!_slotMgmtRowIdx.TryGetValue(key, out idx))
            {
                idx = _gridSlotMgmt.Rows.Add();
                _gridSlotMgmt.Rows[idx].Tag = key;
                _slotMgmtRowIdx[key] = idx;
            }
            var row = _gridSlotMgmt.Rows[idx];
            row.Cells[0].Value = key;
            row.Cells[1].Value = app;
            row.Cells[2].Value = $"slot {slot}";
            row.Cells[3].Value = preemptive;
            row.Cells[4].Value = kvCache;
            row.Cells[5].Value = lastActive.ToString("HH:mm:ss");
        }
    }

    /// <summary>追加一行槽位日志（带时间戳 + 级别着色），自动滚到底部。字符上限防膨胀。</summary>
    private void AppendSlotLog(string line)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}";
        _txtSlotLog.AppendText(entry);
        if (_txtSlotLog.TextLength > 100_000)
        {
            _txtSlotLog.SelectionStart = 0;
            _txtSlotLog.SelectionLength = 50_000;
            _txtSlotLog.SelectedText = "";
        }
        int start = Math.Max(0, _txtSlotLog.TextLength - entry.Length);
        _txtSlotLog.SelectionStart = start;
        _txtSlotLog.SelectionLength = entry.Length;
        _txtSlotLog.SelectionColor = LogFile.Classify(line) switch
        {
            LogFile.Level.Warn => Color.Gold,
            LogFile.Level.Error => Color.Red,
            _ => Color.LightGreen,
        };
        _txtSlotLog.SelectionStart = _txtSlotLog.TextLength;
        _txtSlotLog.SelectionLength = 0;
        _txtSlotLog.ScrollToCaret();
    }

    /// <summary>槽位管理表格 CheckBox 变更 → 回写调度器（SetPreemptive/SetKvCache）。</summary>
    private void OnSlotMgmtCellChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.ColumnIndex < 3 || e.RowIndex >= _gridSlotMgmt.Rows.Count) return;
        var row = _gridSlotMgmt.Rows[e.RowIndex];
        if (row.Tag is not string key) return;
        switch (e.ColumnIndex)
        {
            case 3: // 强占
                bool preemptive = row.Cells[3].Value is true;
                row.Cells[3].Value = preemptive;
                _scheduler.SetSlotPreemptive(key, preemptive);
                _appendLog($"槽位管理：{key} 强占模式 → {(preemptive ? "开启" : "关闭")}");
                break;
            case 4: // KV缓存
                bool kvCache = row.Cells[4].Value is true;
                row.Cells[4].Value = kvCache;
                _scheduler.SetSlotKvCache(key, kvCache);
                _appendLog($"槽位管理：{key} KV Cache → {(kvCache ? "开启" : "关闭")}");
                break;
        }
    }
}
