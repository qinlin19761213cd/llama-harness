namespace LlamaHarness;

/// <summary>
/// 主窗口：参数区（黄金底参默认值）+ 操作区 + 日志区（自动滚动）。
/// 进程管控与智能调度全部委托给 SmartScheduler；本类只负责 UI 渲染、控件启停状态。
/// UI 状态机防重复启动：唤醒/运行/休眠期间禁用启动按钮与全部参数控件。
/// </summary>
public partial class MainForm : Form
{

    private readonly AppConfig _config;
    private readonly SmartScheduler _scheduler;
    private readonly LogView _logView = new();


    public MainForm()
    {
        _config = AppConfig.Load(out string? loadError);
        _scheduler = new SmartScheduler(_config)
        {
            AutoMode = _config.AutoMode,
            IdleMinutes = Math.Clamp(_config.IdleMinutes, 1, 120),
        };

        BuildUi();
        LoadConfigToUi();
        LogFile.Configure(_config.LogQueueFullPolicy); // 异步日志管道队列满策略（立即生效）
        UpdatePortControlState(); // 智能模式下监听器占用端口，禁止编辑
        WireEvents();

        // 调度器事件 → UI（内部统一 BeginInvoke）
        _scheduler.Log += AppendLog;
        _scheduler.StatusChanged += OnSchedulerStatus;
        _scheduler.PhaseChanged += OnPhaseChanged;
        // C-007：统计重置由调度器状态机驱动（Waking 时自动触发），不再依赖 UI 监听 PhaseChanged
        _scheduler.StatsReset += () => _statsParser.Reset();
        // 思考模式状态变更 → UI 标签
        _scheduler.ThinkingModeChanged += OnThinkingModeChanged;
        // 槽位绑定变更 → 刷新槽位表格
        _scheduler.SlotBindingChanged += RefreshSlotBindings;
        // 槽位日志（绑定/驱逐/KV Cache）→ 槽位页 RichTextBox + slot.log 持久化
        _scheduler.SlotLog += OnSlotLog;



        // 启动时按当前附加参数显示初始思考模式（唤醒时会按实际启动参数权威重置）
        RefreshThinkingLabel();
        AppendLog($"思考模式初始状态：「{SmartScheduler.LabelOf(SmartScheduler.DetermineInitialThinkingMode(_config.ExtraArgs))}」");

        // 统计：日志行喂给解析器；解析结果/会话重置回 UI
        _scheduler.Log += line => _statsParser.Feed(line);
        _statsParser.RoundUpdated += OnRoundUpdated;
        _statsParser.RoundRemoved += OnRoundRemoved;
        _statsParser.SessionReset += OnSessionReset;

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

        // 日志防抖定时器：批量消费队列，减少 RichTextBox 重绘闪烁（LogView 常驻运行）
        _logView.Start();
    }

    /// <summary>手动刷新：采集系统资源（本地）+ llama.cpp 三接口（HTTP），更新页面。</summary>
    private async void OnManualRefresh(object? sender, EventArgs e)
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

            if (IsDisposed) return;

            // 3. 更新 UI
            _lblSysRes.Text =
                $"CPU:      {cpu:F0}%\n" +
                $"内存:     {used:F1} / {total:F1} GB\n" +
                $"显存:     {(vram ?? "—（未检测到 nvidia-smi）")}";

            // llama.cpp 三卡片：分区容错，各自独立显示成功/失败
            UpdateSlotsCard(snap);
            UpdatePropsCard(snap);
            UpdateMetricsCard(snap);

            // 时间戳
            _lblResTimestamp.Text = $"上次采集: {DateTime.Now:HH:mm:ss}";

            // 右侧状态面板摘要（保持原有行为）
            _lblResSummary.Text = $"CPU {cpu:F0}% | 内存 {used:F1}/{total:F1}GB";
            _lblRunTime.Text = _wakeTime is DateTime wt ? (DateTime.Now - wt).ToString(@"hh\:mm\:ss") : "—";

