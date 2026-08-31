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

    /// <summary>订阅调度器全部事件并路由到 LogView / 区域 Controller（UI 侧经 BeginInvoke 切回）。</summary>
    public void AttachScheduler()
    {
        _scheduler.Log += line => { _view.AppendLog(line); _stats.FeedLine(line); };
        _scheduler.StatusChanged += text => _view.InvokeOnUi(() => _status.SetStatusText(text));
        _scheduler.InFlightChanged += () => _view.InvokeOnUi(_status.RefreshInFlightTasks); // 在途任务明细（服务阶段卡片）
        _scheduler.PhaseChanged += phase =>
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
        _scheduler.StatsReset += () => _stats.Reset();
        _scheduler.ThinkingModeChanged += level => _view.InvokeOnUi(() => _status.UpdateThinkingLabel(level));
        _scheduler.SlotBindingChanged += () => _slot.RefreshBindings();
        _scheduler.SlotLog += line => _slot.OnSlotLog(line);
    }

    /// <summary>启动/唤醒：同步配置 → 异步启动（失败弹窗）。</summary>
    public async void OnStartClicked()
    {
        _view.SyncConfigFromUi();
        try
        {
            await _scheduler.LaunchNowAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(_view, $"启动失败：\n{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>停止：同步配置 → 立即停止调度器。</summary>
    public void OnStopClicked()
    {
        _view.SyncConfigFromUi();
        _scheduler.StopNow();
    }

    /// <summary>清空 KV Cache：删除缓存目录下所有 *.bin + erase 全部槽位（二次确认 + 执行期禁用按钮）。</summary>
    public async void OnClearCacheClicked()
    {
        var kv = _scheduler.GetKvCache();
        if (kv == null)
        {
            MessageBox.Show(_view, "KV Cache 未启用（需要 --parallel > 1 且配置了缓存路径）。\n\n请在配置管理中设置「缓存路径」并把 Parallel 改为 2，然后重新启动。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show(_view, "确定清空所有 KV Cache 缓存？\n将删除缓存目录下所有 .bin 文件并擦除全部槽位。", "确认",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _view.SetClearCacheEnabled(false);
        try
        {
            int deleted = await kv.ClearAllAsync();
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
