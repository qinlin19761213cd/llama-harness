namespace LlamaHarness;

/// <summary>
/// 主窗口外壳（薄 View）：装配区域 Controller/Presenter + 构建 UI + 生命周期。
/// 职责分工：UI 构建在 MainForm.Ui.cs；配置映射在 MainForm.Config.cs；命令编排与调度器事件路由在
/// MainFormPresenter；区域渲染/业务在 Status/Stats/Slot/Monitor PanelController；日志渲染在 LogView。
/// 进程管控与智能调度全部委托给 SmartScheduler。
/// </summary>
public partial class MainForm : Form
{

    private readonly AppConfig _config;
    private readonly SmartScheduler _scheduler;
    private readonly LogView _logView = new();

    // 区域 Controller / Presenter（视图模型层）：承载本区域的控件与业务逻辑
    private readonly StatusPanelView _status;
    private readonly StatsPanelView _stats;
    private readonly SlotPanelView _slot;
    private readonly MonitorPanelView _monitor;
    private readonly PerfSampler _perfSampler;
    private readonly PerfMonitorView _perfMonitor;
    private readonly MainFormPresenter _presenter;


    public MainForm()
    {
        _config = AppConfig.Load(out string? loadError);
        _scheduler = new SmartScheduler(_config)
        {
            AutoMode = _config.AutoMode,
            IdleMinutes = Math.Clamp(_config.IdleMinutes, 1, 120),
        };

        // 区域 Controller 装配（先于 BuildUi：各 Controller 的区域控件由 BuildPage 自持创建）
        _status = new StatusPanelView(_config, _scheduler, AppendLog);
        _stats = new StatsPanelView(_scheduler, _status, () => IsHandleCreated, InvokeOnUi);
        _slot = new SlotPanelView(_scheduler, AppendLog, _status.SetSlotSummary, () => IsHandleCreated, InvokeOnUi);
        _monitor = new MonitorPanelView(_config, _status, () => _scheduler.BackendPort, () => IsDisposed, AppendLog); // AH-1：监控采集用运行时后端端口
        _perfSampler = new PerfSampler(() => _scheduler.BackendPort, () => _scheduler.InflightCount,
            () => { var r = _scheduler.GetRestoreStats(); return r == null ? (0, 0, 0) : r.PerfSnapshot(); },
            () => _scheduler.SlotPerfSnapshot()); // v2.22 调度累积型快照 // v2.21 性能采样（双节奏 1s/5s，端口门控 cpp）
        _perfSampler.Sampled += OnPerfSampled; // 采样点 → perf.log（system 1s + cpp 5s）
        _scheduler.Timing.Completed += OnPerfTiming; // 请求时延 → perf.log（timing 事件）
        _perfMonitor = new PerfMonitorView(_perfSampler, _scheduler.Timing, _config.PerfThresholds, AppendLog); // v2.21 性能监控页
        _presenter = new MainFormPresenter(this, _config, _scheduler, _status, _stats, _slot, _monitor);

        BuildUi();

        LoadConfigToUi();
        LogFile.Configure(_config.LogQueueFullPolicy); // 异步日志管道队列满策略（立即生效）
        UpdatePortControlState(); // 智能模式下监听器占用端口，禁止编辑
        WireEvents();
        _presenter.AttachScheduler(); // 调度器事件 → LogView / 区域 Controller（内部统一 BeginInvoke）

        // 启动时按当前附加参数显示初始思考模式（唤醒时会按实际启动参数权威重置）
        _status.RefreshThinkingLabel();
        AppendLog($"思考模式初始状态：「{ThinkingMode.LabelOf(ThinkingMode.DetermineInitialThinkingMode(_config.ExtraArgs))}」");

        if (loadError != null)
            AppendLog(loadError);
        AutoFindExe();

        // 首帧渲染后再启动监听/布局，避免构造期间 BeginInvoke
        Shown += OnShown;
    }

