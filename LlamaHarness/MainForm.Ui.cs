namespace LlamaHarness;

/// <summary>
/// MainForm 的 UI 构建部分（partial）：全部控件字段 + 界面构建方法。
/// 业务逻辑 / 事件处理见 MainForm.cs（后续拆分出 MainFormPresenter 与各区域 Controller）。
/// </summary>
public partial class MainForm : Form
{

    // —— 参数控件（Configuration 面板内）——
    private readonly TextBox _txtExe = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White, BorderStyle = BorderStyle.None };
    private readonly Button _btnBrowseExe = new() { Text = "…", Size = new Size(32, 28), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(55, 55, 55), ForeColor = Color.White };
    private readonly TextBox _txtModel = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White, BorderStyle = BorderStyle.None };
    private readonly Button _btnBrowseModel = new() { Text = "…", Size = new Size(32, 28), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(55, 55, 55), ForeColor = Color.White };
    private readonly NumericUpDown _numPort = new() { Minimum = 1, Maximum = 65534, Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White };
    private readonly NumericUpDown _numCtx = new() { Minimum = 256, Maximum = 1_048_576, Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White };
    private readonly NumericUpDown _numNgl = new() { Minimum = 0, Maximum = 999, Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White };
    private readonly NumericUpDown _numParallel = new() { Minimum = 1, Maximum = 128, Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White };
    private readonly CheckBox _chkNoKv = new() { Text = "--no-kv-unified", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(200, 200, 200) };
    private readonly NumericUpDown _numThreads = new() { Minimum = 1, Maximum = 512, Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White };
    private readonly TextBox _txtExtra = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White, BorderStyle = BorderStyle.None };
    private readonly CheckBox _chkAuto = new() { Text = "智能按需模式（推荐）", AutoSize = true, ForeColor = Color.FromArgb(200, 200, 200) }; // AutoSize：紧跟"模式:"标签同行
    private readonly NumericUpDown _numIdleMin = new() { Minimum = 1, Maximum = 120, Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White };
    private readonly TextBox _txtPcoreMask = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White, BorderStyle = BorderStyle.None };
    private readonly CheckBox _chkForceStream = new() { Text = "强制流式", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(200, 200, 200) };
    private readonly TextBox _txtKvCachePath = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White, BorderStyle = BorderStyle.None };
    private readonly CheckBox _chkTokenGuard = new() { Text = "Token Guard（防上下文超长）", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(200, 200, 200) };
    private readonly NumericUpDown _numReservedTokens = new() { Minimum = 512, Maximum = 131_072, Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White };
    private readonly NumericUpDown _numPromptOverhead = new() { Minimum = 0, Maximum = 65_536, Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White };
    private readonly NumericUpDown _numCacheRam = new() { Minimum = 0, Maximum = 16_384, Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White };
    private readonly CheckBox _chkNoCacheIdleSlots = new() { Text = "禁空闲slot入缓存", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(200, 200, 200) };
    private readonly CheckBox _chkContinuation = new() { Text = "输出续接（截断自动续写）", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(200, 200, 200) };
    private readonly NumericUpDown _numMaxContinuations = new() { Minimum = 1, Maximum = 50, Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White };
    private readonly NumericUpDown _numContTimeout = new() { Minimum = 30, Maximum = 3600, Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White };
    private readonly CheckBox _chkCrashRecover = new() { Text = "bad_alloc 自动恢复（快照接续/重放）", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(200, 200, 200) };
    private readonly NumericUpDown _numMaxRestarts = new() { Minimum = 0, Maximum = 10, Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White };
    // Prefill 吞吐参数（阶段二调优：ubatch/batch/KV 量化/flash-attn/投机解码/batch 线程）
    private readonly TextBox _txtLoadMode = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White, BorderStyle = BorderStyle.None };
    private readonly NumericUpDown _numUbatch = new() { Minimum = 1, Maximum = 65536, Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White };
    private readonly NumericUpDown _numBatch = new() { Minimum = 1, Maximum = 65536, Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White };
    private readonly TextBox _txtCacheTypeKv = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White, BorderStyle = BorderStyle.None };
    private readonly CheckBox _chkFlashAttn = new() { Text = "--flash-attn", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(200, 200, 200) };
    private readonly TextBox _txtSpecType = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White, BorderStyle = BorderStyle.None };
    private readonly NumericUpDown _numSpecDraftNMax = new() { Minimum = 0, Maximum = 8, Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White };
    private readonly CheckBox _chkRequestDump = new() { Text = "request-dump（dump 所有请求到 logs/request_dump.log）", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(200, 200, 200) };
    private readonly ComboBox _cmbLogQueuePolicy = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, ForeColor = Color.White };
    private readonly NumericUpDown _numBatchThreads = new() { Minimum = 0, Maximum = 512, Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White };
    // §4.2 自动强占（冻结防驱逐）：按应用类型前缀，勾选 → 该类型绑定强制 Preemptive=true
    private readonly CheckBox _chkAutoPreDshRule = new() { Text = "DSH规则", AutoSize = true, ForeColor = Color.FromArgb(200, 200, 200) };
    private readonly CheckBox _chkAutoPreWebui = new() { Text = "WebUI", AutoSize = true, ForeColor = Color.FromArgb(200, 200, 200) };
    private readonly CheckBox _chkAutoPreTrae = new() { Text = "Trae", AutoSize = true, ForeColor = Color.FromArgb(200, 200, 200) };
    private readonly CheckBox _chkAutoPreDshAgent = new() { Text = "DSH Agent", AutoSize = true, ForeColor = Color.FromArgb(200, 200, 200) };
    // 自动快照恢复（仅快照持久化，不锁槽）：前缀匹配 → 首请求存档 + Warming eager restore；不参与强占/驱逐拒绝
    private readonly CheckBox _chkSnapDshRule = new() { Text = "DSH规则", AutoSize = true, ForeColor = Color.FromArgb(200, 200, 200) };
    private readonly CheckBox _chkSnapWebui = new() { Text = "WebUI", AutoSize = true, ForeColor = Color.FromArgb(200, 200, 200) };
    private readonly CheckBox _chkSnapTrae = new() { Text = "Trae", AutoSize = true, ForeColor = Color.FromArgb(200, 200, 200) };
    private readonly CheckBox _chkSnapDshAgent = new() { Text = "DSH Agent", AutoSize = true, ForeColor = Color.FromArgb(200, 200, 200) };
    private readonly ToolTip _tooltip = new();

    // —— 操作按钮（Control Panel 区）——
    private Button _btnStart = null!;
    private Button _btnStop = null!;
    private Button _btnClearLog = null!;
    private Button _btnClearCache = null!;
    private Button _btnThinkOn = null!;   // 开启思考模式 → XHigh（深度推理）
    private Button _btnTurbo = null!;     // 开启极速模式 → Off（不注入思考参数）
    private Button _btnExportCfg = null!;
    private Button _btnImportCfg = null!;

    // —— 配置管理页（页签6）——
    private Panel _tabConfig = null!;
    private Panel _docPanel = null!; // 文档展示面板（右侧，点击使用说明/常见问题/更新内容后显示）

    // —— 自定义页签区（替代原生 TabControl：扁平按钮页签条 + Panel 显隐切换，对齐参考界面）——
    private SplitContainer _contentSplit = null!;   // 左 80% 页签区 | 右 20% 状态面板（SizeChanged 时按 8:2 重算）
    private Button[] _tabButtons = null!;
    private Control[] _tabPages = null!; // 页签页（_txtLog 是 RichTextBox，其余为 Panel，统一用 Control）
    private int _currentTab = 0;


    // —— 参数控件清单（BuildUi 一次构建；ApplyPhase 按相位批量启停，审计：原实现每次调用重建数组）——
    private Control[] _paramControls = null!;


    // ==================== UI 构建 ====================

    private void BuildUi()
    {
        Text = "Llama Harness";
        ClientSize = new Size(1280, 800);
        // 最小高度 720：侧边栏 17 行共需 668px 客户区（按键合计 536px + 17×8px 行间距 + 4px 顶部留白），
        // 客户区 = 窗体高 − 约 39px 标题栏/边框 → 720 留 13px 余量，避免「常见问题/更新内容」在最小窗口被裁切。
        MinimumSize = new Size(1000, 720);
        StartPosition = FormStartPosition.CenterScreen;

        BackColor = UiTheme.C_Bg;
        ForeColor = UiTheme.C_TextFg;

        var tabArea = BuildTabArea();
        var leftPanel = BuildLeftPanel();
        var titleBlock = BuildTitleBlock();
        var statusPanel = _status.BuildPage();

        // 参数控件清单一次构建（审计：原实现每次 ApplyPhase 调用都重建数组）
        _paramControls = new Control[]
        {
            _txtExe, _btnBrowseExe, _txtModel, _btnBrowseModel,
            _numPort, _numCtx, _numNgl, _numParallel, _chkNoKv, _numThreads, _txtExtra,
            _chkAuto, _numIdleMin, _txtPcoreMask, _chkForceStream, _txtKvCachePath,
            _chkTokenGuard, _numReservedTokens,
            _chkContinuation, _numMaxContinuations, _numContTimeout,
            _chkCrashRecover, _numMaxRestarts,
            _chkAutoPreDshRule, _chkAutoPreWebui, _chkAutoPreTrae, _chkAutoPreDshAgent,
            _chkSnapDshRule, _chkSnapWebui, _chkSnapTrae, _chkSnapDshAgent,
            _btnExportCfg, _btnImportCfg, // 运行中禁止导入/导出，避免改参冲突
        };

        // ════════════ 右侧主区：顶部橙色标题块 + 下方 7:3 分栏（左页签 | 右状态面板）════════════
        var rightContent = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.C_Bg };
        _contentSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 4,
            BackColor = UiTheme.C_Bg,
        };
        _contentSplit.Panel1.Controls.Add(tabArea);
        _contentSplit.Panel2.Controls.Add(statusPanel);
        rightContent.Controls.Add(_contentSplit);
        rightContent.Controls.Add(titleBlock); // Dock Top，后添加 → 位于最上

        // ════════════ 主布局：左侧边栏(200px) | 右侧主区 ════════════
        var mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 4,
            BackColor = UiTheme.C_Bg,
        };
        mainSplit.Panel1.Controls.Add(leftPanel);
        mainSplit.Panel2.Controls.Add(rightContent);
        Controls.Add(mainSplit);

        Shown += (_, _) =>
        {
            mainSplit.SplitterDistance = 240; // 侧边栏 240px（容纳按钮文字，避免滚动条）
            ApplyContentSplitRatio(); // 初始按 8:2 分栏
        };

        // 窗口缩放/最大化时：侧边栏固定 240px + 内容区按 8:2 重算，保证任何尺寸下布局稳定
        mainSplit.SizeChanged += (_, _) =>
        {
            if (mainSplit.Width > 0)
                mainSplit.SplitterDistance = 240; // 侧边栏始终 240px（根因：最大化后 SplitContainer 不自动保持分割位置）
            ApplyContentSplitRatio();
        };
    }

    /// <summary>按 8:2 比例设置内容分栏：左页签区 80%、右状态面板 20%。</summary>
    private void ApplyContentSplitRatio()
    {
        if (_contentSplit == null || _contentSplit.Width <= 0) return;
        int avail = _contentSplit.Width - _contentSplit.SplitterWidth;
        _contentSplit.SplitterDistance = Math.Max(300, (int)(avail * 0.8));
    }

    /// <summary>左侧边栏 (200px)：应用名 + Control Panel + Configuration + User Manual。
    /// 按钮带参考界面 PNG 图标（static/icon），悬停变亮，对齐 Auto_Pilot 侧边栏样式。</summary>
    private Panel BuildLeftPanel()
    {
        // 侧边栏：#2d2d2d 深灰底（对齐参考 sidebar_bg），宽度 240（容纳按钮文字，避免滚动条）。
        // 层次感靠灰度差异：侧栏 #2d2d2d < 按钮 #3d3d3d < 悬停 #4a4a4a，非纯黑。
        var leftPanel = new Panel
        {
            Dock = DockStyle.Left,
            Width = 240,
            BackColor = UiTheme.C_Card, // #2d2d2d
            Padding = new Padding(0), // 边距由下方 Percent 列控制（等比缩放）
            AutoScroll = false, // 禁用滚动条（内容高度足够容纳全部按钮）
        };

        // ── 应用名区（替代原顶部标题栏；扁平 Button 样式化，支持图标+文本并排）──
        var lblAppName = new Button
        {
            Text = "Llama Harness",
            Image = UiTheme.LoadIcon("控制面板.png"),
            TextImageRelation = TextImageRelation.ImageBeforeText,
            ImageAlign = ContentAlignment.MiddleLeft,
            TextAlign = ContentAlignment.MiddleCenter,
            Height = 44,
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
            BackColor = UiTheme.C_BtnHover, // #4a4a4a（默认色，原悬停色）
            FlatStyle = FlatStyle.Flat,
            TabStop = false,
            Cursor = Cursors.Default,
        };
        lblAppName.FlatAppearance.BorderSize = 0; // 无边框，消除白边
        lblAppName.MouseEnter += (_, _) => lblAppName.BackColor = UiTheme.C_Card; // 悬停 → #2d2d2d（原默认色）
        lblAppName.MouseLeave += (_, _) => lblAppName.BackColor = UiTheme.C_BtnHover; // 还原 → #4a4a4a

        // ── Control Panel ──
        var lblCtrlTitle = UiTheme.MakeSectionTitle("Control Panel");
        _btnStart = UiTheme.MakeBtn("启动 / 唤醒", "设备启动.png");
        _btnStop = UiTheme.MakeBtn("停止", "设备停止.png", enabled: false);
        _btnClearLog = UiTheme.MakeBtn("清空日志", "清除日志.png", h: 30);
        _btnClearCache = UiTheme.MakeBtn("清空缓存", "其他设置.png", h: 30);
        _btnThinkOn = UiTheme.MakeBtn("开启思考模式", "附加选项.png", h: 30);
        _btnTurbo = UiTheme.MakeBtn("开启极速模式", "速度设置.png", h: 30);
        // _lblStatus 已移至右侧"服务阶段"卡片（替换原 _lblPhase），此处不再创建侧边栏实例

        // ── Configuration ──
        var lblCfgTitle = UiTheme.MakeSectionTitle("Configuration");
        var btnSlotMgmt = UiTheme.MakeBtn("槽位管理", "扩展设置.png", h: 30);
        var btnOpenConfig = UiTheme.MakeBtn("配置管理", "配置管理.png");
        _btnExportCfg = UiTheme.MakeBtn("保存配置到…", "数据上传.png", h: 30);
        _btnImportCfg = UiTheme.MakeBtn("载入配置", "路径设置.png", h: 30);
        btnSlotMgmt.Click += (_, _) => SelectTab(3); // 槽位管理页
        btnOpenConfig.Click += (_, _) => SelectTab(5); // 配置管理页

        // ── User Manual（接线：显示 static/doc 对应文档）──
        var lblManualTitle = UiTheme.MakeSectionTitle("User Manual");
        var btnHelp = UiTheme.MakeBtn("使用说明", "使用说明.png", h: 30);
        var btnFaq = UiTheme.MakeBtn("常见问题", "常见问题.png", h: 30);
        var btnChangelog = UiTheme.MakeBtn("更新内容", "更新内容.png", h: 30);
        btnHelp.Click += (_, _) => { SelectTab(6); ShowDocInPanel(_docPanel, "使用说明", "static/doc/readme.md"); };
        btnFaq.Click += (_, _) => { SelectTab(6); ShowDocInPanel(_docPanel, "常见问题", "static/doc/FAQs.md"); };
        btnChangelog.Click += (_, _) => { SelectTab(6); ShowDocInPanel(_docPanel, "更新内容", "static/doc/update.md"); };

        // ── 布局网格：[12.5% | 75% | 12.5%] 三列——所有按键/容器放中列，
        // 等宽（含应用名）+ 居中 + 左右间隔等宽；Percent 列保证侧边栏放大时边距等比缩放 ──
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12.5f)); // 左边距（等比）
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 75f));   // 内容列（所有按键等宽）
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12.5f)); // 右边距（等比）

        // 行高 = 按钮高度 + 上下留白（各 4px），按钮 Dock=Top + Margin 垂直居中，不顶满
        void AddRow(Control c, int h)
        {
            int row = grid.RowStyles.Count;
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, h + 8)); // 行高 = 内容高 + 8px 留白
            c.Dock = DockStyle.Top; // 宽度撑满中列，高度固定为控件自身 Height
            c.Margin = new Padding(0, 4, 0, 4); // 上下各 4px → 垂直居中
            grid.Controls.Add(c, 1, row);
        }

        AddRow(lblAppName, 44); // 应用名与下方按键等宽
        AddRow(lblCtrlTitle, 30);
        AddRow(_btnStart, 34);
        AddRow(_btnStop, 34);
        AddRow(_btnClearLog, 30);
        AddRow(_btnClearCache, 30);
        AddRow(_btnThinkOn, 30);
        AddRow(_btnTurbo, 30);
        AddRow(lblCfgTitle, 30);
        AddRow(btnSlotMgmt, 30);
        AddRow(btnOpenConfig, 34);
        AddRow(_btnExportCfg, 30);
        AddRow(_btnImportCfg, 30);
        AddRow(lblManualTitle, 30);
        AddRow(btnHelp, 30);
        AddRow(btnFaq, 30);
        AddRow(btnChangelog, 30);

        leftPanel.Controls.Add(grid);
        return leftPanel;
    }

    /// <summary>在指定 Panel 中渲染 Markdown 文档（内嵌显示，不新开窗口）。</summary>
    private void ShowDocInPanel(Panel container, string title, string relPath)
    {
        // 清除容器现有内容
        container.Controls.Clear();
        container.Visible = true;

        var rtb = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = UiTheme.C_TextBg,
            ForeColor = UiTheme.C_TextFg,
            BorderStyle = BorderStyle.None,
            Font = new Font("Microsoft YaHei UI", 9F),
        };

        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relPath.Replace('/', Path.DirectorySeparatorChar)));
        string mdText;
        try
        {
            mdText = File.Exists(path) ? File.ReadAllText(path) : "（文档文件缺失：" + relPath + "）";
        }
        catch
        {
            mdText = "（文档加载失败）";
        }

        MarkdownRenderer.RenderToRichTextBox(rtb, mdText);
        container.Controls.Add(rtb);
    }

    /// <summary>顶部橙色大标题块 (~90px，对齐参考界面)：左多行橙黄主标题 + 右操作提示。</summary>
    private static Panel BuildTitleBlock()
    {
        var titleBlock = new Panel
        {
            Dock = DockStyle.Top,
            Height = 150,
            BackColor = UiTheme.C_Bg,
            Padding = new Padding(16, 10, 16, 6),
        };
        var lblTitle = new Label
        {
            Text = "Llama.cpp Harness--长Agent本地私有化资源治理框架\n资源治理抬高效率下限，硬件决定性能上限!\n专为低并发、高可靠、复杂Agent任务深度优化",
            Font = new Font("Microsoft YaHei UI", 24F, FontStyle.Bold),
            ForeColor = UiTheme.C_Primary,
            AutoSize = true,
            Dock = DockStyle.Left,
            Margin = new Padding(10, 4, 16, 10),
        };
        var lblHint = new Label
        {
            Text = "槽位亲和 ·自动路由 · KV 快照自愈\n动态锁定 · 实时监控 · 告警机制",
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            ForeColor = UiTheme.C_Primary,
            TextAlign = ContentAlignment.MiddleRight,
            Dock = DockStyle.Fill,
            Margin = new Padding(10, 8, 8, 10),
        };
        titleBlock.Controls.Add(lblHint); // 先添加（Fill 后布局，占 Left 之外的剩余空间）
        titleBlock.Controls.Add(lblTitle);
        return titleBlock;
    }

    /// <summary>自定义页签区（左 70%）：扁平按钮页签条 + 6 内容页 Panel 显隐切换（替代原生 TabControl，对齐参考界面）。
    /// 选中 = #FFA500 底黑字；未选 = #3d3d3d 底白字。</summary>
    private Panel BuildTabArea()
    {
        // 精简嵌套：_txtLog 直接作为页签页（去掉原 pad/tabLog 冗余 Panel 层），
        // 布局链 = _contentSplit.Panel1 → container → host → _txtLog，无任何 Padding
        var container = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.C_Bg };

        var tabStats = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.C_Bg, Padding = new Padding(10) };
        tabStats.Controls.Add(_stats.BuildPage());

        // 槽位绑定页：由 SlotPanelController 构建（上方绑定表格 + 下方槽位日志）
        var tabSlots = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.C_Bg, Padding = new Padding(10) };
        tabSlots.Controls.Add(_slot.BuildBindingsPage());

        // 槽位管理页：由 SlotPanelController 构建（强占/KV缓存 CheckBox 可编辑）
        var tabSlotMgmt = _slot.BuildMgmtPage();

        // 系统资源页：由 MonitorPanelController 构建（本地采集 + llama.cpp 三卡片）
        var tabRes = _monitor.BuildPage();

        // 配置管理页（纯配置面板）
        _tabConfig = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.C_Bg, Padding = new Padding(10), AutoScroll = true };
        _tabConfig.Controls.Add(BuildConfigPanel());

        // 信息展示页（独立页签，与配置管理平级；点击使用说明/常见问题/更新内容后显示 MD）
        _docPanel = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.C_TextBg, Padding = new Padding(8) };

        // 页签条：7 个扁平按钮（选中橙底黑字 / 未选 #3d3d3d 白字，悬停变亮）
        string[] names = { "日志", "统计", "槽位绑定", "槽位管理", "系统资源", "配置管理", "信息展示" };
        _tabButtons = new Button[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            int idx = i;
            var b = UiTheme.MakeTabBtn(names[i]);
            b.Click += (_, _) => SelectTab(idx);
            b.MouseEnter += (_, _) => { if (_currentTab != idx) b.BackColor = UiTheme.C_BtnHover; };
            b.MouseLeave += (_, _) => { if (_currentTab != idx) b.BackColor = UiTheme.C_Btn; };
            _tabButtons[i] = b;
        }
        var tabStrip = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true, // 窗口过窄时页签换行，高度自适应
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = UiTheme.C_Bg,
            Margin = new Padding(0),
            Padding = new Padding(4, 2, 4, 2),
        };
        foreach (var b in _tabButtons) tabStrip.Controls.Add(b);

        // 内容宿主：6 页叠放 + Visible 切换（_txtLog 直接作为一页，无中间包装 Panel）
        var host = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.C_Bg };
        _tabPages = new Control[] { _logView.TxtLog, tabStats, tabSlots, tabSlotMgmt, tabRes, _tabConfig, _docPanel };
        foreach (var p in _tabPages) host.Controls.Add(p);

        container.Controls.Add(host);
        container.Controls.Add(tabStrip); // Dock Top，后添加 → 位于最上
        SelectTab(0);
        return container;
    }


    /// <summary>切换页签：内容页显隐 + 选中样式刷新（选中 = #FFA500 底黑字）。</summary>
    private void SelectTab(int index)
    {
        if (index < 0 || index >= _tabPages.Length) return;
        _currentTab = index;
        for (int i = 0; i < _tabPages.Length; i++)
        {
            bool sel = i == index;
            _tabPages[i].Visible = sel;
            _tabButtons[i].BackColor = sel ? UiTheme.C_Primary : UiTheme.C_Btn;
            _tabButtons[i].ForeColor = sel ? UiTheme.C_TextBg : Color.White;
        }
    }

    /// <summary>构建 Configuration 面板（14 项配置 + 浏览按钮）。字体白色，暗色背景。</summary>
    private Control BuildConfigPanel()
    {
        // 浏览按钮边框清零（对象初始化器内无法引用实例成员，在此统一设置）
        _btnBrowseExe.FlatAppearance.BorderSize = 0;
        _btnBrowseModel.FlatAppearance.BorderSize = 0;

        // 文字框白字（禁用时也保持白字，清晰）+ CheckBox 勾改黑
        foreach (var c in new[] { _txtExe, _txtModel, _txtExtra, _txtPcoreMask, _txtKvCachePath, _txtLoadMode, _txtCacheTypeKv, _txtSpecType })
            if (c is TextBox tb) tb.ForeColor = Color.White;
        foreach (var c in new[] { _chkNoKv, _chkAuto, _chkForceStream, _chkTokenGuard, _chkContinuation, _chkCrashRecover, _chkFlashAttn, _chkRequestDump, _chkAutoPreDshRule, _chkAutoPreWebui, _chkAutoPreTrae, _chkAutoPreDshAgent, _chkSnapDshRule, _chkSnapWebui, _chkSnapTrae, _chkSnapDshAgent, _chkNoCacheIdleSlots })
            UiTheme.ApplyBlackCheck(c);

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 6),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        void AddRow(string label, Control value, Control? extra)
        {
            int row = panel.RowStyles.Count;
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var lbl = new Label
            {
                Text = label,
                AutoSize = true,
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 4, 6, 4),
            };
            panel.Controls.Add(lbl, 0, row);
            value.Margin = new Padding(0, 2, 0, 2);
            panel.Controls.Add(value, 1, row);
            if (extra != null)
            {
                extra.Margin = new Padding(2, 0, 0, 0);
                panel.Controls.Add(extra, 2, row);
            }
        }

        AddRow("exe:", _txtExe, _btnBrowseExe);
        AddRow("模型:", _txtModel, _btnBrowseModel);
        AddRow("端口:", _numPort, null);
        AddRow("ctx:", _numCtx, null);
        AddRow("ngl:", _numNgl, null);
        AddRow("parallel:", _numParallel, null);
        AddRow("kv:", _chkNoKv, null);
        AddRow("线程:", _numThreads, null);
        AddRow("load-mode:", _txtLoadMode, null);
        AddRow("ubatch:", _numUbatch, null);
        AddRow("batch:", _numBatch, null);
        AddRow("cache-type-k/v:", _txtCacheTypeKv, null);
        AddRow("flash-attn:", _chkFlashAttn, null);
        AddRow("spec-type:", _txtSpecType, null);
        AddRow("spec-draft-n-max:", _numSpecDraftNMax, null);
        AddRow("request-dump:", _chkRequestDump, null);
        _cmbLogQueuePolicy.Items.Add("drop-newest（保留历史，丢新入队）");
        _cmbLogQueuePolicy.Items.Add("drop-oldest（丢最旧，保留新消息）");
        AddRow("log-queue-full:", _cmbLogQueuePolicy, null);
        AddRow("tb(batch线程):", _numBatchThreads, null);
        AddRow("附加:", _txtExtra, null);
        AddRow("休眠(min):", _numIdleMin, null);
        AddRow("P核掩码:", _txtPcoreMask, null);
        AddRow("流式:", _chkForceStream, null);
        AddRow("缓存路径:", _txtKvCachePath, null);
        AddRow("Token Guard:", _chkTokenGuard, null);
        AddRow("输出预留:", _numReservedTokens, null);
        AddRow("Prompt头部开销:", _numPromptOverhead, null);
        AddRow("Cache-RAM(MiB):", _numCacheRam, null);
        AddRow("空闲slot缓存:", _chkNoCacheIdleSlots, null);
        AddRow("输出续接:", _chkContinuation, null);
        AddRow("最大续接:", _numMaxContinuations, null);
        AddRow("续接超时:", _numContTimeout, null);
        AddRow("崩溃恢复:", _chkCrashRecover, null);
        AddRow("最大重启:", _numMaxRestarts, null);
        var autoPreFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, BackColor = Color.Transparent };
        autoPreFlow.Controls.Add(_chkAutoPreDshRule);
        autoPreFlow.Controls.Add(_chkAutoPreWebui);
        autoPreFlow.Controls.Add(_chkAutoPreTrae);
        autoPreFlow.Controls.Add(_chkAutoPreDshAgent);
        AddRow("自动强占:", autoPreFlow, null);
        var snapFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, BackColor = Color.Transparent };
        snapFlow.Controls.Add(_chkSnapDshRule);
        snapFlow.Controls.Add(_chkSnapWebui);
        snapFlow.Controls.Add(_chkSnapTrae);
        snapFlow.Controls.Add(_chkSnapDshAgent);
        AddRow("自动快照:", snapFlow, null);
        // 模式行：标签 + CheckBox 同行（AutoSize 让 CheckBox 紧跟标签，不再撑满整行）
        var chkAutoRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, BackColor = Color.Transparent };
        chkAutoRow.Controls.Add(_chkAuto);
        AddRow("模式:", chkAutoRow, null);

        _tooltip.SetToolTip(_txtExtra, "原样拼入命令行；含空格的路径需加引号");
        _tooltip.SetToolTip(_chkForceStream, "把非流式请求改写为 stream=true。仅适用于能解析 SSE 的客户端。");
        _tooltip.SetToolTip(_txtKvCachePath, "KV Cache 保存目录（--slot-save-path）；多槽时驱逐自动 save，重绑定自动 restore。留空 = 禁用。");
        _tooltip.SetToolTip(_chkTokenGuard, "代理层预估算 + 裁剪，防上下文超长 400。预算 = ctx ÷ parallel − 输出预留。");
        _tooltip.SetToolTip(_numReservedTokens, "输出预留：为模型生成回复保留的 token 数（默认 8192）。预算 = ctx ÷ parallel − 输出预留 − Prompt头部开销预留。");
        _tooltip.SetToolTip(_numPromptOverhead, "Prompt 头部开销预留：tools 工具定义、system 提示词、Jinja 模板渲染带来的隐形 token，不计入对话消息统计（默认 10240）。工具数量增多时可调大。");
        _tooltip.SetToolTip(_numCacheRam, "llama.cpp 主机内存 Prompt-Cache 上限（MiB，--cache-ram）。0 = 关闭内置 prompt-cache（RAMDisk 快照全权接管模式，消除 LRU 驱逐虚假 KV-MISS）；回滚旧双兜底模式设 8192。");
        _tooltip.SetToolTip(_chkNoCacheIdleSlots, "禁止任务 release 后自动把空闲 slot 状态存入 prompt cache（--no-cache-idle-slots，与 Cache-RAM=0 配套）。");
        _tooltip.SetToolTip(_chkContinuation, "输出被 max_tokens 截断（finish_reason=length）时自动续写；工具调用/流式分片场景自动隔离不介入。");
        _tooltip.SetToolTip(_numMaxContinuations, "单次请求最多自动续接轮数（防死循环，默认 10）。");
        _tooltip.SetToolTip(_numContTimeout, "单轮推理超时秒数，超时返回已生成内容（默认 300）。");
        _tooltip.SetToolTip(_chkCrashRecover, "检测到 bad_alloc（任务级内存耗尽）时自动恢复：服务端存活→KV 快照接续/全量重放（SSE keep-alive 保活，客户端无感）；进程死亡→自动重启后重放。10 分钟内 ≥3 次崩溃触发熔断停止自动恢复。");
        _tooltip.SetToolTip(_numMaxRestarts, "进程死亡分支的最大自动重启次数（0 = 禁用自动重启，默认 2）。");
        _tooltip.SetToolTip(_txtLoadMode, "模型加载模式（--load-mode）：mlock = 全量加载 + 物理内存锁定，无页交换。");
        _tooltip.SetToolTip(_numUbatch, "Prefill 微批大小（--ubatch-size）：提升 prefill 单步并行度；阶段二调优 2048→4096，不得超过 batch。");
        _tooltip.SetToolTip(_numBatch, "Prompt 处理批量上限（--batch-size）：不得低于 ubatch 的 2 倍。");
        _tooltip.SetToolTip(_txtCacheTypeKv, "KV 缓存量化（q4_0 / q8_0 / f16），同时拼 --cache-type-k 与 --cache-type-v；切 q8_0 前必须核算显存。");
        _tooltip.SetToolTip(_chkFlashAttn, "Flash Attention（--flash-attn on）：prefill 速度核心开关，必开。");
        _tooltip.SetToolTip(_txtSpecType, "投机解码类型（--spec-type）：draft-mtp = MTP draft 模型，decode 提速 2~3 倍；留空 = 禁用。");
        _tooltip.SetToolTip(_numSpecDraftNMax, "每轮投机 draft token 数（--spec-draft-n-max）：0 = 不拼接该参数。");
        _tooltip.SetToolTip(_chkRequestDump, "勾选后 dump 所有请求体 + headers 到 logs/request_dump.log（应用识别分析用）；不勾选 = 关闭。");
        _tooltip.SetToolTip(_cmbLogQueuePolicy, "日志管道队列满（50k 行）时的丢弃策略：drop-newest = 保留历史日志、丢新入队（默认，排查更看重最早异常源头）；drop-oldest = 丢最旧、保留新消息。");
        _tooltip.SetToolTip(_numBatchThreads, "batch 阶段 CPU 线程数（--tb）：prefill 分词/调度辅助加速；0 = 不拼接。");
        _tooltip.SetToolTip(_chkAutoPreDshRule, "勾选后 DSH 规则引擎会话（dsh_rule_*）槽位自动强占：空闲不被 LRU 驱逐，再次提问零 Prefill 开销。");
        _tooltip.SetToolTip(_chkAutoPreWebui, "勾选后 WebUI 会话（webui_*）槽位自动强占：空闲不被 LRU 驱逐。");
        _tooltip.SetToolTip(_chkAutoPreTrae, "勾选后 Trae Work（trae_global）槽位自动强占：空闲不被 LRU 驱逐。");
        _tooltip.SetToolTip(_chkAutoPreDshAgent, "勾选后 DSH 主 Agent（dsh_agent_global）槽位自动强占：空闲不被 LRU 驱逐。注意 parallel=2 时若两槽都被强占，新会话将排队等待（上限 30s）。");
        _tooltip.SetToolTip(_chkSnapDshRule, "勾选后 DSH 规则引擎会话（dsh_rule_*）启用自动快照恢复：首请求存档 + 唤醒 eager restore；不锁槽，可被其他应用正常驱逐。");
        _tooltip.SetToolTip(_chkSnapWebui, "勾选后 WebUI 会话（webui_*）启用自动快照恢复：首请求存档 + 唤醒 eager restore；不锁槽，可被其他应用正常驱逐。");
        _tooltip.SetToolTip(_chkSnapTrae, "勾选后 Trae Work（trae_global）启用自动快照恢复：首请求存档 + 唤醒 eager restore；不锁槽，可被其他应用正常驱逐。");
        _tooltip.SetToolTip(_chkSnapDshAgent, "勾选后 DSH 主 Agent（dsh_agent_global）启用自动快照恢复：首请求存档 + 唤醒 eager restore；不锁槽，可被其他应用正常驱逐。");

        return panel;
    }

}