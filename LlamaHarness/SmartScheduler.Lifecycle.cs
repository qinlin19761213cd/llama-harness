using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ThinkingModeHelper = LlamaHarness.ThinkingMode;

namespace LlamaHarness;

/// <summary>
/// 生命周期与状态机（EnsureRunningAsync/WakeUpAsync/WaitReadyAsync/RunWarmingAsync/闲置休眠/停止/释放/SetPhase/Dispose）。partial 聚类方法体零改动。
/// </summary>
public partial class SmartScheduler
{
    /// <summary>确保后端服务运行；未运行时排队等待唤醒任务。</summary>
    private async Task EnsureRunningAsync()
    {
        if (_server.IsRunning) return;
        Task t;
        lock (_wakeGate)
        {
            t = _wakeTask ??= WakeUpAsync();
        }
        await t;
    }

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
        _affinity = new SlotAffinity(_cfg.Parallel, rules: _cfg.AffinityRules);
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

    /// <summary>轮询后端 /v1/models 直至就绪（最长 5 分钟），期间进程退出立即报错。
    /// C-003：不仅校验 HTTP 200，还校验响应内容含 "object":"list" 模型列表特征——
    /// 防 TOCTOU 窗口内其他程序抢占后端端口时被误判为 llama-server 就绪。
    /// 每 15 秒输出一次进度（大模型加载可达数分钟），避免界面无反馈看似卡死。</summary>
    private async Task WaitReadyAsync(int srvPort)
    {
        var url = $"http://localhost:{srvPort}/v1/models";
        var deadline = DateTime.Now + TimeSpan.FromMinutes(5);
        var start = DateTime.Now;
        int nextProgressAtSec = 10; // 下次进度日志的累计秒数阈值
        while (DateTime.Now < deadline)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var r = await _hc.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (r.IsSuccessStatusCode)
                {
                    var body = await r.Content.ReadAsStringAsync(cts.Token);
                    // 内容特征校验：llama-server /v1/models 返回 {"object":"list",...}
                    if (body.Contains("\"object\":\"list\"")) return;
                }
            }
            catch
            {
                // 连接拒绝 / 超时：服务尚未就绪，继续轮询
            }
            if (!_server.IsRunning)
                throw new InvalidOperationException(
                    "llama-server 进程已退出，唤醒失败。\n最近输出：\n" + RecentOutput());