    private void OnShown(object? sender, EventArgs e)
    {
        _scheduler.Initialize();

        // 系统资源改为手动触发（无轮询）：点击「手动刷新」按钮才采集一次

        PerfLog.Start(); // 性能日志写入器（独立直写 perf.log，5MB×3 轮切）

        // 性能采样器：常驻后台（1s 轻量 + 5s 慢指标），随应用生命周期启停（v2.21）
        _perfSampler.Start();

        // 日志防抖定时器：批量消费队列，减少 RichTextBox 重绘闪烁（LogView 常驻运行）
        _logView.Start();
    }

    // ==================== 事件 ====================

    private void WireEvents()
    {
        _btnBrowseExe.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Title = "选择 llama-server.exe",
                Filter = "llama-server.exe|llama-server.exe|所有文件 (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                _txtExe.Text = dlg.FileName;
        };

        _btnBrowseModel.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Title = "选择模型文件",
                Filter = "GGUF 模型 (*.gguf)|*.gguf|所有文件 (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                _txtModel.Text = dlg.FileName;
        };

        _btnClearLog.Click += (_, _) => _logView.Clear();
        _btnClearCache.Click += (_, _) => _presenter.OnClearCacheClicked();
        // 思考模式状态机 UI 入口：开启思考 → XHigh（深度推理）；开启极速 → Off（不注入思考参数，65+ t/s）
        _btnThinkOn.Click += (_, _) => _presenter.OnThinkOnClicked();
        _btnTurbo.Click += (_, _) => _presenter.OnTurboClicked();
        _btnExportCfg.Click += (_, _) => _presenter.OnExportConfigClicked();
        _btnImportCfg.Click += (_, _) => _presenter.OnImportConfigClicked();
        _btnStart.Click += (_, _) => _presenter.OnStartClicked();
        _btnStop.Click += (_, _) => _presenter.OnStopClicked();

        // 参数编辑实时同步到共享配置（唤醒时自动使用最新值）
        _txtExe.TextChanged += (_, _) => _presenter.OnParamEdited();
        _txtModel.TextChanged += (_, _) => _presenter.OnParamEdited();
        _numPort.ValueChanged += (_, _) => _presenter.OnParamEdited();
        _numCtx.ValueChanged += (_, _) => _presenter.OnParamEdited();
        _numNgl.ValueChanged += (_, _) => _presenter.OnParamEdited();
        _numParallel.ValueChanged += (_, _) => _presenter.OnParamEdited();
        _chkNoKv.CheckedChanged += (_, _) => _presenter.OnParamEdited();
        _numThreads.ValueChanged += (_, _) => _presenter.OnParamEdited();
        _txtExtra.TextChanged += (_, _) => _presenter.OnParamEdited();
        _txtPcoreMask.TextChanged += (_, _) => _presenter.OnParamEdited();
        _txtKvCachePath.TextChanged += (_, _) => _presenter.OnParamEdited();
        _chkForceStream.CheckedChanged += (_, _) => _presenter.OnParamEdited();
        _chkTokenGuard.CheckedChanged += (_, _) => _presenter.OnParamEdited();
        _numReservedTokens.ValueChanged += (_, _) => _presenter.OnParamEdited();
        _numCacheRam.ValueChanged += (_, _) => _presenter.OnParamEdited();
        _chkNoCacheIdleSlots.CheckedChanged += (_, _) => _presenter.OnParamEdited();
        _chkContinuation.CheckedChanged += (_, _) => _presenter.OnParamEdited();
        _numMaxContinuations.ValueChanged += (_, _) => _presenter.OnParamEdited();
        _numContTimeout.ValueChanged += (_, _) => _presenter.OnParamEdited();
        _chkCrashRecover.CheckedChanged += (_, _) => _presenter.OnParamEdited();
        _numMaxRestarts.ValueChanged += (_, _) => _presenter.OnParamEdited();
        _txtLoadMode.TextChanged += (_, _) => _presenter.OnParamEdited();
        _numUbatch.ValueChanged += (_, _) => _presenter.OnParamEdited();
        _numBatch.ValueChanged += (_, _) => _presenter.OnParamEdited();
        _txtCacheTypeKv.TextChanged += (_, _) => _presenter.OnParamEdited();
        _chkFlashAttn.CheckedChanged += (_, _) => _presenter.OnParamEdited();
        _txtSpecType.TextChanged += (_, _) => _presenter.OnParamEdited();
        _numSpecDraftNMax.ValueChanged += (_, _) => _presenter.OnParamEdited();
        _chkRequestDump.CheckedChanged += (_, _) => _presenter.OnParamEdited();
        _cmbLogQueuePolicy.SelectedIndexChanged += (_, _) => _presenter.OnParamEdited();
        _numBatchThreads.ValueChanged += (_, _) => _presenter.OnParamEdited();
        foreach (var c in _autoPreChecks) c.CheckedChanged += (_, _) => _presenter.OnParamEdited();
        foreach (var c in _snapChecks) c.CheckedChanged += (_, _) => _presenter.OnParamEdited();
        _numIdleMin.ValueChanged += (_, _) => _presenter.OnIdleEdited();
        _chkAuto.CheckedChanged += (_, _) => _presenter.OnAutoModeEdited();

        FormClosing += OnFormClosing;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _logView.StopAndFlush(); // 停表 + 刷出剩余日志（避免最后几条丢失）
        if (_scheduler.CurrentPhase is SmartScheduler.Phase.Running
            or SmartScheduler.Phase.Waking
            or SmartScheduler.Phase.Sleeping)
        {
            var r = MessageBox.Show(this,
                "llama-server 正在运行，确定停止并关闭？",
                "确认退出", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _scheduler.StopNow();
        }
        SyncConfigFromUi();
        if (!_config.Save(out string? err))
            AppendLog($"警告：配置保存失败：{err}");
        _perfSampler.Dispose(); // 停止后台采样（Step2 接线）
        _perfMonitor.Shutdown(); // 停止监控页定时器与事件订阅（v2.21）
        PerfLog.Stop(); // 关闭性能日志写入器（Flush 后释放文件）
        _scheduler.Dispose();
        LogFile.Shutdown(); // E-6：Flush + 关闭常驻日志写入器（防缓冲丢失）
    }

    // ==================== 日志 / 跨线程 / 命令（供 Presenter 与区域 Controller 调用） ====================

    /// <summary>追加一行日志（文件持久化 + UI 队列渲染由 LogView 承接）。可来自任意线程。</summary>
    // —— 性能日志回调（v2.21）：后台线程触发，PerfLog 自身线程安全 ——
    private void OnPerfSampled(PerfPoint p)
    {
        PerfLog.LogSystem(p);
        if (p.HasCpp) PerfLog.LogCpp(p); // cpp 字段非空才写（5s 节奏）
    }

    private void OnPerfTiming(RequestTiming t) => PerfLog.LogTiming(t);

    internal void AppendLog(string line) => _logView.Append(line);

    /// <summary>跨线程切回 UI 线程执行（句柄未创建时静默丢弃，同原 BeginInvoke 语义）。</summary>
    internal void InvokeOnUi(Action action)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(action);
    }

    /// <summary>清空缓存执行期禁用/恢复按钮。</summary>
    internal void SetClearCacheEnabled(bool enabled) => _btnClearCache.Enabled = enabled;

    /// <summary>智能空闲分钟数（OnIdleEdited 即时更新调度器用）。</summary>
    internal int IdleMinutesValue => (int)_numIdleMin.Value;

    /// <summary>参数 CheckBox 集合（ApplyPhase 禁用时刷新为灰；启用时恢复黑）。</summary>
    private CheckBox[] ParamCheckBoxes => new[]
    {
        _chkNoKv, _chkAuto, _chkForceStream, _chkTokenGuard, _chkContinuation, _chkCrashRecover,
        _chkNoCacheIdleSlots,
    }.Concat(_autoPreChecks).Concat(_snapChecks).ToArray();
}
