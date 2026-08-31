# 步骤4/5：MainForm.Ui.cs —— 删除已迁移区域字段 + 页签改用 Controller.BuildPage + 删除已迁移构建方法
$ErrorActionPreference = 'Stop'
$p = 'C:\project\lunch\LlamaHarness\MainForm.Ui.cs'
$c = [System.IO.File]::ReadAllText($p)
$c = $c.Replace("`r`n", "`n")  # 归一化 LF 便于正则
function ToNl([string]$s) { $s -replace "`r?`n", "`n" }
$fail = 0
function Rep([string]$name, [string]$old, [string]$new) {
    $n = $script:c.Replace((ToNl $old), (ToNl $new))
    if ($n -eq $script:c) { Write-Host "[FAIL] $name"; $script:fail++ } else { Write-Host "[OK] $name"; $script:c = $n }
}
function Reg([string]$name, [string]$pattern, [string]$new) {
    $nl = "`n"
    $new2 = $new.Replace('\n', $nl)  # replacement 中写 \n 表示真实换行
    $n = [regex]::Replace($script:c, $pattern, $new2)
    if ($n -eq $script:c) { Write-Host "[FAIL](reg) $name"; $script:fail++ } else { Write-Host "[OK](reg) $name"; $script:c = $n }
}

# ============ 字段区删除 ============
Rep '删 _lblThinking' @'
    // —— 思考模式状态标签（侧边统计面板）——
    private readonly Label _lblThinking = new()
    {
        Text = "思考: 极速",
        ForeColor = Color.Silver,
    };

'@ ''
Rep '删 监控字段块' @'
    // —— 系统资源统计（手动触发，无轮询）——
    private readonly SystemMetrics _metrics = new();
    private Button _btnRefreshRes = null!;
    private Label _lblResTimestamp = null!;
    private Panel _sysCard = null!;         // 系统资源卡片容器
    private Label _lblSysRes = null!;       // 系统资源卡片内容
    private Panel _slotsCard = null!;       // /slots 卡片容器
    private Label _lblSlotsTitle = null!;   // /slots 标题（含状态 ✓/✗）
    private Label _lblSlotsBody = null!;    // /slots 数据区
    private Button _btnRawSlots = null!;    // [查看原始报文 ▸]
    private TextBox _rawSlotsBox = null!;   // Raw 内容（TextBox 支持滚动）
    private Panel _propsCard = null!;       // /props 卡片容器
    private Label _lblPropsTitle = null!;   // /props 标题（含状态 ✓/✗）
    private TableLayoutPanel _tblPropsBody = null!; // /props 数据区（两列表格：左标签+右值）
    private Button _btnRawProps = null!;    // [查看原始报文 ▸]
    private TextBox _rawPropsBox = null!;   // Raw 内容（TextBox 支持滚动）
    private Panel _metricsCard = null!;     // /metrics 卡片容器
    private Label _lblMetricsTitle = null!; // /metrics 标题（含状态 ✓/✗）
    private Label _lblMetricsBody = null!;  // /metrics 数据区
    private Button _btnRawMetrics = null!;  // [查看原始报文 ▸]
    private TextBox _rawMetricsBox = null!; // Raw 内容（TextBox 支持滚动）
    private LlamaCppMonitorCollector? _monitorCollector; // llama.cpp 采集器（懒初始化，端口确定后创建）
    private int _metricsBusy;
    private bool _crashAlertShown; // 崩溃熔断红色告警状态（防重复告警；窗口滑出后自动恢复）

'@ ''
Rep '删 统计字段块' @'
    // —— 主日志区（LogView 承载：RichTextBox 按行独立着色 + 防抖）——
    // —— 统计区（实时解析 print_timing）——
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
        BorderStyle = BorderStyle.None, // 无边框，消除白边
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = UiTheme.C_TextBg,
        ForeColor = UiTheme.C_TextFg,
        GridColor = UiTheme.C_Card,
        RowHeadersVisible = false,
        RowTemplate = new DataGridViewRow { Height = 22 },
    };

'@ ''
Rep '删 槽位字段块' @'
    // —— 槽位绑定表格（页签3）——
    private DataGridView _gridSlots = null!;
    // —— 槽位管理页/表格（页签4，强占/KV缓存可编辑）——
    private Panel _tabSlotMgmt = null!;
    private DataGridView _gridSlotMgmt = null!;
'@ ''
Rep '删 右侧状态面板字段' @'
    // —— 右侧状态面板（原底部 SideStatsPanel 移入）——
    private Label _lblStatus = null!;       // 服务阶段卡片：调度器状态文本（运行中 · N个在途任务…），替代原侧边栏实例
    private Label _lblModuleState = null!;  // 模块状态（网关 运行中绿 / 已停止红）
    private Label _lblResSummary = null!;   // 系统资源单行摘要（CPU/内存/显存）
    private Label _lblRunTime = null!;      // 运行时长（自本次唤醒起）
    private DateTime? _wakeTime;    // 本次唤醒时刻（非 Running 为 null）


