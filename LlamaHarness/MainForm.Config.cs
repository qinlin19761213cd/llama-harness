namespace LlamaHarness;

/// <summary>
/// MainForm 的配置同步部分（partial）：UI 控件 <-> AppConfig 双向映射 + 自动查找 exe。
/// 说明：配置映射强绑定 40+ 个参数控件（都声明于 MainForm.Ui.cs），故以 partial 文件组织
/// 而非独立类——避免为解耦而向外部暴露 40 个控件属性，收益小于成本。
/// </summary>
public partial class MainForm
{
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
}