            // 进度反馈：大模型（数十 GB）加载耗时可达数分钟，期间静默等待易被误判为卡死
            int elapsedSec = (int)(DateTime.Now - start).TotalSeconds;
            if (elapsedSec >= nextProgressAtSec)
            {
                nextProgressAtSec = elapsedSec + 15;
                var lastLine = RecentOutput().Split('\n').LastOrDefault()?.Trim();
                Log?.Invoke($"等待 llama-server 就绪… {elapsedSec}s（正在加载模型/显存分配。最新输出：{(string.IsNullOrEmpty(lastLine) ? "无" : lastLine)}）");
            }
            await Task.Delay(2000);
        }
        throw new TimeoutException("等待 llama-server 就绪超时（5 分钟）。");
    }

    /// <summary>3.2 Warming 子状态主体：eager restore（autoPre key 有快照者）+ best-effort dummy 预热（max_tokens=1 直连后端端口，绕过代理管道）。
    /// 全部失败不抛（调用方另有兜底 catch）：eager restore 失败 → 首请求惰性 restore 自愈；dummy 预热失败 → 仅损失 CUDA graph 捕获收益。</summary>
    private async Task RunWarmingAsync(int srvPort, CancellationToken ct)
    {
        var kv = _kvCache;
        var aff = _affinity;
        if (aff == null) return; // 理论不可达（WakeUpAsync 在 Warming 前已赋值），防御性早退
        // eager restore：已绑定槽位且有磁盘快照的自动快照 key → 立即恢复 KV（成功记入 _servedKeysThisRun 防首请求重复 restore）
        if (kv != null)
        {
            foreach (var b in aff.Snapshot()) // (Key, App, Slot, LastActive, Preemptive, KvCache)
            {
                if (ct.IsCancellationRequested) break;
                if (!b.KvCache || !IsAutoSnapshotKey(b.Key) || !kv.HasCache(b.Key)) continue;
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var restoreTask = kv.RestoreAsync(b.Slot, b.Key);
                    var guard = Task.Delay(30_000, ct); // 单 key restore 30s 超时（RestoreAsync 内部不响应取消，WhenAny 兜底）
                    if (await Task.WhenAny(restoreTask, guard) != restoreTask)
                    {
                        Log?.Invoke($"Warming eager restore 超时：{b.Key} → slot{b.Slot}，首请求自愈。");
                        continue;
                    }
                    bool ok = await restoreTask;
                    if (ok)
                    {
                        lock (_kvStateGate) _servedKeysThisRun.Add(b.Key);
                        Log?.Invoke($"[KV-RESTORE] Warming eager restore：{b.Key} → slot{b.Slot}（{sw.Elapsed.TotalSeconds:F1}s）");
                    }
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"Warming eager restore 失败：{b.Key} → slot{b.Slot}（{ex.Message}），首请求自愈。");
                }
            }
        }

        // best-effort dummy 预热：选一个未绑定 KV 快照的槽位发 max_tokens=1 请求，捕获 CUDA graph / 预热 decode 路径。
        // 只碰无快照槽位：避免污染已 eager restore 的槽位 KV（新进程内存 KV 全空，无需 erase）。
        var kvBoundSlots = aff.Snapshot().Where(x => x.KvCache).Select(x => x.Slot);
        int warmSlot = SchedulerUtils.PickWarmSlot(_cfg.Parallel, kvBoundSlots);
        if (warmSlot < 0)
        {
            Log?.Invoke("Warming dummy 预热跳过：全部槽位均绑定 KV 快照 key（防污染已恢复 KV）。");
            return;
        }
        try
        {
            var body = $"{{\"model\":\"local_model\",\"messages\":[{{\"role\":\"user\",\"content\":\"warm\"}}],\"max_tokens\":1,\"stream\":false,\"n_slots\":[{warmSlot}]}}";
            using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var resp = await _hc.PostAsync(new Uri($"http://localhost:{srvPort}/v1/chat/completions"), content, ct);
            Log?.Invoke($"Warming dummy 预热：HTTP {(int)resp.StatusCode}（slot{warmSlot}，decode graph 捕获）");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"警告：Warming dummy 预热失败（{ex.Message}），不影响后续请求。");
        }
    }

    private void OnTick(object? _)
    {
        if (CurrentPhase != Phase.Running) return;
        int inflight = Volatile.Read(ref _inflight);
        var remaining = new DateTime(Interlocked.Read(ref _lastTouchTicks)).Add(TimeSpan.FromMinutes(IdleMinutes)) - DateTime.Now;
        if (remaining <= TimeSpan.Zero && inflight == 0)
            SleepNow();
        else if (inflight > 0)
            // 有在途任务时不触发休眠，明确提示原因（长驻 SSE 流式连接会一直压制休眠）
            RaiseStatus($"运行中 · {inflight} 个在途任务，休眠暂停");
        else
            RaiseStatus($"运行中 · {(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2} 无请求后自动休眠");

        // P 核亲和性自愈：每 5 秒检查一次，被系统重置时自动重绑
        if (++_tickCount % AffinityHealEveryTicks == 0 && CpuAffinity.Heal(_server.Current, _cfg.PCoreMask))
            Log?.Invoke("检测到 CPU 亲和性被重置，已重新绑定 P 核。");
    }

    /// <summary>
    /// 安全停机入口：闲置超时且无在途任务时触发。启动后台休眠流程（防重复）：
    /// 10 秒静默观察期（新请求/在途任务即取消）→ 逐槽 save KV 快照 → Kill 整个进程树，杜绝残留。
    /// </summary>
    private void SleepNow()
    {
        lock (_sleepGate)
        {
            if (CurrentPhase != Phase.Running || _sleepPreparing) return;
            _sleepPreparing = true;
        }
        _ = SleepNowCoreAsync();
    }

    private async Task SleepNowCoreAsync()
    {
        try
        {
            var touchAtEntry = Interlocked.Read(ref _lastTouchTicks);
            RaiseStatus($"闲置超时，{SleepGraceSeconds} 秒后休眠（期间保存 KV 缓存）…");
            // 静默观察期：期间任何新请求（Touch 刷新基准点）或在途任务都取消本次休眠
            for (int i = 0; i < SleepGraceSeconds; i++)
            {
                await Task.Delay(1000).ConfigureAwait(false);
                if (Volatile.Read(ref _inflight) > 0 || Interlocked.Read(ref _lastTouchTicks) != touchAtEntry)
                {
                    Log?.Invoke("休眠取消：观察期内有新请求或在途任务。");
                    RaiseStatus("运行中 · 休眠取消（有新活动）");
                    return;
                }
            }
            lock (_sleepGate)
            {
                if (CurrentPhase != Phase.Running) return; // 观察期内被手动停止等：放弃休眠
                SetPhase(Phase.Sleeping);
            }
            await SaveAllSlotsBeforeStopAsync().ConfigureAwait(false);
            Interlocked.Increment(ref _sleepCount); // C-102：休眠计数
            Log?.Invoke($"{IdleMinutes} 分钟无请求，自动休眠（累计 #{Volatile.Read(ref _sleepCount)}，inflight 峰值 {Volatile.Read(ref _inflightPeak)}），正在释放显存…");
            RaiseStatus("闲置超时，正在释放显存…");
            _server.Stop(); // Exited 事件将把状态拉回 Standby
        }
        finally
        {
            lock (_sleepGate) { _sleepPreparing = false; }
        }
    }

    /// <summary>
    /// 休眠前逐槽保存 KV 快照（仅 KvCache=true 的绑定）：进程即将终止，槽位内存 KV 仅此一次落盘机会；
    /// 唤醒后各 key 首次请求将 restore 快照跳过全量 prefill。整体 60s 超时保底（后端卡死不阻塞休眠）。
    /// </summary>
    private async Task SaveAllSlotsBeforeStopAsync()
    {
        var aff = _affinity;
        var kv = _kvCache;
        if (aff == null || kv == null) return; // --slots 未启用：无快照能力，直接休眠
        // O-13：60s CTS——超时后主动取消孤儿 save 任务（原实现 WaitAsync 只停止等待，任务仍在后台运行）
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var saveAll = Task.Run(async () =>
        {
            foreach (var b in aff.Snapshot()) // (Key, App, Slot, LastActive, Preemptive, KvCache)
            {
                if (!b.KvCache)
                {
                    EmitSlot($"休眠前跳过 save：{b.Key}（KV Cache 已关闭）");
                    continue;
                }
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    await kv.SaveAsync(b.Slot, b.Key, cts.Token).ConfigureAwait(false);
                    EmitSlot($"[KV-SAVE] 休眠前快照：{b.Key} → slot{b.Slot}（{sw.Elapsed.TotalSeconds:F1}s）");
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    return; // O-13：超时取消，放弃剩余槽位
                }
                catch (Exception ex)
                {
                    EmitSlot($"休眠前 KV 保存失败：{b.Key}（{ex.Message}），该槽位 KV 将丢失，唤醒后全量 prefill。");
                }
            }
        }, cts.Token);
        try
        {
            await saveAll.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log?.Invoke("休眠前 KV 保存超时（60s），放弃剩余快照，继续休眠。");
            cts.Cancel(); // CTS(60s) 自动取消与 WaitAsync 计时存在竞态：此处确保孤儿任务被取消
        }
        // 3.1：进程即将终止，显式落盘 RestoreStats 累计统计（节流自动保存在休眠后无机会执行）
        _restoreStats?.Save();
    }

    /// <summary>进程退出回调：休眠/运行态退出 → 回到监听待机；唤醒态由唤醒任务自行处理。</summary>
    private void OnServerExited(int code)
    {
        var p = CurrentPhase;
        if (p == Phase.Sleeping || p == Phase.Running)
        {
            bool wasSleep = p == Phase.Sleeping;
            SetPhase(Phase.Standby);
            Log?.Invoke($"llama-server 已退出（退出码 {code}），显存已释放，回到监听待机。");
            RaiseStatus(AutoMode ? "已休眠，继续监听待机。" : "已停止。");
            if (wasSleep) _ = VerifyVramReleasedAsync(); // C-006：休眠后校验显存是否真正回落
        }
    }

    /// <summary>C-006：休眠 Kill 进程树后延迟读显存；未回落到待机水平则告警（衍生子进程孤儿残留）。</summary>
    private async Task VerifyVramReleasedAsync()
    {
        await Task.Delay(3000); // 等 GPU 驱动回收显存稳定
        var mb = await SystemMetrics.GetVramUsedMbAsync();
        if (mb is > VramAlertThresholdMb)
            Log?.Invoke($"警告：休眠后显存占用仍为 {mb} MB（预期接近 0），疑似 llama-server 衍生子进程残留，请在任务管理器中检查。");
    }

    /// <summary>停止按钮 / 关闭前：终止进程树。</summary>
    public void StopNow()
    {
        Log?.Invoke("正在停止 llama-server…");
        SetPhase(Phase.Standby); // 先置位，Exited 回调不再重复报告
        RaiseStatus(AutoMode ? "已停止，监听待机中。" : "已停止。");
        _server.Stop();
    }

    public void Dispose()
    {
        StopListening();
        SetPhase(Phase.Standby);
        try { _server.Stop(); } catch { }
        _tickTimer.Dispose();
        _hc.Dispose();
        _server.Dispose();
    }

    private void SetPhase(Phase p)
    {
        if (CurrentPhase == p) return;
        Volatile.Write(ref _phase, (int)p);
        PhaseChanged?.Invoke(p);
    }
}