            // 崩溃熔断红色告警（保留原有逻辑）
            bool tripped = CrashRecovery.IsTripped;
            if (tripped && !_crashAlertShown)
            {
                _crashAlertShown = true;
                AppendLog("⚠⚠ 崩溃熔断器已跳闸：10 分钟内 ≥3 次 bad_alloc，自动恢复已停止。请加内存 / 降上下文后手动重试！");
                _lblStatus.ForeColor = Color.FromArgb(0xF5, 0x3F, 0x3F);
                _lblStatus.Text = "⚠ 崩溃熔断：自动恢复已停止，需人工介入";
            }
            else if (!tripped && _crashAlertShown)
            {
                _crashAlertShown = false;
                ApplyPhase(_scheduler.CurrentPhase);
            }
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

        // 重建表格行（左标签 + 右值，带分隔线）
        _tblPropsBody.Controls.Clear();
        int rowIdx = 0;
        foreach (var kv in p.RawFields)
        {
            if (string.IsNullOrEmpty(kv.Value)) continue;
            if (kv.Key == "chat_template") continue; // 太长，只在 Raw 区显示
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
        // 提取关键指标行（含 memory/kv/throughput/tokens 的 metrics）
        var lines = snap.RawMetricsText.Split('\n');
        var keyLines = lines.Where(l => l.Contains("memory") || l.Contains("kv_") || l.Contains("throughput") || l.Contains("tokens"))
                             .Take(10);
        _lblMetricsBody.Text = string.Join("\n", keyLines) + (keyLines.Count() < lines.Length ? "\n…（完整报文见下方折叠区）" : "");
        _btnRawMetrics.Visible = true;
        _rawMetricsBox.Text = snap.RawMetricsText;
    }

    /// <summary>切换 Raw 折叠区（TextBox）显示/隐藏，并强制重算布局。</summary>
    private void ToggleRaw(Button btn, TextBox box)
    {
        bool show = !box.Visible;
        box.Visible = show;
        btn.Text = show ? "收起原始报文 ▴" : "查看原始报文 ▸";
        // 强制父容器重算布局（卡片高度随内容变化）
        var parent = box.Parent as Panel;
        if (parent != null)
        {
            parent.Invalidate(true);
            parent.Update();
        }
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
        _btnClearCache.Click += OnClearCacheClick;
        // 思考模式状态机 UI 入口：开启思考 → XHigh（深度推理）；开启极速 → Off（不注入思考参数，65+ t/s）
        _btnThinkOn.Click += (_, _) => _scheduler.SetThinkingMode(SmartScheduler.ThinkingLevel.XHigh);
        _btnTurbo.Click += (_, _) => _scheduler.SetThinkingMode(SmartScheduler.ThinkingLevel.Off);
        _btnClearStats.Click += (_, _) => _statsParser.Reset();
        _btnExportCfg.Click += OnExportConfigClick;
        _btnImportCfg.Click += OnImportConfigClick;
        _btnStart.Click += OnStartClick;
        _btnStop.Click += (_, _) =>
        {
            SyncUiToConfig();
            _scheduler.StopNow();
        };

        // 参数编辑实时同步到共享配置（唤醒时自动使用最新值）
        _txtExe.TextChanged += OnParamEdited;
        _txtModel.TextChanged += OnParamEdited;
        _numPort.ValueChanged += OnParamEdited;
        _numCtx.ValueChanged += OnParamEdited;
        _numNgl.ValueChanged += OnParamEdited;
        _numParallel.ValueChanged += OnParamEdited;
        _chkNoKv.CheckedChanged += OnParamEdited;
        _numThreads.ValueChanged += OnParamEdited;
        _txtExtra.TextChanged += OnParamEdited;
        _txtPcoreMask.TextChanged += OnParamEdited;
        _txtKvCachePath.TextChanged += OnParamEdited;
        _chkForceStream.CheckedChanged += OnParamEdited;
        _chkTokenGuard.CheckedChanged += OnParamEdited;
        _numReservedTokens.ValueChanged += OnParamEdited;
        _numCacheRam.ValueChanged += OnParamEdited;
        _chkNoCacheIdleSlots.CheckedChanged += OnParamEdited;
        _chkContinuation.CheckedChanged += OnParamEdited;
        _numMaxContinuations.ValueChanged += OnParamEdited;
        _numContTimeout.ValueChanged += OnParamEdited;
        _chkCrashRecover.CheckedChanged += OnParamEdited;
        _numMaxRestarts.ValueChanged += OnParamEdited;
        _txtLoadMode.TextChanged += OnParamEdited;
        _numUbatch.ValueChanged += OnParamEdited;
        _numBatch.ValueChanged += OnParamEdited;
        _txtCacheTypeKv.TextChanged += OnParamEdited;
        _chkFlashAttn.CheckedChanged += OnParamEdited;
        _txtSpecType.TextChanged += OnParamEdited;
        _numSpecDraftNMax.ValueChanged += OnParamEdited;
        _chkRequestDump.CheckedChanged += OnParamEdited;
        _cmbLogQueuePolicy.SelectedIndexChanged += OnParamEdited;
        _numBatchThreads.ValueChanged += OnParamEdited;
        _chkAutoPreDshRule.CheckedChanged += OnParamEdited;
        _chkAutoPreWebui.CheckedChanged += OnParamEdited;
        _chkAutoPreTrae.CheckedChanged += OnParamEdited;
        _chkAutoPreDshAgent.CheckedChanged += OnParamEdited;
        _chkSnapDshRule.CheckedChanged += OnParamEdited;
        _chkSnapWebui.CheckedChanged += OnParamEdited;
        _chkSnapTrae.CheckedChanged += OnParamEdited;
        _chkSnapDshAgent.CheckedChanged += OnParamEdited;
        _numIdleMin.ValueChanged += OnIdleEdited;
        _chkAuto.CheckedChanged += OnAutoModeEdited;

        FormClosing += OnFormClosing;
    }

