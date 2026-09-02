# 步骤3a：MainForm.cs —— 配置同步迁至 MainForm.Config.cs、日志渲染迁至 LogView.cs
$ErrorActionPreference = 'Stop'
$p = 'C:\project\lunch\LlamaHarness\MainForm.cs'
$c = [System.IO.File]::ReadAllText($p)
function To-CrLf([string]$s) { $s -replace "`r?`n", "`r`n" }
$fail = 0
function Apply([string]$name, [string]$old, [string]$new) {
    $script:c = $script:c.Replace((To-CrLf $old), (To-CrLf $new))
}
function Verify([string]$name, [string]$needle) {
    if (-not $script:c.Contains((To-CrLf $needle))) { Write-Host "[FAIL] 未找到: $name"; $script:fail++ } else { Write-Host "[OK] $name" }
}

# 1. 加 _logView 字段（在 _scheduler 声明后）
$old = @'
    private readonly AppConfig _config;
    private readonly SmartScheduler _scheduler;
'@
$new = @'
    private readonly AppConfig _config;
    private readonly SmartScheduler _scheduler;
    private readonly LogView _logView = new();
'@
Apply '加 _logView 字段' $old $new

# 2. OnShown 日志定时器启动 → _logView.Start()
$old = @'
        // 日志防抖定时器：批量消费队列，减少 RichTextBox 重绘闪烁。
        // 常驻运行（不 Stop/Start）：跨线程操作 WinForms Timer 会导致 SetTimer 绑定错误消息循环而永久停摆
        _logFlushTimer.Tick += OnLogFlush;
        _logFlushTimer.Start();
'@
$new = @'
        // 日志防抖定时器：批量消费队列，减少 RichTextBox 重绘闪烁（LogView 常驻运行）
        _logView.Start();
'@
Apply 'OnShown 日志定时器' $old $new

# 3. WireEvents 清空日志 → _logView.Clear()
$old = @'
        _btnClearLog.Click += (_, _) =>
        {
            lock (_logQueue) _logQueue.Clear(); // 清空队列，防止残留旧日志追加
            _txtLog.Clear();
        };
'@
$new = @'
        _btnClearLog.Click += (_, _) => _logView.Clear();
'@
Apply 'WireEvents 清空日志' $old $new

# 4. OnFormClosing 日志停表+flush → _logView.StopAndFlush()
$old = @'
        _logFlushTimer.Stop();
        // 刷出队列中剩余日志（避免最后几条丢失）
        bool hasPending;
        lock (_logQueue) hasPending = _logQueue.Count > 0;
        if (hasPending) OnLogFlush(null!, EventArgs.Empty);
'@
$new = @'
        _logView.StopAndFlush(); // 停表 + 刷出剩余日志（避免最后几条丢失）
'@
Apply 'OnFormClosing 日志收尾' $old $new

