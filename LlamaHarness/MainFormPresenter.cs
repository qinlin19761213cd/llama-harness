namespace LlamaHarness;

/// <summary>
/// MainForm Presenter：承载全局命令编排（启动/停止/清缓存/配置导入导出/思考切换/参数联动）与调度器事件路由。
/// 不直接触碰控件——控件交互经 MainForm 的 internal 命令方法（SyncConfigFromUi/SetClearCacheEnabled 等），
/// 区域渲染委托给各区域 Controller（Status/Stats/Slot/Monitor），日志委托 LogView。
/// </summary>
public sealed class MainFormPresenter
{
    private readonly MainForm _view;
    private readonly AppConfig _config;
    private readonly SmartScheduler _scheduler;
    private readonly StatusPanelView _status;
    private readonly StatsPanelView _stats;
    private readonly SlotPanelView _slot;
    private readonly MonitorPanelView _monitor;

    public MainFormPresenter(MainForm view, AppConfig config, SmartScheduler scheduler,
        StatusPanelView status, StatsPanelView stats, SlotPanelView slot,
        MonitorPanelView monitor)
    {
        _view = view;
        _config = config;
        _scheduler = scheduler;
        _status = status;
        _stats = stats;
        _slot = slot;
        _monitor = monitor;
    }

    // M-08/M-09 修复：保存事件处理器引用以便 DetachScheduler 正确取消订阅
    private Action<string>? _schedLogHandler;
    private Action<string>? _schedStatusHandler;
    private Action? _schedInflightHandler;
    private Action<SmartScheduler.Phase>? _schedPhaseHandler;
    private Action? _schedStatsResetHandler;
    private Action<SmartScheduler.ThinkingLevel>? _schedThinkingModeHandler;
    private Action? _schedSlotBindingChangedHandler;
    private Action<string>? _schedSlotLogHandler;

    /// <summary>订阅调度器全部事件并路由到 LogView / 区域 Controller（UI 侧经 BeginInvoke 切回）。</summary>
    public void AttachScheduler()
    {
        _schedLogHandler = line => { _view.AppendLog(line); _stats.FeedLine(line); };
        _schedStatusHandler = text => _view.InvokeOnUi(() => _status.SetStatusText(text));
        _schedInflightHandler = () => _view.InvokeOnUi(_status.RefreshInFlightTasks); // 在途任务明细（服务阶段卡片）
        _schedPhaseHandler = phase =>
        {
            // C-007：统计重置由调度器状态机驱动（Waking 时自动触发）
            if (phase == SmartScheduler.Phase.Waking)
                _stats.Reset();
            _view.InvokeOnUi(() =>
            {
                _status.ApplyPhase(phase);
                // 唤醒后刷新槽位绑定/管理页面（已恢复的历史绑定不触发 SlotBindingChanged）
                if (phase == SmartScheduler.Phase.Running)
                    _slot.RefreshBindings();
            });
        };
        _schedStatsResetHandler = () => _stats.Reset();
        _schedThinkingModeHandler = level => _view.InvokeOnUi(() => _status.UpdateThinkingLabel(level));
        _schedSlotBindingChangedHandler = () => _slot.RefreshBindings();
        _schedSlotLogHandler = line => _slot.OnSlotLog(line);

        _scheduler.Log += _schedLogHandler!;
        _scheduler.StatusChanged += _schedStatusHandler!;
        _scheduler.InFlightChanged += _schedInflightHandler!;
        _scheduler.PhaseChanged += _schedPhaseHandler!;
        _scheduler.StatsReset += _schedStatsResetHandler!;
        _scheduler.ThinkingModeChanged += _schedThinkingModeHandler!;
        _scheduler.SlotBindingChanged += _schedSlotBindingChangedHandler!;
        _scheduler.SlotLog += _schedSlotLogHandler!;
    }

    /// <summary>取消订阅调度器全部事件（M-08/M-09 修复：防止事件泄漏）。</summary>
    public void DetachScheduler()
    {
        if (_schedLogHandler != null) _scheduler.Log -= _schedLogHandler;
        if (_schedStatusHandler != null) _scheduler.StatusChanged -= _schedStatusHandler;
        if (_schedInflightHandler != null) _scheduler.InFlightChanged -= _schedInflightHandler;
        if (_schedPhaseHandler != null) _scheduler.PhaseChanged -= _schedPhaseHandler;
        if (_schedStatsResetHandler != null) _scheduler.StatsReset -= _schedStatsResetHandler;
        if (_schedThinkingModeHandler != null) _scheduler.ThinkingModeChanged -= _schedThinkingModeHandler;
        if (_schedSlotBindingChangedHandler != null) _scheduler.SlotBindingChanged -= _schedSlotBindingChangedHandler;
        if (_schedSlotLogHandler != null) _scheduler.SlotLog -= _schedSlotLogHandler;
    }

    /// <summary>启动/唤醒：同步配置 → 异步启动（失败弹窗）。</summary>
    // P0-H-03 修复：消除 async void，使用 async Task 内部方法 + async void 包装器
    // async void 仅用于事件处理器入口，内部逻辑走 async Task 确保异常被正确捕获
    public async void OnStartClicked() => await OnStartClickedAsync();

