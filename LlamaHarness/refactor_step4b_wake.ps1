# 步骤4b：WakeUpAsync 拆「参数校验」+「进程拉起」+「装配初始化」三段
$ErrorActionPreference = 'Stop'
$base = 'C:\project\lunch\LlamaHarness'

function Get-BraceEnd([string[]]$ls, [int]$decl) {
  $depth = 0; $started = $false
  for ($j=$decl; $j -lt $ls.Count; $j++) {
    $line = $ls[$j]; $i = 0; $n = $line.Length
    while ($i -lt $n) {
      $ch = $line[$i]
      if ($ch -eq '/' -and $i+1 -lt $n -and $line[$i+1] -eq '/') { break }
      if ($ch -eq '/' -and $i+1 -lt $n -and $line[$i+1] -eq '*') {
        $i += 2
        while ($i+1 -lt $n -and -not ($line[$i] -eq '*' -and $line[$i+1] -eq '/')) { $i++ }
        $i = [Math]::Min($i+2, $n); continue
      }
      if ($ch -eq "'") {
        $i++
        while ($i -lt $n) { if ($line[$i] -eq '\') { $i += 2; continue }; if ($line[$i] -eq "'") { $i++; break }; $i++ }
        continue
      }
      if ($ch -eq '@' -and $i+1 -lt $n -and $line[$i+1] -eq '"') {
        $i += 2
        while ($i -lt $n) { if ($line[$i] -eq '"' -and $i+1 -lt $n -and $line[$i+1] -eq '"') { $i += 2; continue }; if ($line[$i] -eq '"') { $i++; break }; $i++ }
        continue
      }
      if ($ch -eq '"') {
        $i++
        while ($i -lt $n) { if ($line[$i] -eq '\') { $i += 2; continue }; if ($line[$i] -eq '"') { $i++; break }; $i++ }
        continue
      }
      if ($ch -eq '{') { $depth++; $started = $true }
      elseif ($ch -eq '}') { $depth-- }
      $i++
    }
    if ($started -and $depth -le 0 -and $j -gt $decl) { return $j }
  }
  return -1
}

function Replace-Method([string]$path, [string]$methodName, [string]$newBlock) {
  $lines = [System.IO.File]::ReadAllLines($path)
  for ($i=0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match "^\s*(public|private|internal|protected)\s+(static\s+)?(async\s+)?.*?\b$([regex]::Escape($methodName))\s*\(") {
      $cs = $i
      while ($cs -gt 0 -and ($lines[$cs-1] -match '^\s*///' -or $lines[$cs-1] -match '^\s*//')) { $cs-- }
      $end = Get-BraceEnd $lines $i
      if ($end -lt 0) { throw "配平失败: $methodName" }
      $newLines = @()
      for ($k=0; $k -lt $lines.Count; $k++) {
        if ($k -ge $cs -and $k -le $end) { continue }
        $newLines += $lines[$k]
      }
      $out = @()
      for ($k=0; $k -lt $newLines.Count; $k++) {
        $out += $newLines[$k]
        if ($k -eq ($cs-1)) { $out += ($newBlock -split "`r`n") }
      }
      $content = ($out -join "`r`n")
      $content = [regex]::Replace($content, "(\r\n){3,}", "`r`n`r`n")
      [System.IO.File]::WriteAllText($path, $content, [System.Text.UTF8Encoding]::new($false))
      Write-Host "[替换] $(Split-Path $path -Leaf) : $methodName"
      return
    }
  }
  throw "未找到方法 $methodName in $path"
}

$wakeNew = @'
    /// <summary>
    /// 唤醒流程：校验 exe/模型 → 按黄金底参启动 llama-server（后端端口）→ 轮询就绪。
    /// 失败时清理刚拉起的进程，回到待机，异常抛给调用方。
    /// </summary>
    private async Task WakeUpAsync()
    {
        _nonStreamWarned = 0; // 新会话：非流式告警重新计数
        StatsReset?.Invoke();   // C-007：进入 Waking 即重置统计（llama-server task ID 从 0 重计），不再依赖 UI 调用
        SetPhase(Phase.Waking);
        RaiseStatus("唤醒中…（正在加载模型）");
        var wakeStart = DateTime.Now; // C-102：唤醒耗时计时
        try
        {
            // ① 参数校验 + 端口/线程解析（exe 存在性 / 模型文件 / 空闲端口 / P 核线程钳制 / --host 警告）
            var (srvPort, threads, exe, args) = ResolveLaunchParams();
            _backendPort = srvPort;

            // ② 进程拉起 + P 核绑定 + 思考模式基线
            LaunchBackendProcess(exe, args);

            // ③ 装配初始化：槽位亲和 + KV Cache 持久化 + 服务标记清空
            InitRuntimeAssemblies(srvPort);

            await WaitReadyAsync(srvPort);

            // ④ Warming 子状态：eager restore + dummy 预热，60s 超时兜底；期间到达的请求天然排队等待
            await RunWarmingPhaseAsync(srvPort);

            // ⑤ 就绪收尾：保活状态 + 唤醒统计 + 配置持久化
            Touch();
            SetPhase(Phase.Running);
            // C-102：唤醒统计埋点（累计次数 + 本次耗时）
            Interlocked.Increment(ref _wakeCount);
            var elapsed = (DateTime.Now - wakeStart).TotalSeconds;
            Log?.Invoke($"llama-server 就绪，进入保活状态。（唤醒 #{Volatile.Read(ref _wakeCount)}，本次耗时 {elapsed:F1}s）");
            // 唤醒成功：持久化当前参数
            if (!_cfg.Save(out string? saveErr))
                Log?.Invoke($"警告：配置持久化失败（{saveErr}），下次启动不会恢复本次参数。");
        }
        catch (Exception)
        {
            try { _server.Stop(); } catch { } // 清理失败时拉起的进程，防残留
            SetPhase(Phase.Standby);
            RaiseStatus($"唤醒失败，回到待机。");
            throw;
        }
        finally
        {
            lock (_wakeGate) { _wakeTask = null; }
        }
    }

    /// <summary>Warming 子状态（WakeUpAsync 子段④）：eager restore（autoPre key 有快照者）+ dummy 预热（max_tokens=1 直连后端）。
    /// 期间到达的请求经 EnsureRunningAsync await _wakeTask 天然排队等待（本方法未完成），无需额外机制；
    /// 整体 60s 超时兜底；任何失败不阻塞转 Running（首请求仍有惰性 restore 自愈路径）；
    /// 但预热期间进程死亡（如 dummy 请求触发 OOM 崩溃）中止唤醒走失败清理，不带死进程进 Running。</summary>
    private async Task RunWarmingPhaseAsync(int srvPort)
    {
        SetPhase(Phase.Warming);
        RaiseStatus("预热中…（restore KV + 捕获 decode graph）");
        try
        {
            using var warmCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await RunWarmingAsync(srvPort, warmCts.Token);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"警告：Warming 阶段异常（{ex.Message}），跳过并进入 Running。");
        }
        // 预热期间进程死亡（如 dummy 请求触发 OOM 崩溃）：中止唤醒走失败清理，不带死进程进 Running
        if (!_server.IsRunning)
            throw new InvalidOperationException("llama-server 在预热期间退出（疑似崩溃）。");
    }

    /// <summary>唤醒参数校验与解析（WakeUpAsync 子段①）：exe/模型存在性 → 空闲端口探测（智能模式）→
    /// P 核掩码线程钳制 → --host 警告 → 构造启动参数。返回（后端端口、钳制后线程数、exe、args）。</summary>
    private (int SrvPort, int Threads, string Exe, string Args) ResolveLaunchParams()
    {
        var exe = LlamaFinder.Find(_cfg.ExePath)
            ?? throw new InvalidOperationException("未找到 llama-server.exe，请先在界面指定路径。");
        if (string.IsNullOrWhiteSpace(_cfg.ModelPath) || !File.Exists(_cfg.ModelPath))
            throw new InvalidOperationException($"模型文件不存在：{_cfg.ModelPath}");

        // 智能模式下自动探测空闲后端端口，规避 Hyper-V/WSL2 动态端口保留导致的绑定失败
        int srvPort = AutoMode ? SchedulerUtils.PickFreePort(PreferredBackendPort) : _cfg.Port;

        // P 核掩码生效时线程数不得超过掩码绑定的核数，否则超订降速
        int threads = _cfg.Threads;
        var pcoreMask = CpuAffinity.ParseMask(_cfg.PCoreMask);
        if (pcoreMask != null)
        {
            int coreCount = System.Numerics.BitOperations.PopCount((ulong)pcoreMask.Value); // 掩码恒为正，转 ulong 安全
            if (threads > coreCount)
            {
                Log?.Invoke($"注意：线程数 {threads} 超出 P 核掩码的 {coreCount} 核，本次启动钳制为 {coreCount}（超订会降速）。建议调整线程数参数。");
                threads = coreCount;
            }
        }

        // --host 使后端监听非本机地址：绕过代理闲置休眠逻辑并把模型暴露到局域网
        if (_cfg.ExtraArgs.Contains("--host", StringComparison.OrdinalIgnoreCase))
            Log?.Invoke("警告：附加参数含 --host，后端可能监听非本机地址，将暴露到局域网并绕过闲置休眠。建议移除。");

        var args = LlamaFinder.BuildArgs(_cfg, srvPort, threads);
        Log?.Invoke($"唤醒 llama-server：{Path.GetFileName(exe)} {args}");
        return (srvPort, threads, exe, args);
    }

    /// <summary>进程拉起与基础装配（WakeUpAsync 子段②）：启动 llama-server → P 核掩码绑定 → 思考模式基线重置。</summary>
    private void LaunchBackendProcess(string exe, string args)
    {
        _server.Start(exe, args, Path.GetDirectoryName(Path.GetFullPath(exe))!);

        // 13900F 纯大核绑定：按配置掩码绑定 P 核（留空 = 禁用）
        string? affinityDesc = CpuAffinity.Apply(_server.Current, _cfg.PCoreMask);
        Log?.Invoke(affinityDesc != null ? $"P核绑定生效：{affinityDesc}" : "P核绑定已禁用（掩码为空或无效）。");

        // 思考模式基线：新服务进程按本次启动参数重置（运行态指令切换不跨会话携带）
        var baseLevel = ThinkingModeHelper.DetermineInitialThinkingMode(_cfg.ExtraArgs);
        lock (_thinkingGate) { _thinkingMode = baseLevel; }
        ThinkingModeChanged?.Invoke(baseLevel);
        Log?.Invoke($"思考模式基线：「{ThinkingModeHelper.LabelOf(baseLevel)}」（{(ThinkingModeHelper.EffortOf(baseLevel) is var be && be != null ? $"reasoning_effort={be}, " : "")}enable_thinking={(baseLevel == ThinkingLevel.Off ? "false" : "true")}）。");
    }

    /// <summary>运行时装配初始化（WakeUpAsync 子段③）：槽位亲和（含强占裁剪）→ KV Cache 持久化与 RestoreStats →
    /// 新进程槽位 KV 全空标记清空（唤醒后各 key 首次请求触发 restore 自愈，autoPre key 重新触发首请求存档）。</summary>
    private void InitRuntimeAssemblies(int srvPort)
    {
        // 槽位亲和：始终启用（单槽/多槽均激活），指纹绑定 + n_slots 路由
        _affinity = new SlotAffinity(_cfg.Parallel);
        // 启动时强制：裁剪超额强占到 ≤ slotCount-1（保"至少 1 槽给非强占新任务"不变量）
        var evictedPreemptive = _affinity.EnforcePreemptiveCap();
        if (evictedPreemptive.Count > 0)
            Log?.Invoke($"强占裁剪：{string.Join(", ", evictedPreemptive)} 取消强占（保 ≥1 槽给非强占任务）。");
        Log?.Invoke($"槽位亲和已启用：{_cfg.Parallel} 槽，指纹绑定 + n_slots 路由（绑定表 slot_bindings.json，LRU 驱逐）。");

        // KV Cache 持久化：KvCachePath 非空时启用（驱逐 save / 重绑定 restore / 休眠前 save / 唤醒后 restore）
        // ctxSize + log 回调：快照元数据 json（ctx_size 字段）+ [EDGE-CASE-SNAPSHOT-CORRUPT] 埋点
        _kvCache = !string.IsNullOrWhiteSpace(_cfg.KvCachePath)
            ? new KvCacheManager(_hc, _cfg.KvCachePath, _cfg.Parallel, srvPort, _cfg.CtxSize, s => Log?.Invoke(s))
            : null;
        // 3.1 Restore 命中率可观测：与 KV Cache 同生命周期（累计统计跨唤醒周期持久化于 config/restore_stats.json）
        _restoreStats = _kvCache != null ? new RestoreStats() : null;
        if (_kvCache != null)
            Log?.Invoke($"KV Cache 持久化已启用：路径 {_cfg.KvCachePath}（驱逐自动 save，重绑定自动 restore，休眠前自动 save，唤醒后自动 restore）。");

        // 新进程槽位 KV 全空：清空「本轮已服务」+「首请求存档」+「快照新鲜度」标记 → 唤醒后各 key 首次请求触发 restore 自愈（跳过全量 prefill），autoPre key 重新触发首请求存档
        lock (_kvStateGate) { _servedKeysThisRun.Clear(); _savedKeysThisRun.Clear(); _freshSnapshotKeys.Clear(); }
    }
'@
Replace-Method (Join-Path $base 'SmartScheduler.Lifecycle.cs') 'WakeUpAsync' $wakeNew
