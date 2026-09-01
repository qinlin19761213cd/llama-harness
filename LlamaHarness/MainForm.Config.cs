namespace LlamaHarness;

/// <summary>
/// MainForm 的配置同步部分（partial）：UI 控件 <-> AppConfig 双向映射 + 自动查找 exe。
/// 说明：配置映射强绑定 40+ 个参数控件（都声明于 MainForm.Ui.cs），故以 partial 文件组织
/// 而非独立类——避免为解耦而向外部暴露 40 个控件属性，收益小于成本。
/// </summary>
public partial class MainForm
{
    private void LoadConfigToUi() => ApplyConfigToUi(_config);

    /// <summary>把配置对象写入全部 UI 控件（启动时 / 载入配置文件共用）。供 Presenter（配置导入）调用。</summary>
    internal void ApplyConfigToUi(AppConfig cfg)
    {
        _txtExe.Text = cfg.ExePath;
        _txtModel.Text = cfg.ModelPath;
        _numPort.Text = Math.Clamp(cfg.Port, NUM_PORT_MIN, NUM_PORT_MAX).ToString();
        _numCtx.Text = Math.Clamp(cfg.CtxSize, NUM_CTX_MIN, NUM_CTX_MAX).ToString();
        _numNgl.Text = Math.Clamp(cfg.Ngl, NUM_NGL_MIN, NUM_NGL_MAX).ToString();
        _numParallel.Text = Math.Clamp(cfg.Parallel, NUM_PARALLEL_MIN, NUM_PARALLEL_MAX).ToString();
        _chkNoKv.Checked = cfg.NoKvUnified;
        _numThreads.Text = Math.Clamp(cfg.Threads, NUM_THREADS_MIN, NUM_THREADS_MAX).ToString();
        _txtLoadMode.Text = cfg.LoadMode;
        _numUbatch.Text = Math.Clamp(cfg.UbatchSize, NUM_UBATCH_MIN, NUM_UBATCH_MAX).ToString();
        _numBatch.Text = Math.Clamp(cfg.BatchSize, NUM_BATCH_MIN, NUM_BATCH_MAX).ToString();
        _txtCacheTypeKv.Text = cfg.CacheTypeKv;
        _chkFlashAttn.Checked = cfg.FlashAttn;
        _txtSpecType.Text = cfg.SpecType;
        _numSpecDraftNMax.Text = Math.Clamp(cfg.SpecDraftNMax, NUM_DRAFT_MIN, NUM_DRAFT_MAX).ToString();
        _chkRequestDump.Checked = cfg.RequestDumpEnabled;
        _chkUnknownAutoBind.Checked = cfg.UnknownAppAutoBind; // v2.23.8 未知应用自动兜底
        _cmbLogQueuePolicy.SelectedIndex = cfg.LogQueueFullPolicy == QueueFullPolicy.DropOldest ? 1 : 0;
        _numBatchThreads.Text = Math.Clamp(cfg.BatchThreads, NUM_BTHREADS_MIN, NUM_BTHREADS_MAX).ToString();
        _txtExtra.Text = cfg.ExtraArgs;
        _chkAuto.Checked = cfg.AutoMode;
        _numIdleMin.Text = Math.Clamp(cfg.IdleMinutes, NUM_IDLE_MIN, NUM_IDLE_MAX).ToString();
        _txtPcoreMask.Text = cfg.PCoreMask;
        _chkForceStream.Checked = cfg.ForceStream;
        _txtKvCachePath.Text = cfg.KvCachePath;
        _chkTokenGuard.Checked = cfg.TokenGuardEnabled;
        _numReservedTokens.Text = Math.Clamp(cfg.ReservedOutputTokens, NUM_RESERVED_MIN, NUM_RESERVED_MAX).ToString();
        _numPromptOverhead.Text = Math.Clamp(cfg.ReservedPromptOverhead, NUM_OVERHEAD_MIN, NUM_OVERHEAD_MAX).ToString();
        _numCacheRam.Text = Math.Clamp(cfg.CacheRamMiB, NUM_CACHERAM_MIN, NUM_CACHERAM_MAX).ToString();
        _chkNoCacheIdleSlots.Checked = cfg.NoCacheIdleSlots;
        _chkContinuation.Checked = cfg.ContinuationEnabled;
        _numMaxContinuations.Text = Math.Clamp(cfg.MaxContinuations, NUM_CONT_MIN, NUM_CONT_MAX).ToString();
        _numContTimeout.Text = Math.Clamp(cfg.ContinuationTimeoutSeconds, NUM_CTIMEOUT_MIN, NUM_CTIMEOUT_MAX).ToString();
        _chkCrashRecover.Checked = cfg.CrashRecoveryEnabled;
        _numMaxRestarts.Text = Math.Clamp(cfg.MaxAutoRestarts, NUM_RESTART_MIN, NUM_RESTART_MAX).ToString();
        var autoPreSet = new HashSet<string>(cfg.AutoPreemptiveApps.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
        foreach (var c in _autoPreChecks) c.Checked = autoPreSet.Contains((string)c.Tag!);
        var snapSet = new HashSet<string>(cfg.AutoSnapshotKeys.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
        foreach (var c in _snapChecks) c.Checked = snapSet.Contains((string)c.Tag!);
    }

    /// <summary>智能模式下监听器占用前端端口，改端口需重绑，监听中禁止编辑。供 Presenter（自动模式切换）调用。</summary>
    internal void UpdatePortControlState() => _numPort.Enabled = !_config.AutoMode;

    /// <summary>UI → 共享配置对象（内存同步；持久化时机：唤醒成功 / 模式切换 / 关闭）。供 Presenter 与 OnFormClosing 调用。</summary>
    internal void SyncConfigFromUi()
    {
        _config.ExePath = _txtExe.Text.Trim();
        _config.ModelPath = _txtModel.Text.Trim();
        _config.Port = ReadInt(_numPort, NUM_PORT_MIN, NUM_PORT_MAX, _config.Port);
        _config.CtxSize = ReadInt(_numCtx, NUM_CTX_MIN, NUM_CTX_MAX, _config.CtxSize);
        _config.Ngl = ReadInt(_numNgl, NUM_NGL_MIN, NUM_NGL_MAX, _config.Ngl);
        _config.Parallel = ReadInt(_numParallel, NUM_PARALLEL_MIN, NUM_PARALLEL_MAX, _config.Parallel);
        _config.NoKvUnified = _chkNoKv.Checked;
        _config.Threads = ReadInt(_numThreads, NUM_THREADS_MIN, NUM_THREADS_MAX, _config.Threads);
        _config.LoadMode = _txtLoadMode.Text.Trim();
        _config.UbatchSize = ReadInt(_numUbatch, NUM_UBATCH_MIN, NUM_UBATCH_MAX, _config.UbatchSize);
        _config.BatchSize = ReadInt(_numBatch, NUM_BATCH_MIN, NUM_BATCH_MAX, _config.BatchSize);
        _config.CacheTypeKv = _txtCacheTypeKv.Text.Trim();
        _config.FlashAttn = _chkFlashAttn.Checked;
        _config.SpecType = _txtSpecType.Text.Trim();
        _config.SpecDraftNMax = ReadInt(_numSpecDraftNMax, NUM_DRAFT_MIN, NUM_DRAFT_MAX, _config.SpecDraftNMax);
        _config.RequestDumpEnabled = _chkRequestDump.Checked;
        _config.UnknownAppAutoBind = _chkUnknownAutoBind.Checked;
        var logPolicy = _cmbLogQueuePolicy.SelectedIndex == 1 ? QueueFullPolicy.DropOldest : QueueFullPolicy.DropNewest;
        _config.LogQueueFullPolicy = logPolicy;
        LogFile.Configure(logPolicy); // 运行时立即生效
        _config.BatchThreads = ReadInt(_numBatchThreads, NUM_BTHREADS_MIN, NUM_BTHREADS_MAX, _config.BatchThreads);
        _config.ExtraArgs = _txtExtra.Text.Trim();
        _config.AutoMode = _chkAuto.Checked;
        _config.IdleMinutes = ReadInt(_numIdleMin, NUM_IDLE_MIN, NUM_IDLE_MAX, _config.IdleMinutes);
        _config.PCoreMask = _txtPcoreMask.Text.Trim();
        _config.ForceStream = _chkForceStream.Checked;
        _config.KvCachePath = _txtKvCachePath.Text.Trim();
        _config.TokenGuardEnabled = _chkTokenGuard.Checked;
        _config.ReservedOutputTokens = ReadInt(_numReservedTokens, NUM_RESERVED_MIN, NUM_RESERVED_MAX, _config.ReservedOutputTokens);
        _config.ReservedPromptOverhead = ReadInt(_numPromptOverhead, NUM_OVERHEAD_MIN, NUM_OVERHEAD_MAX, _config.ReservedPromptOverhead);
        _config.CacheRamMiB = ReadInt(_numCacheRam, NUM_CACHERAM_MIN, NUM_CACHERAM_MAX, _config.CacheRamMiB);
        _config.NoCacheIdleSlots = _chkNoCacheIdleSlots.Checked;
        _config.ContinuationEnabled = _chkContinuation.Checked;
        _config.MaxContinuations = ReadInt(_numMaxContinuations, NUM_CONT_MIN, NUM_CONT_MAX, _config.MaxContinuations);
        _config.ContinuationTimeoutSeconds = ReadInt(_numContTimeout, NUM_CTIMEOUT_MIN, NUM_CTIMEOUT_MAX, _config.ContinuationTimeoutSeconds);
        _config.CrashRecoveryEnabled = _chkCrashRecover.Checked;
        _config.MaxAutoRestarts = ReadInt(_numMaxRestarts, NUM_RESTART_MIN, NUM_RESTART_MAX, _config.MaxAutoRestarts);
        _config.AutoPreemptiveApps = string.Join(",", _autoPreChecks.Where(c => c.Checked).Select(c => (string)c.Tag!));
        _config.AutoSnapshotKeys = string.Join(",", _snapChecks.Where(c => c.Checked).Select(c => (string)c.Tag!));
    }

    /// <summary>从数字 TextBox 安全读取整数值：TryParse + clamp 到 [min,max]，解析失败回落 fallback（保留原值，防非法输入破坏配置）。</summary>
    private static int ReadInt(TextBox tb, int min, int max, int fallback)
        => int.TryParse(tb.Text.Trim(), out var v) ? Math.Clamp(v, min, max) : fallback;

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
}