    private async Task OnStartClickedAsync()
    {
        try
        {
            _view.SyncConfigFromUi();
            // v2.23.6：唤醒链（EnsureRunning→WakeUp→KV restore→dummy 预热）整体在 Task.Run 线程池执行——
            // 其内部 await 均无 ConfigureAwait(false)，若从 UI 线程启动则恢复点回 UI 线程 SynchronizationContext，
            // 唤醒期间大量逻辑（KV restore/预热/日志/SetPhase 回调）穿插占用 UI 线程。后台执行 + 事件封送安全。
            await Task.Run(() => _scheduler.LaunchNowAsync());
        }
        catch (Exception ex)
        {
            // P0-H-03 修复：MessageBox.Show 必须在 UI 线程调用
            _view.InvokeOnUi(() => MessageBox.Show(_view, $"启动失败：\n{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error));
        }
    }

    /// <summary>停止：同步配置 → 立即停止调度器。</summary>
    public void OnStopClicked()
    {
        _view.SyncConfigFromUi();
        _scheduler.StopNow();
    }

    /// <summary>清空 KV Cache：删除缓存目录下所有 *.bin + erase 全部槽位（二次确认 + 执行期禁用按钮）。</summary>
    // P0-H-04 修复：消除 async void，使用 async Task 内部方法 + async void 包装器
    public async void OnClearCacheClicked() => await OnClearCacheClickedAsync();

    private async Task OnClearCacheClickedAsync()
    {
        var kv = _scheduler.GetKvCache();
        if (kv == null)
        {
            _view.InvokeOnUi(() => MessageBox.Show(_view, "KV Cache 未启用（需要 --parallel > 1 且配置了缓存路径）。\n\n请在配置管理中设置「缓存路径」并把 Parallel 改为 2，然后重新启动。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information));
            return;
        }

        var confirm = _view.InvokeOnUiSync(() => MessageBox.Show(_view, "确定清空所有 KV Cache 缓存？\n将删除缓存目录下所有 .bin 文件并擦除全部槽位。", "确认",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question));
        if (confirm != DialogResult.Yes)
            return;

        _view.SetClearCacheEnabled(false);
        try
        {
            // v2.23.6：清缓存（删 *.bin 同步循环 + 逐槽 erase）移出 UI 线程——ClearAllAsync 内的同步段
            // 在 await 恢复的线程执行，直接从 UI 线程调用会占用 UI 线程。
            int deleted = await Task.Run(() => kv.ClearAllAsync());
            _view.AppendLog($"KV Cache 已清空：删除 {deleted} 个缓存文件，全部槽位已擦除。");
        }
        catch (Exception ex)
        {
            _view.AppendLog($"KV Cache 清空失败：{ex.Message}");
        }
        finally
        {
            _view.SetClearCacheEnabled(true);
        }
    }

    /// <summary>保存配置到…：把当前窗口全部配置项序列化到用户选择的 json 文件。</summary>
    public void OnExportConfigClicked()
    {
        _view.SyncConfigFromUi();
        using var dlg = new SaveFileDialog
        {
            Title = "保存配置到…",
            Filter = "JSON 配置文件 (*.json)|*.json",
            FileName = "llama-harness-config.json",
        };
        if (dlg.ShowDialog(_view) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dlg.FileName,
                System.Text.Json.JsonSerializer.Serialize(_config,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            _view.AppendLog($"配置已保存到：{dlg.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(_view, $"保存失败：\n{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>载入配置：读取 json 文件，校验后写入当前窗口全部配置项。</summary>
    public void OnImportConfigClicked()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "载入配置",
            Filter = "JSON 配置文件 (*.json)|*.json",
        };
        if (dlg.ShowDialog(_view) != DialogResult.OK) return;
        try
        {
            var cfg = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(dlg.FileName))
                ?? throw new InvalidOperationException("反序列化结果为空");

            AppConfig.Sanitize(cfg); // 数值兜底：与 Load 共用统一规则（集中维护，防漂移）

            _view.ApplyConfigToUi(cfg);    // 写入全部 UI 控件
            _view.SyncConfigFromUi();      // UI → 共享配置对象（下次唤醒即生效）
            _status.RefreshThinkingLabel();// 附加参数可能变化，同步刷新思考模式标签
            _view.AppendLog($"配置已载入：{dlg.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(_view, $"载入失败：\n{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>参数编辑实时同步到共享配置（唤醒时自动使用最新值）。</summary>
    public void OnParamEdited() => _view.SyncConfigFromUi();

    /// <summary>智能空闲分钟数编辑：同步配置 + 即时更新调度器。</summary>
    public void OnIdleEdited()
    {
        _view.SyncConfigFromUi();
        _scheduler.IdleMinutes = _view.IdleMinutesValue;
    }

    /// <summary>自动模式切换：同步配置 + 调度器切模式 + 端口编辑态 + 持久化。</summary>
    public void OnAutoModeEdited()
    {
        _view.SyncConfigFromUi();
        _scheduler.SetAutoMode(_config.AutoMode);
        _view.UpdatePortControlState();
        if (!_config.Save(out string? err))
            _view.AppendLog($"警告：配置保存失败：{err}");
    }

    /// <summary>开启思考模式 → XHigh（深度推理）。</summary>
    public void OnThinkOnClicked() => _scheduler.SetThinkingMode(SmartScheduler.ThinkingLevel.XHigh);

    /// <summary>开启极速模式 → Off（不注入思考参数，65+ t/s）。</summary>
    public void OnTurboClicked() => _scheduler.SetThinkingMode(SmartScheduler.ThinkingLevel.Off);
}