    private void OnParamEdited(object? sender, EventArgs e) => SyncUiToConfig();

    private void OnIdleEdited(object? sender, EventArgs e)
    {
        SyncUiToConfig();
        _scheduler.IdleMinutes = (int)_numIdleMin.Value;
    }

    private void OnAutoModeEdited(object? sender, EventArgs e)
    {
        SyncUiToConfig();
        _scheduler.SetAutoMode(_config.AutoMode);
        UpdatePortControlState();
        if (!_config.Save(out string? err))
            AppendLog($"警告：配置保存失败：{err}");
    }

    private async void OnStartClick(object? sender, EventArgs e)
    {
        SyncUiToConfig();
        try
        {
            await _scheduler.LaunchNowAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"启动失败：\n{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ==================== 配置导出 / 导入（独立 json 文件） ====================

    /// <summary>保存配置到…：把当前窗口全部配置项序列化到用户选择的 json 文件。</summary>
    private void OnExportConfigClick(object? sender, EventArgs e)
    {
        SyncUiToConfig();
        using var dlg = new SaveFileDialog
        {
            Title = "保存配置到…",
            Filter = "JSON 配置文件 (*.json)|*.json",
            FileName = "llama-harness-config.json",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dlg.FileName,
                System.Text.Json.JsonSerializer.Serialize(_config,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            AppendLog($"配置已保存到：{dlg.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存失败：\n{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>载入配置：读取 json 文件，校验后写入当前窗口全部配置项。</summary>
    private void OnImportConfigClick(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "载入配置",
            Filter = "JSON 配置文件 (*.json)|*.json",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var cfg = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(dlg.FileName))
                ?? throw new InvalidOperationException("反序列化结果为空");

            // 数值兜底：与 AppConfig.Load 相同规则，防止越界值
            if (cfg.Port is < 1 or > 65534) cfg.Port = 8080;
            if (cfg.CtxSize <= 0) cfg.CtxSize = 262144;
            if (cfg.Ngl < 0) cfg.Ngl = 999;
            if (cfg.Parallel <= 0) cfg.Parallel = 1;
            if (cfg.Threads <= 0) cfg.Threads = Environment.ProcessorCount;
            if (cfg.IdleMinutes <= 0) cfg.IdleMinutes = 15;

            WriteConfigToUi(cfg);   // 写入全部 UI 控件
            SyncUiToConfig();       // UI → 共享配置对象（下次唤醒即生效）
            RefreshThinkingLabel(); // 附加参数可能变化，同步刷新思考模式标签
            AppendLog($"配置已载入：{dlg.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"载入失败：\n{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>清空 KV Cache：删除缓存目录下所有 *.bin + erase 全部槽位。</summary>
    private async void OnClearCacheClick(object? sender, EventArgs e)
    {
        var kv = _scheduler.GetKvCache();
        if (kv == null)
        {
            MessageBox.Show(this, "KV Cache 未启用（需要 --parallel > 1 且配置了缓存路径）。\n\n请在配置管理中设置「缓存路径」并把 Parallel 改为 2，然后重新启动。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show(this, "确定清空所有 KV Cache 缓存？\n将删除缓存目录下所有 .bin 文件并擦除全部槽位。", "确认",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _btnClearCache.Enabled = false;
        try
        {
            int deleted = await kv.ClearAllAsync();
            AppendLog($"KV Cache 已清空：删除 {deleted} 个缓存文件，全部槽位已擦除。");
        }
        catch (Exception ex)
        {
            AppendLog($"KV Cache 清空失败：{ex.Message}");
        }
        finally
        {
            _btnClearCache.Enabled = true;
        }
    }

    /// <summary>调度器状态文本（非 UI 线程）→ 状态栏。</summary>
    private void OnSchedulerStatus(string text)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() => _lblStatus.Text = text);
    }

    /// <summary>思考模式状态变更（非 UI 线程）→ 标签更新。</summary>
    private void OnThinkingModeChanged(SmartScheduler.ThinkingLevel level)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() => UpdateThinkingLabel(level));
    }

    /// <summary>更新思考模式标签文本和颜色（四档：极速/轻度/中度/深度）。</summary>
    private void UpdateThinkingLabel(SmartScheduler.ThinkingLevel level)
    {
        _lblThinking.Text = $"思考: {SmartScheduler.LabelOf(level)}";
        _lblThinking.ForeColor = level switch
        {
            SmartScheduler.ThinkingLevel.Off => Color.Silver,
            SmartScheduler.ThinkingLevel.Low => Color.LightGreen,
            SmartScheduler.ThinkingLevel.Medium => Color.DodgerBlue,
            _ => Color.LightBlue, // XHigh
        };
    }

    /// <summary>按当前启动附加参数刷新思考模式标签（仅显示；权威重置在 SmartScheduler 唤醒时执行）。
    /// --reasoning on → XHigh；--reasoning off 或无该参数 → Off（默认不思考）。</summary>
    private void RefreshThinkingLabel()
    {
        UpdateThinkingLabel(SmartScheduler.DetermineInitialThinkingMode(_config.ExtraArgs));
    }

    /// <summary>槽位绑定变更（非 UI 线程）→ 刷新槽位表格 + 管理表格 + 侧边摘要。</summary>
    private void RefreshSlotBindings()
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            RefreshSlotGrid();
            RefreshSlotMgmtGrid();
        });
    }

    /// <summary>从调度器获取槽位快照并填充绑定表格 + 管理表格 + 侧边摘要标签。</summary>
    private void RefreshSlotGrid()
    {
        var bindings = _scheduler.GetSlotBindings();
        if (bindings == null || bindings.Count == 0)
        {
            _gridSlots.Rows.Clear();
            _lblSlotSummary.Text = "槽位: 0 绑定";
            return;
        }
        _gridSlots.Rows.Clear();
        foreach (var (key, app, slot, lastActive, _, _) in bindings)
            _gridSlots.Rows.Add(key, app, $"slot {slot}", lastActive.ToString("HH:mm:ss"));
        _lblSlotSummary.Text = $"槽位: {bindings.Count} 绑定";
    }

    /// <summary>填充槽位管理表格（强占/KV缓存 CheckBox 可编辑）。</summary>
    private void RefreshSlotMgmtGrid()
    {
        var bindings = _scheduler.GetSlotBindings();
        if (bindings == null)
        {
            _gridSlotMgmt.Rows.Clear();
            _slotMgmtRowIdx.Clear();
            return;
        }
        // 行 Key = 亲和 Key，避免整表 Clear 后重复刷新闪烁；Dictionary 索引消除 O(n²) 线性扫 Tag（审计）
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

    /// <summary>槽位日志事件（非 UI 线程）→ 显示到槽位页 RichTextBox + slot.log 持久化。</summary>
    private void OnSlotLog(string line)
    {
        LogFile.SlotAppend(line); // 文件持久化（独立 slot.log，2MB 轮切）
        if (!IsHandleCreated) return;
        BeginInvoke(() => AppendSlotLog(line));
    }

    /// <summary>追加一行槽位日志到 RichTextBox（带时间戳 + 级别着色），自动滚到底部。字符上限防膨胀。</summary>
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
        // 着色本行
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
                AppendLog($"槽位管理：{key} 强占模式 → {(preemptive ? "开启" : "关闭")}");
                break;
            case 4: // KV缓存
                bool kvCache = row.Cells[4].Value is true;
                row.Cells[4].Value = kvCache;
                _scheduler.SetSlotKvCache(key, kvCache);
                AppendLog($"槽位管理：{key} KV Cache → {(kvCache ? "开启" : "关闭")}");
                break;
        }
    }

    /// <summary>阶段切换（非 UI 线程）→ 控件启停 + 状态颜色；唤醒 = 新会话，清空统计。</summary>
    private void OnPhaseChanged(SmartScheduler.Phase phase)
    {
        // llama-server 重启后 task ID 从 0 重新计数，必须重置解析器防跨会话 ID 冲突
        if (phase == SmartScheduler.Phase.Waking)
            _statsParser.Reset();
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            ApplyPhase(phase);
            // 唤醒后刷新槽位绑定/管理页面：SlotAffinity 在唤醒时创建并从 slot_bindings.json 恢复历史绑定，
            // 但 SlotBindingChanged 仅在新绑定创建时触发，已恢复的绑定不会触发 → 此处主动刷新
            if (phase == SmartScheduler.Phase.Running)
                RefreshSlotBindings();
        });
    }