# 5. 删除配置同步区（已迁 MainForm.Config.cs）
$old = @'
    // ==================== 配置 <-> UI ====================

    private void LoadConfigToUi() => WriteConfigToUi(_config);

    /// <summary>把配置对象写入全部 UI 控件（启动时 / 载入配置文件共用）。</summary>
    private void WriteConfigToUi(AppConfig cfg)
    {
        _txtExe.Text = cfg.ExePath;
        _txtModel.Text = cfg.ModelPath;
        _numPort.Value = Math.Clamp(cfg.Port, (int)_numPort.Minimum, (int)_numPort.Maximum);
        _numCtx.Value = Math.Clamp(cfg.CtxSize, (int)_numCtx.Minimum, (int)_numCtx.Maximum);
        _numNgl.Value = Math.Clamp(cfg.Ngl, (int)_numNgl.Minimum, (int)_numNgl.Maximum);
        _numParallel.Value = Math.Clamp(cfg.Parallel, (int)_numParallel.Minimum, (int)_numParallel.Maximum);
        _chkNoKv.Checked = cfg.NoKvUnified;
        _numThreads.Value = Math.Clamp(cfg.Threads, (int)_numThreads.Minimum, (int)_numThreads.Maximum);
        _txtLoadMode.Text = cfg.LoadMode;
        _numUbatch.Value = Math.Clamp(cfg.UbatchSize, (int)_numUbatch.Minimum, (int)_numUbatch.Maximum);
        _numBatch.Value = Math.Clamp(cfg.BatchSize, (int)_numBatch.Minimum, (int)_numBatch.Maximum);
        _txtCacheTypeKv.Text = cfg.CacheTypeKv;
        _chkFlashAttn.Checked = cfg.FlashAttn;
        _txtSpecType.Text = cfg.SpecType;
        _numSpecDraftNMax.Value = Math.Clamp(cfg.SpecDraftNMax, (int)_numSpecDraftNMax.Minimum, (int)_numSpecDraftNMax.Maximum);
        _chkRequestDump.Checked = cfg.RequestDumpEnabled;
        _cmbLogQueuePolicy.SelectedIndex = cfg.LogQueueFullPolicy == QueueFullPolicy.DropOldest ? 1 : 0;
        _numBatchThreads.Value = Math.Clamp(cfg.BatchThreads, (int)_numBatchThreads.Minimum, (int)_numBatchThreads.Maximum);
        _txtExtra.Text = cfg.ExtraArgs;
        _chkAuto.Checked = cfg.AutoMode;
        _numIdleMin.Value = Math.Clamp(cfg.IdleMinutes, (int)_numIdleMin.Minimum, (int)_numIdleMin.Maximum);
        _txtPcoreMask.Text = cfg.PCoreMask;
        _chkForceStream.Checked = cfg.ForceStream;
        _txtKvCachePath.Text = cfg.KvCachePath;
        _chkTokenGuard.Checked = cfg.TokenGuardEnabled;
        _numReservedTokens.Value = Math.Clamp(cfg.ReservedOutputTokens, (int)_numReservedTokens.Minimum, (int)_numReservedTokens.Maximum);
        _numPromptOverhead.Value = Math.Clamp(cfg.ReservedPromptOverhead, (int)_numPromptOverhead.Minimum, (int)_numPromptOverhead.Maximum);
        _numCacheRam.Value = Math.Clamp(cfg.CacheRamMiB, (int)_numCacheRam.Minimum, (int)_numCacheRam.Maximum);
        _chkNoCacheIdleSlots.Checked = cfg.NoCacheIdleSlots;
        _chkContinuation.Checked = cfg.ContinuationEnabled;
        _numMaxContinuations.Value = Math.Clamp(cfg.MaxContinuations, (int)_numMaxContinuations.Minimum, (int)_numMaxContinuations.Maximum);
        _numContTimeout.Value = Math.Clamp(cfg.ContinuationTimeoutSeconds, (int)_numContTimeout.Minimum, (int)_numContTimeout.Maximum);
        _chkCrashRecover.Checked = cfg.CrashRecoveryEnabled;
        _numMaxRestarts.Value = Math.Clamp(cfg.MaxAutoRestarts, (int)_numMaxRestarts.Minimum, (int)_numMaxRestarts.Maximum);
        var autoPreSet = new HashSet<string>(cfg.AutoPreemptiveApps.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
        _chkAutoPreDshRule.Checked = autoPreSet.Contains("dsh_rule");
        _chkAutoPreWebui.Checked = autoPreSet.Contains("webui");
        _chkAutoPreTrae.Checked = autoPreSet.Contains("trae_global");
        _chkAutoPreDshAgent.Checked = autoPreSet.Contains("dsh_agent_global");
        var snapSet = new HashSet<string>(cfg.AutoSnapshotKeys.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
        _chkSnapDshRule.Checked = snapSet.Contains("dsh_rule");
        _chkSnapWebui.Checked = snapSet.Contains("webui");
        _chkSnapTrae.Checked = snapSet.Contains("trae_global");
        _chkSnapDshAgent.Checked = snapSet.Contains("dsh_agent_global");
    }

    /// <summary>智能模式下监听器占用前端端口，改端口需重绑，监听中禁止编辑。</summary>
    private void UpdatePortControlState() => _numPort.Enabled = !_config.AutoMode;

    /// <summary>UI → 共享配置对象（内存同步；持久化时机：唤醒成功 / 模式切换 / 关闭）。</summary>
    private void SyncUiToConfig()
    {
        _config.ExePath = _txtExe.Text.Trim();
        _config.ModelPath = _txtModel.Text.Trim();
        _config.Port = (int)_numPort.Value;
        _config.CtxSize = (int)_numCtx.Value;
        _config.Ngl = (int)_numNgl.Value;
        _config.Parallel = (int)_numParallel.Value;
        _config.NoKvUnified = _chkNoKv.Checked;
        _config.Threads = (int)_numThreads.Value;
        _config.LoadMode = _txtLoadMode.Text.Trim();
        _config.UbatchSize = (int)_numUbatch.Value;
        _config.BatchSize = (int)_numBatch.Value;
        _config.CacheTypeKv = _txtCacheTypeKv.Text.Trim();
        _config.FlashAttn = _chkFlashAttn.Checked;
        _config.SpecType = _txtSpecType.Text.Trim();
        _config.SpecDraftNMax = (int)_numSpecDraftNMax.Value;
        _config.RequestDumpEnabled = _chkRequestDump.Checked;
        var logPolicy = _cmbLogQueuePolicy.SelectedIndex == 1 ? QueueFullPolicy.DropOldest : QueueFullPolicy.DropNewest;
        _config.LogQueueFullPolicy = logPolicy;
        LogFile.Configure(logPolicy); // 运行时立即生效
        _config.BatchThreads = (int)_numBatchThreads.Value;
        _config.ExtraArgs = _txtExtra.Text.Trim();
        _config.AutoMode = _chkAuto.Checked;
        _config.IdleMinutes = (int)_numIdleMin.Value;
        _config.PCoreMask = _txtPcoreMask.Text.Trim();
        _config.ForceStream = _chkForceStream.Checked;
        _config.KvCachePath = _txtKvCachePath.Text.Trim();
        _config.TokenGuardEnabled = _chkTokenGuard.Checked;
        _config.ReservedOutputTokens = (int)_numReservedTokens.Value;
        _config.ReservedPromptOverhead = (int)_numPromptOverhead.Value;
        _config.CacheRamMiB = (int)_numCacheRam.Value;
        _config.NoCacheIdleSlots = _chkNoCacheIdleSlots.Checked;
        _config.ContinuationEnabled = _chkContinuation.Checked;
        _config.MaxContinuations = (int)_numMaxContinuations.Value;
        _config.ContinuationTimeoutSeconds = (int)_numContTimeout.Value;
        _config.CrashRecoveryEnabled = _chkCrashRecover.Checked;
        _config.MaxAutoRestarts = (int)_numMaxRestarts.Value;
        var autoPrePrefixes = new List<string>();
        if (_chkAutoPreDshRule.Checked) autoPrePrefixes.Add("dsh_rule");
        if (_chkAutoPreWebui.Checked) autoPrePrefixes.Add("webui");
        if (_chkAutoPreTrae.Checked) autoPrePrefixes.Add("trae_global");
        if (_chkAutoPreDshAgent.Checked) autoPrePrefixes.Add("dsh_agent_global");
        _config.AutoPreemptiveApps = string.Join(",", autoPrePrefixes);
        var snapPrefixes = new List<string>();
        if (_chkSnapDshRule.Checked) snapPrefixes.Add("dsh_rule");
        if (_chkSnapWebui.Checked) snapPrefixes.Add("webui");
        if (_chkSnapTrae.Checked) snapPrefixes.Add("trae_global");
        if (_chkSnapDshAgent.Checked) snapPrefixes.Add("dsh_agent_global");
        _config.AutoSnapshotKeys = string.Join(",", snapPrefixes);
    }

    /// <summary>自动查找 llama-server.exe：配置路径无效时用搜索结果回填。</summary>
    private void AutoFindExe()
    {
        var found = LlamaFinder.Find(_config.ExePath);
        if (found == null)
        {
            AppendLog("未找到 llama-server.exe，请通过「浏览…」手动指定路径。");
            return;
        }
        var current = _txtExe.Text.Trim();
        if (!File.Exists(current))
            _txtExe.Text = found;
    }

'@
Apply '删除配置同步区' $old ''

# 6. 日志区：MaxLogChars 常量删除 + AppendLog 改转发 + OnLogFlush 删除
$old = @'
    /// <summary>日志字符上限（约数万行）：防止长期运行无限增长拖慢 UI。</summary>
    private const int MaxLogChars = 400_000;

    /// <summary>追加一行带时间戳的日志并按级别着色（正常绿/警告黄/错误红），自动滚到底部。可来自任意线程。
    /// 防抖：日志先入队列，UI 定时器每 150ms 批量消费（一次 AppendText + 逐行着色），减少重绘闪烁。</summary>
    private void AppendLog(string line)
    {
        LogFile.Append(line); // 文件持久化 + 轮切 + 警告/错误独立输出
        var entry = $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}";
        lock (_logQueue) _logQueue.Enqueue((line, entry));
        // 注意：禁止在此（后台线程）Start/Stop 定时器——Win32 SetTimer 绑定调用线程的消息循环，
        // 跨线程 Start 会静默失败导致 UI 显示永久停摆。定时器常驻运行，OnLogFlush 空队列时直接返回。
    }

    /// <summary>批量消费日志队列：一次 AppendText + 逐行着色，大幅减少 RichTextBox 重绘次数。（UI 线程）</summary>
    private void OnLogFlush(object? sender, EventArgs e)
    {
        List<(string line, string entry)> batch;
        lock (_logQueue)
        {
            if (_logQueue.Count == 0) return; // 无新日志，直接返回（定时器常驻）
            batch = new List<(string line, string entry)>(_logQueue.Count);
            while (_logQueue.Count > 0) batch.Add(_logQueue.Dequeue());
        }

        try
        {
            // E-9：全部 entry 拼接后单次 AppendText（替代 N 次独立追加，减少布局触发/重绘）
            var all = string.Concat(batch.Select(b => b.entry));
            _txtLog.AppendText(all);

            // 字符上限截断
            if (_txtLog.TextLength > MaxLogChars)
            {
                _txtLog.SelectionStart = 0;
                _txtLog.SelectionLength = _txtLog.TextLength / 2;
                _txtLog.SelectedText = "";
            }

            // 逐行着色：从末尾往前累加 entry.Length 定位每行起点
            int pos = _txtLog.TextLength;
            for (int i = batch.Count - 1; i >= 0; i--)
            {
                var (line, entry) = batch[i];
                pos -= entry.Length;
                int start = Math.Max(0, pos);
                _txtLog.SelectionStart = start;
                _txtLog.SelectionLength = entry.Length;
                _txtLog.SelectionColor = LogFile.Classify(line) switch
                {
                    LogFile.Level.Warn => Color.Gold,
                    LogFile.Level.Error => Color.Red,
                    _ => Color.LightGreen,
                };
            }

            // 滚动到底部
            _txtLog.SelectionStart = _txtLog.TextLength;
            _txtLog.SelectionLength = 0;
            _txtLog.ScrollToCaret();
        }
        catch
        {
            // 显示层异常不得杀死日志管道（文件层已持久化），吞掉继续
        }
    }
'@
$new = @'
    /// <summary>追加一行日志（文件持久化 + UI 队列渲染由 LogView 承接）。</summary>
    private void AppendLog(string line) => _logView.Append(line);
'@
Apply '日志区改造' $old $new

if ($fail -gt 0) { Write-Host '存在未匹配项，中止写回'; exit 1 }
[System.IO.File]::WriteAllText($p, $c, [System.Text.UTF8Encoding]::new($false))
Write-Host 'MainForm.cs 改造完成'