'@ ''
Rep '删 槽位索引/日志字段' @'
    // —— 槽位管理表 key→行索引（审计：原实现每轮刷新线性扫 Tag，O(n²)）——
    private readonly Dictionary<string, int> _slotMgmtRowIdx = new(StringComparer.Ordinal);
    // —— stats 表 id→行索引（E-10：对齐 _slotMgmtRowIdx 模式，替代 FindStatRow 线性扫 Tag）——
    private readonly Dictionary<long, DataGridViewRow> _statsRowIdx = new();
    // —— 槽位日志（槽位绑定页下方，独立持久化 slot.log）——
    private RichTextBox _txtSlotLog = null!;

'@ ''
Rep '删 侧边统计标签' @'
    // —— 侧边统计标签 ——
    private Label _lblTokenSummary = null!;
    private Label _lblSlotSummary = null!;
    private Label _lblRestoreHit = null!;   // Restore 命中率卡片（3.1 可观测）
'@ ''

# ============ BuildUi / BuildTabArea 改造 ============
Rep 'BuildUi statusPanel' '        var statusPanel = BuildStatusPanel();' '        var statusPanel = _status.BuildPage();'
Rep 'tabStats 用 Stats.BuildPage' '        tabStats.Controls.Add(BuildStatsPanel());' '        tabStats.Controls.Add(_stats.BuildPage());'

# 槽位绑定页块 → SlotPanelController.BuildBindingsPage
Reg '槽位绑定页块' '(?s)        // 槽位绑定页：上方绑定表格 \+ 下方槽位日志（独立持久化 slot\.log）\n.*?        tabSlots\.Controls\.Add\(_gridSlots\);\n\n' '        // 槽位绑定页：由 SlotPanelController 构建（上方绑定表格 + 下方槽位日志）\n        tabSlots.Controls.Add(_slot.BuildBindingsPage());\n\n'

# 槽位管理页块 → SlotPanelController.BuildMgmtPage
Reg '槽位管理页块' '(?s)        // 槽位管理页：DataGridView（强占/KV缓存 CheckBox 可编辑）\n.*?        _tabSlotMgmt\.Controls\.Add\(_gridSlotMgmt\);\n\n' '        // 槽位管理页：由 SlotPanelController 构建（强占/KV缓存 CheckBox 可编辑）\n        var tabSlotMgmt = _slot.BuildMgmtPage();\n\n'

# 系统资源页块 → MonitorPanelController.BuildPage
Reg '系统资源页块' '(?s)        // ════════════ 系统资源页：可滚动 Panel \+ TableLayoutPanel 纵向布局 ════════════\n.*?        _btnRawMetrics\.Click \+= \(s, e\) => ToggleRaw\(_btnRawMetrics, _rawMetricsBox\);\n\n' '        // 系统资源页：由 MonitorPanelController 构建（本地采集 + llama.cpp 三卡片）\n        var tabRes = _monitor.BuildPage();\n\n'

# _tabPages 引用：_tabSlotMgmt → 局部 tabSlotMgmt
Rep '_tabPages 引用' '_tabPages = new Control[] { _logView.TxtLog, tabStats, tabSlots, _tabSlotMgmt, tabRes, _tabConfig, _docPanel };' '_tabPages = new Control[] { _logView.TxtLog, tabStats, tabSlots, tabSlotMgmt, tabRes, _tabConfig, _docPanel };'

# ============ 删除已迁移的构建方法 ============
# BuildStatusPanel（右侧状态面板）→ StatusPanelController.BuildPage
Reg '删 BuildStatusPanel' '(?s)    /// <summary>右侧状态面板.*?    /// <summary>构建 Configuration 面板' '    /// <summary>构建 Configuration 面板'

# BuildStatsPanel（统计面板，文件末尾）→ StatsPanelController.BuildPage
Reg '删 BuildStatsPanel' '(?s)    /// <summary>构建统计面板.*?\n}\s*\z' '}'

# 清理多余空行（连续 3+ 空行 → 2）
$c = [regex]::Replace($c, "\n{4,}", "\n\n\n")
$c = $c.Replace("`n", "`r`n")  # 恢复 CRLF

if ($fail -gt 0) { Write-Host '存在未匹配项，中止写回'; exit 1 }
[System.IO.File]::WriteAllText($p, $c, [System.Text.UTF8Encoding]::new($false))
Write-Host 'MainForm.Ui.cs 改造完成'