    // ==================== 统计 ====================

    /// <summary>一轮统计更新（进程输出线程）→ 表格行增量刷新 + 汇总；新行自动滚到底部。</summary>
    private void OnRoundUpdated(LlamaStatsParser.RoundStats s)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            var row = FindStatRow(s.Id);
            bool isNew = row == null;
            if (isNew)
            {
                int idx = _gridStats.Rows.Add();
                row = _gridStats.Rows[idx];
                row.Tag = s.Id;
                _statsRowIdx[s.Id] = row; // E-10：索引登记
            }
            if (row != null)
                FillStatRow(row, s);
            UpdateSummary();
            // 仅新增行时滚动到最后一行（已有行的更新不打扰阅读）：设 CurrentCell 会自动滚入视图
            if (isNew && row != null)
                _gridStats.CurrentCell = row.Cells[0];
        });
    }

    /// <summary>超出 50 轮上限、最旧轮次被淘汰（解析器线程）→ 删除对应表格行。</summary>
    private void OnRoundRemoved(LlamaStatsParser.RoundStats s)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            var row = FindStatRow(s.Id);
            if (row != null)
            {
                _gridStats.Rows.Remove(row);
                _statsRowIdx.Remove(s.Id); // E-10：索引同步移除
            }
            UpdateSummary(); // 行被淘汰后刷新汇总，保持请求数/合计与表格一致
        });
    }

    /// <summary>会话重置（解析器线程）→ 清空表格。</summary>
    private void OnSessionReset()
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            _gridStats.Rows.Clear();
            _statsRowIdx.Clear(); // E-10：索引同步清空
            _lblSummary.Text = "请求: 0";
        });
    }

    /// <summary>E-10：字典 O(1) 查找（替代原线性扫 Tag）。</summary>
    private DataGridViewRow? FindStatRow(long id)
    {
        return _statsRowIdx.TryGetValue(id, out var r) ? r : null;
    }

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

    /// <summary>累计汇总：请求数、总 tokens、平均速度、加权命中率。同步更新侧边统计标签。</summary>
    private void UpdateSummary()
    {
        UpdateRestoreCard(); // 3.1：Restore 命中率卡片（独立数据源，随轮次统计同步刷新）
        var rounds = _statsParser.GetRounds();
        if (rounds.Count == 0)
        {
            _lblSummary.Text = "请求: 0";
            _lblTokenSummary.Text = "请求: 0";
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
        _lblTokenSummary.Text = summary;
    }

    /// <summary>3.1 Restore 命中率卡片：总命中率 + 误报率 + 最近一次明细；颜色按阈值（≥80% 绿 / &lt;80% 黄 / &lt;50% 红）。</summary>
    private void UpdateRestoreCard()
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

    private void ApplyPhase(SmartScheduler.Phase phase)
    {
        // 唤醒时刻追踪：进入 Running 记录（离开 Running 清空）→ 运行时长卡片显示
        _wakeTime = phase == SmartScheduler.Phase.Running ? (_wakeTime ?? DateTime.Now) : null;

        bool busy = phase is SmartScheduler.Phase.Waking
                    or SmartScheduler.Phase.Running
                    or SmartScheduler.Phase.Sleeping;
        _btnStart.Enabled = !busy;
        _btnStop.Enabled = busy;
        // 思考模式是运行态状态机：仅 Running 可切换（唤醒会按启动参数重置基线，待机/过渡态点击无意义）
        _btnThinkOn.Enabled = _btnTurbo.Enabled = phase == SmartScheduler.Phase.Running;

        // 模块状态（右侧状态面板）：运行=绿 / 唤醒·休眠=橙过渡 / 待机=红停止
        bool running = phase == SmartScheduler.Phase.Running;
        _lblModuleState.Text = running ? "网关 运行中" : (phase == SmartScheduler.Phase.Standby ? "网关 已停止" : "网关 过渡中");
        _lblModuleState.BackColor = running ? UiTheme.C_Green : (phase == SmartScheduler.Phase.Standby ? UiTheme.C_Red : UiTheme.C_Warn);

        // 唤醒/运行/休眠期间禁用全部参数控件，防止运行中改参（清单在 BuildUi 一次构建）
        foreach (var c in _paramControls)
            c.Enabled = !busy;
        // 智能模式下监听器占用端口，改端口需重绑，监听中禁止编辑
        if (_config.AutoMode)
            _numPort.Enabled = false;

        // 服务阶段卡片（_lblStatus）：相位切换时设基础文本 + 颜色；调度器状态事件会覆盖为详细文本（"运行中 · N个在途任务…"）
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
            _ => Color.Gray, // Standby 待机
        };

        // 禁用按钮字体统一白色（用户要求：禁用态也保持白字，清晰）
        foreach (var b in new[] { _btnStart, _btnStop, _btnClearLog, _btnClearCache, _btnThinkOn, _btnTurbo, _btnExportCfg, _btnImportCfg })
            if (b != null) b.ForeColor = Color.White;

        // CheckBox 禁用时同步刷新为灰（启用时恢复黑）
        var checkColor = busy ? Color.FromArgb(0x88, 0x88, 0x88) : Color.Black;
        foreach (var c in new[] { _chkNoKv, _chkAuto, _chkForceStream, _chkTokenGuard, _chkContinuation, _chkCrashRecover, _chkAutoPreDshRule, _chkAutoPreWebui, _chkAutoPreTrae, _chkAutoPreDshAgent, _chkSnapDshRule, _chkSnapWebui, _chkSnapTrae, _chkSnapDshAgent, _chkNoCacheIdleSlots })
            if (c is CheckBox cb)
                cb.ForeColor = checkColor;
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
        SyncUiToConfig();
        if (!_config.Save(out string? err))
            AppendLog($"警告：配置保存失败：{err}");
        _scheduler.Dispose();
        LogFile.Shutdown(); // E-6：Flush + 关闭常驻日志写入器（防缓冲丢失）
    }

    // ==================== 日志 ====================

    /// <summary>追加一行日志（文件持久化 + UI 队列渲染由 LogView 承接）。</summary>
    private void AppendLog(string line) => _logView.Append(line);
}
