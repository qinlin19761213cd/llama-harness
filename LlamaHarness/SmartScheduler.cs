using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ThinkingModeHelper = LlamaHarness.ThinkingMode;

namespace LlamaHarness;

/// <summary>
/// 智能按需调度器（监听优先、按需启动、闲置释放）：
/// - 待机态：仅用轻量 HttpListener 占用前端端口（默认 8080），零显存占用
/// - 首个请求触发唤醒：拉起 llama-server（后端端口 = 前端端口 + 1），等待就绪后代理转发，用户无感知
/// - 保活态：每次请求刷新闲置计时；连续 N 分钟（默认 15）无请求且无在途任务 → 自动 Kill 进程树释放显存
/// - 休眠后自动回到监听待机，循环待命
/// 并发请求在唤醒期间共享同一个唤醒任务排队等待，避免重复拉起多个进程。
/// </summary>
public sealed class SmartScheduler : IDisposable
{
    /// <summary>调度器状态机（Warming：就绪后、Running 前的预热子状态——eager restore + dummy 预热，期间请求排队等待唤醒完成）</summary>
    public enum Phase { Standby, Waking, Warming, Running, Sleeping }

    private readonly AppConfig _cfg;
    private readonly LlamaServerProcess _server = new();
    // 代理用 HttpClient：推理请求可能很长，禁用客户端超时。
    // E-7：keep-alive + 池化连接寿命上限（替代 Connection: close）：
    // 休眠/唤醒后残留的死连接由 PooledConnectionLifetime 自然过期淘汰；
    // 偶发死连接命中时由 SendAndPipeAsync 的 500ms 重试兜底。
    private readonly HttpClient _hc = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromSeconds(30),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
    })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };
    private readonly HttpListener _listener = new();
    private readonly System.Threading.Timer _tickTimer;
    private readonly object _wakeGate = new();
    private readonly object _sleepGate = new();

    private Task? _wakeTask;
    private int _inflight;                       // 在途请求计数（含排队等待唤醒的）
    private long _lastTouchTicks = DateTime.Now.Ticks; // 闲置计时基准（Interlocked 保护，跨线程读写）
    private int _phase;                          // Phase 索引，统一经 Volatile.Read/Write 访问
    private volatile int _backendPort;           // 实际运行时后端端口（自动探测空闲）
    private int _tickCount;                      // 秒级 tick 计数（定时器周期 1s），用于周期性自愈检查
    private readonly System.Collections.Generic.Queue<string> _recentOutput = new(); // 进程输出末几行，用于失败诊断

    /// <summary>P 核亲和性自愈检查间隔（tick 数，定时器周期 1s）：每 5 秒核对一次绑定是否被系统重置。</summary>
    private const int AffinityHealEveryTicks = 5;

    // —— 审计 O-11：魔法数字提常量（原散落各处的裸数值） ——
    /// <summary>休眠静默观察期时长（秒）：期间新请求/在途任务取消休眠。</summary>
    private const int SleepGraceSeconds = 10;
    /// <summary>request_dump.log 请求体截断长度（字符），防大请求撑爆磁盘。</summary>
    private const int DumpBodyMaxLength = 2000;
    /// <summary>休眠后显存告警阈值（MB）：高于此值疑似子进程残留。</summary>
    private const int VramAlertThresholdMb = 1024;
    /// <summary>崩溃恢复内存余量阈值（GB）：空闲 RAM 低于此值时重放预算收紧。</summary>
    private const double TightMemoryFreeGb = 4.0;
    /// <summary>崩溃恢复预算收紧系数（严格预算 = 基础预算 × 此系数）。</summary>
    private const double TightBudgetFactor = 0.75;
    /// <summary>bad_alloc 日志佐证窗口（秒）：该窗口内出现过 bad_alloc 关键字才认可 5xx 响应体判定。</summary>
    private static readonly TimeSpan BadAllocEvidenceWindow = TimeSpan.FromSeconds(60);

    /// <summary>日志行（可能来自任意线程），UI 侧负责 BeginInvoke</summary>
    public event Action<string>? Log;
    /// <summary>状态栏文本变更（可能来自任意线程），UI 侧负责 BeginInvoke</summary>
    public event Action<string>? StatusChanged;
    /// <summary>阶段切换（可能来自任意线程）</summary>
    public event Action<Phase>? PhaseChanged;
    /// <summary>C-007：进入 Waking 阶段时触发，UI 据此重置统计解析器（职责下沉到调度器内部，不再依赖 UI 自行监听 PhaseChanged）。</summary>
    public event Action? StatsReset;
    /// <summary>思考模式状态变更（可能来自任意线程），UI 侧负责 BeginInvoke。参数为当前档位。</summary>
    public event Action<ThinkingLevel>? ThinkingModeChanged;
    /// <summary>槽位绑定变更（新绑定/驱逐），UI 侧刷新槽位表格。</summary>
    public event Action? SlotBindingChanged;
    /// <summary>槽位相关日志（绑定/驱逐/KV Cache 保存恢复，可能来自任意线程）：UI 显示于槽位页 + 持久化 slot.log。</summary>
    public event Action<string>? SlotLog;

    /// <summary>槽位事件双写：主日志（UI 显示 + harness.log）+ slot.log / 槽位页（审计 O-10：收敛此前 10+ 处成对 Invoke 样板）。</summary>
    private void EmitSlot(string msg)
    {
        Log?.Invoke(msg);
        SlotLog?.Invoke(msg);
    }

    // C-102 运行统计埋点
    private int _wakeCount, _sleepCount, _inflightPeak;

    /// <summary>思考模式三档状态机（lock 保护，多 agent 并发安全）。默认 Off = 极速模式（65+ t/s）。</summary>
    private ThinkingLevel _thinkingMode = ThinkingLevel.Off;
    private readonly object _thinkingGate = new();

    /// <summary>当前思考模式档位（线程安全读取）。</summary>
    public ThinkingLevel ThinkingMode { get { lock (_thinkingGate) return _thinkingMode; } }

    /// <summary>程序化设置思考模式档位（UI 按钮调用）：线程安全，触发 ThinkingModeChanged + 日志。
    /// 与聊天指令切换同属运行态开关——不跨会话携带，唤醒时按启动参数重置基线。</summary>
    public void SetThinkingMode(ThinkingLevel level)
    {
        lock (_thinkingGate) { _thinkingMode = level; }
        Log?.Invoke($"思考模式已切换为「{ThinkingModeHelper.LabelOf(level)}」（{(ThinkingModeHelper.EffortOf(level) is var e && e != null ? $"reasoning_effort={e}, " : "")}enable_thinking={(level == ThinkingLevel.Off ? "false" : "true")}）。");
        ThinkingModeChanged?.Invoke(level);
    }

    /// <summary>多槽亲和绑定管理器（--parallel &gt; 1 时创建；null = 单槽不路由）。</summary>
    private volatile SlotAffinity? _affinity;

    /// <summary>KV Cache 管理器（--parallel &gt; 1 且 KvCachePath 非空时创建；null = 禁用）。</summary>
    private volatile KvCacheManager? _kvCache;

    /// <summary>Restore 命中率可观测（3.1）：与 KV Cache 同生命周期启用；null = 禁用。</summary>
    private volatile RestoreStats? _restoreStats;

    // ==================== KV 全场景复用状态（§4.1/§4.5/§8，多 agent 并发请求共享）====================

    /// <summary>KV 复用状态统一门控（_truncPending / _toolLockedKeys / _prefixHashes 共用）。</summary>
    private readonly object _kvStateGate = new();
    /// <summary>截断待续接标记（§4.1）：已 save 断点快照且续接中的 key。续接成功 → 清理过期快照；失败 → 保留供 restore。</summary>
    private readonly HashSet<string> _truncPending = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Tool 链锁定集合（§4.5）：本层执行过 SetPreemptive(true) 的 key。只解锁集合内的键，不碰用户手动/自动强占。</summary>
    private readonly HashSet<string> _toolLockedKeys = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>前缀哈希表（§8 可观测）：key → SHA256(最新一轮之前的全部 messages JSON)。比对判定原生 KV 前缀复用 HIT/MISS。</summary>
    private readonly Dictionary<string, string> _prefixHashes = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>本进程运行以来已服务过的亲和 key（唤醒时清空）：「进程重启后该 key 首次使用 → restore KV 自愈」判定依据，
    /// 防止进程存活期间误用磁盘旧快照回退内存中更新的槽位状态。</summary>
    private readonly HashSet<string> _servedKeysThisRun = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>本唤醒周期内已完成「首请求存档」的 autoPre key（唤醒时清空）：
    /// autoPre key 首次真实 prefill 完成后立即落盘快照（1.1 修复），防进程崩溃未休眠时磁盘快照停留在旧状态。
    /// 每周期只存一次；后续增量 KV 仍由休眠前 save 兜底最终态。</summary>
    private readonly HashSet<string> _savedKeysThisRun = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>快照新鲜度标记（唤醒时清空）：key 的 RAMDisk 快照已覆盖到最近一轮任务完成时刻。
    /// 条件式 save（RAMDisk 快照全权接管）：每轮任务完成后，非新鲜的 autoSnapshot key 触发后台异步 save（不阻塞响应）；
    /// save 成功/restore 命中/同步存档 → 标记新鲜；save 失败 → DeleteCache 废弃 + [EDGE-CASE-SAVE-FAILED]。</summary>
    private readonly HashSet<string> _freshSnapshotKeys = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>休眠静默观察期进行中标志（_sleepGate 保护）：防闲置定时器重复触发休眠流程。</summary>
    private bool _sleepPreparing;

    /// <summary>思考模式档位：Off=极速（不注入）/ Low / Medium / XHigh。Low~XHigh 均携带 enable_thinking=true。</summary>
    public enum ThinkingLevel { Off, Low, Medium, XHigh }

    public bool AutoMode { get; set; } = true;
    public int IdleMinutes { get; set; } = 15;

    public Phase CurrentPhase => (Phase)Volatile.Read(ref _phase);

    /// <summary>获取槽位绑定快照（UI 表格刷新用，含应用名/强占/KV缓存配置）。null = 未启用多槽。</summary>
    public List<(string Key, string App, int Slot, DateTime LastActive, bool Preemptive, bool KvCache)>? GetSlotBindings()
    {
        var aff = _affinity;
        return aff?.Snapshot();
    }

    /// <summary>设置指定绑定的强占模式（UI 槽位管理页调用）。</summary>
    public void SetSlotPreemptive(string key, bool value) => _affinity?.SetPreemptive(key, value);

    /// <summary>设置指定绑定的 KV Cache 开关（UI 槽位管理页调用）。</summary>
    public void SetSlotKvCache(string key, bool value) => _affinity?.SetKvCache(key, value);

    /// <summary>获取 KV Cache 管理器（UI 清空缓存用）。null = 未启用。</summary>
    public KvCacheManager? GetKvCache() => _kvCache;

    /// <summary>获取 Restore 命中率统计（UI「Restore 命中率」卡片用）。null = 未启用。</summary>
    public RestoreStats? GetRestoreStats() => _restoreStats;

    public SmartScheduler(AppConfig cfg)
    {
        _cfg = cfg;
        _server.OutputLine += OnServerOutput;
        _server.Exited += (_, code) => OnServerExited(code);
        _tickTimer = new System.Threading.Timer(OnTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>首选后端端口 = 前端端口 + 1；若被占用则向上探测空闲端口。</summary>
    private int PreferredBackendPort => Math.Min(_cfg.Port + 1, 65535);

    private void OnServerOutput(string line)
    {
        Log?.Invoke(line);
        // bad_alloc 检测：llama.cpp 任务级 OOM（"got exception: bad allocation"）→ 记录观测事件供流中断佐证
        CrashRecovery.OnBackendLine(line);
        if (line.Contains("bad allocation", StringComparison.OrdinalIgnoreCase))
            Log?.Invoke($"⚠ 检测到后端 bad_alloc（任务级内存耗尽），已记录崩溃事件。");
        // 3.1 Restore 命中率判定：prompt eval tokens 为唯一真值源（mini 状态机，FIFO 归属 + TTL 防错位）
        var rs = _restoreStats;
        if (rs != null && line.Contains("prompt eval time", StringComparison.Ordinal)
            && RestoreStats.TryParsePromptEvalTokens(line, out int nEval))
        {
            var r = rs.OnPromptEval(nEval);
            if (r != null)
            {
                EmitSlot($"[KV-RESTORE-JUDGE] key={r.Key} hit={(r.Hit ? 1 : 0)} reason={r.Reason} prompt_eval={r.PromptEvalTokens} saved_n={r.SavedN} wrapper_hit={(r.WrapperHit ? 1 : 0)} false_miss={(r.FalseMiss ? 1 : 0)} false_hit={(r.FalseHit ? 1 : 0)}");
                if (r.Alert != RestoreStats.AlertLevel.None)
                {
                    var msg = $"Restore 命中率告警：总命中率 {(r.HitRate * 100):F1}%（{(r.Alert == RestoreStats.AlertLevel.Red ? "红色" : "黄色")}）";
                    // 含「警告/错误」字样 → LogFile.Append 自动入 warn_error.log
                    Log?.Invoke((r.Alert == RestoreStats.AlertLevel.Red ? "错误：" : "警告：") + msg);
                    EmitSlot(msg);
                }
            }
        }
        lock (_recentOutput)
        {
            _recentOutput.Enqueue(line);
            while (_recentOutput.Count > 3) _recentOutput.Dequeue();
        }
    }

    private string RecentOutput()
    {
        lock (_recentOutput)
        {
            return string.Join(Environment.NewLine, _recentOutput);
        }
    }

    /// <summary>初始化：启动闲置计时；智能模式下开始监听前端端口。</summary>
    public void Initialize()
    {
        _tickTimer.Change(1000, 1000);
        if (AutoMode)
        {
            StartListening();
            RaiseStatus($"待机 · 监听 {_cfg.Port}，等待请求唤醒。");
        }
        else
        {
            RaiseStatus("手动模式：点击「启动 / 唤醒」运行 llama-server。");
        }
    }

    // ==================== 监听（代理入口） ====================

    private void StartListening()
    {
        if (_listener.IsListening) return;
        try
        {
            // 仅绑定本机回环，无需管理员权限
            _listener.Prefixes.Add($"http://localhost:{_cfg.Port}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{_cfg.Port}/");
            _listener.Start();
            Log?.Invoke($"智能模式：已接管端口 {_cfg.Port}（llama-server 唤醒时将自动选择空闲后端端口，首选 {PreferredBackendPort}），当前显存占用为 0。");
            _ = AcceptLoopAsync();
        }
        catch (HttpListenerException ex)
        {
            Log?.Invoke($"监听端口 {_cfg.Port} 失败（可能被占用）：{ex.Message}");
        }
    }

    private void StopListening()
    {
        try
        {
            if (_listener.IsListening) _listener.Stop();
        }
        catch
        {
            // 忽略停止异常
        }
    }

    private async Task AcceptLoopAsync()
    {
        int failures = 0;
        while (_listener.IsListening)
        {
            HttpListenerContext? ctx = null;
            bool got = false;
            try
            {
                ctx = await _listener.GetContextAsync();
                got = true;
                failures = 0;
            }
            catch (Exception ex)
            {
                // C-008：运行期监听异常（端口抢占/睡眠唤醒/权限变更）——记录 + 有限次数重试
                if (!_listener.IsListening) return; // 正常停止，静默退出
                Log?.Invoke($"错误：监听异常（{ex.Message}），尝试重新监听…");
                if (++failures >= 3)
                {
                    RaiseStatus("监听失败：端口不可用，请检查端口后重启智能模式。");
                    return;
                }
                await Task.Delay(2000);
                try
                {
                    _listener.Stop();
                    _listener.Start();
                    Log?.Invoke("监听已重新建立。");
                }
                catch (Exception ex2)
                {
                    Log?.Invoke($"错误：重新监听失败：{ex2.Message}");
                    RaiseStatus("监听失败：端口不可用，请检查端口后重启智能模式。");
                    return;
                }
            }
            if (got && ctx != null) _ = HandleRequestAsync(ctx); // 仅成功取到请求时处理；重试后回到循环顶部
        }
    }

    // ==================== 请求处理（排队唤醒 + 代理转发） ====================

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;

        // 本地状态探测端点：不触发唤醒、不刷新闲置计时
        var reqPath = req.Url?.AbsolutePath;
        if (string.Equals(reqPath, "/__status__", StringComparison.OrdinalIgnoreCase))
        {
            // C-103：phase 输出枚举名、idle_minutes 为当前已闲置分钟数（动态值）+ 配置阈值、
            // recent_logs 取 LogFile 环形缓冲（含 harness 侧日志），供外部 Agent 远程诊断
            var aff = _affinity;
            var idleMinutes = (DateTime.Now - new DateTime(Interlocked.Read(ref _lastTouchTicks))).TotalMinutes;
            var payload = new
            {
                phase = CurrentPhase.ToString(),
                inflight = Volatile.Read(ref _inflight),
                backend_port = _backendPort,
                idle_minutes = Math.Round(idleMinutes, 1),
                idle_threshold_minutes = IdleMinutes,
                slots = aff == null ? null : new
                {
                    count = aff.SlotCount,
                    bindings = aff.Snapshot().ToDictionary(
                        kv => kv.Key,
                        kv => new { slot = kv.Slot, last_active = kv.LastActive }),
                },
                recent_logs = LogFile.SnapshotRecent(),
            };
            await RequestProcessor.WriteJsonAsync(ctx, 200, System.Text.Json.JsonSerializer.Serialize(payload));
            return;
        }

        // 休眠释放进行中：不转发（服务正被终止），提示客户端稍后重试
        if (CurrentPhase == Phase.Sleeping)
        {
            RequestProcessor.WriteError(ctx, 502, "LLM 服务正在休眠释放，请稍后重试。");
            return;
        }

        bool isInference = RequestProcessor.IsInferenceRequest(req);

        // 探测类请求（GET /v1/models、健康检查等）无唤醒权：
        // 服务运行时照常代理；待机/休眠时直接拒绝，防止 Agent 周期性轮探
        // 把刚休眠的服务反复唤醒（唤醒→15分钟倒计时→再休眠→再唤醒循环）
        if (!isInference && !_server.IsRunning)
        {
            RequestProcessor.WriteError(ctx, 503, "LLM 服务处于待机/休眠状态，仅推理请求可触发唤醒。");
            return;
        }

        int cur = Interlocked.Increment(ref _inflight);
        if (cur > Volatile.Read(ref _inflightPeak)) Volatile.Write(ref _inflightPeak, cur); // C-102 峰值记录
        try
        {
            // 首请求排队等待唤醒完成（共享同一唤醒任务，防多进程冲突）
            await EnsureRunningAsync();
            // 只有真实推理请求才刷新闲置计时；探测类请求不算使用
            if (isInference) Touch();
            await ForwardAsync(ctx);       // 代理转发到后端 llama-server（流式直通）
            if (isInference) Touch();      // 请求完成：再次刷新倒计时
        }
        catch (Exception ex)
        {
            // 带上内层异常细节，便于定位（如连接重置 vs 超时）
            var detail = ex.InnerException != null ? $"（内层：{ex.InnerException.Message}）" : "";
            Log?.Invoke($"请求处理失败：{ex.Message}{detail}");
            RequestProcessor.WriteError(ctx, 503, ex.Message);
        }
        finally
        {
            Interlocked.Decrement(ref _inflight);
        }
    }

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
            var exe = LlamaFinder.Find(_cfg.ExePath)
                ?? throw new InvalidOperationException("未找到 llama-server.exe，请先在界面指定路径。");
            if (string.IsNullOrWhiteSpace(_cfg.ModelPath) || !File.Exists(_cfg.ModelPath))
                throw new InvalidOperationException($"模型文件不存在：{_cfg.ModelPath}");

            // 智能模式下自动探测空闲后端端口，规避 Hyper-V/WSL2 动态端口保留导致的绑定失败
            int srvPort = AutoMode ? SchedulerUtils.PickFreePort(PreferredBackendPort) : _cfg.Port;
            _backendPort = srvPort;

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

            _server.Start(exe, args, Path.GetDirectoryName(Path.GetFullPath(exe))!);

            // 13900F 纯大核绑定：按配置掩码绑定 P 核（留空 = 禁用）
            string? affinityDesc = CpuAffinity.Apply(_server.Current, _cfg.PCoreMask);
            Log?.Invoke(affinityDesc != null ? $"P核绑定生效：{affinityDesc}" : "P核绑定已禁用（掩码为空或无效）。");

            // 思考模式基线：新服务进程按本次启动参数重置（运行态指令切换不跨会话携带）
            var baseLevel = ThinkingModeHelper.DetermineInitialThinkingMode(_cfg.ExtraArgs);
            lock (_thinkingGate) { _thinkingMode = baseLevel; }
            ThinkingModeChanged?.Invoke(baseLevel);
            Log?.Invoke($"思考模式基线：「{ThinkingModeHelper.LabelOf(baseLevel)}」（{(ThinkingModeHelper.EffortOf(baseLevel) is var be && be != null ? $"reasoning_effort={be}, " : "")}enable_thinking={(baseLevel == ThinkingLevel.Off ? "false" : "true")}）。");

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

            await WaitReadyAsync(srvPort);

            // 3.2 Warming 子状态：eager restore（autoPre key 有快照者）+ dummy 预热（max_tokens=1 直连后端）。
            // 期间到达的请求经 EnsureRunningAsync await _wakeTask 天然排队等待（本方法未完成），无需额外机制。
            // 整体 60s 超时兜底；任何失败不阻塞转 Running（首请求仍有惰性 restore 自愈路径）。
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

    /// <summary>把请求原样转发到后端；ResponseHeadersRead + CopyToAsync 保证 SSE/流式响应直通。
    /// 审计 O-8：按管道阶段拆分为 读体 → 网关预处理 → 转发管道 → 完成清理 四段，本方法仅做编排。</summary>
    private async Task ForwardAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var uri = new Uri($"http://localhost:{_backendPort}{req.RawUrl}");
        string path = req.Url?.AbsolutePath ?? "";

        // ① 读取完整请求体（非流式检测 / 强制流式改写需要）；GET 无请求体
        byte[]? bodyBytes = await RequestProcessor.ReadRequestBodyAsync(req);

        // 请求体 dump（应用识别分析用）：每个 POST 请求的原始 body + headers 落盘；O-18：默认关闭，配置开启才生效（防 prompt 隐私落盘与无谓 IO）
        if (bodyBytes != null && bodyBytes.Length > 0 && _cfg.RequestDumpEnabled)
            DumpRequest(ctx, bodyBytes);

        // ② 网关预处理：思考模式拦截 / 槽位亲和与 KV restore / TokenGuard / 强制流式 / 前缀哈希
        string? finalBody = null;   // 最终请求体（网关改写后），供输出续接构造下一轮
        bool effStreaming = false;  // 有效流式（含 ForceStream 改写）
        int? routedSlot = null;     // 本次请求亲和路由的槽位号（崩溃恢复快照接续用）
        string? routedKey = null;   // 本次请求亲和路由的绑定 key（KV 快照文件名）
        JsonObject? root = null;    // 解析后的 DOM（400 自愈分支需原地裁剪重发）
        if (bodyBytes != null && bodyBytes.Length > 0)
        {
            var prepared = await PrepareGatewayAsync(ctx, req, path, bodyBytes);
            if (prepared == null) return; // TokenGuard 拒绝：响应已写出
            (bodyBytes, finalBody, effStreaming, routedSlot, routedKey, root) = prepared.Value;
        }

        // ③ 转发后端 + 响应管道 + 完成清理
        await SendAndPipeAsync(ctx, uri, path, req, bodyBytes, finalBody, effStreaming, routedSlot, routedKey, root);
    }

    /// <summary>网关预处理管道（仅推理请求）：
    /// 思考模式拦截 → 槽位亲和路由 + Tool 链锁定 + KV 驱逐 save / restore 自愈 → TokenGuard 裁剪 → 强制流式改写 → 前缀哈希可观测。
    /// 返回 (改写后 bodyBytes, finalBody, effStreaming, routedSlot, routedKey, root)；返回 null = TokenGuard 拒绝（已向客户端写 400）。</summary>
    private async Task<(byte[] BodyBytes, string FinalBody, bool EffStreaming, int? RoutedSlot, string? RoutedKey, JsonObject? Root)?> PrepareGatewayAsync(
        HttpListenerContext ctx, HttpListenerRequest req, string path, byte[] bodyBytes)
    {
        string p = req.Url?.AbsolutePath ?? "";
        bool isCompletions = p.Contains("completion", StringComparison.OrdinalIgnoreCase)
                             || p.Contains("embedding", StringComparison.OrdinalIgnoreCase);
        if (!isCompletions)
            return null; // 非推理请求：不做网关处理（finalBody=null 走纯透传管道）

        string body = System.Text.Encoding.UTF8.GetString(bodyBytes);

        // E-1/E-3：入口一次性解析 → 后续所有阶段复用同一棵 DOM，管道末端只序列化一次。
        // 解析失败（非法 JSON）→ root=null → 跳过全部 DOM 改写、原样透传（等价于旧实现各方法 try-catch 透传）。
        JsonObject? root = null;
        try { root = JsonNode.Parse(body)?.AsObject(); } catch { /* 非法 JSON */ }

        int? routedSlot = null;
        string? routedKey = null;

        // 思考模式拦截（仅 chat/completions）：识别指令 / 注入 reasoning_effort + enable_thinking / 校验修正非法档位
        if (RequestProcessor.IsChatCompletions(p) && root != null)
        {
            ThinkingLevel lvl, prev;
            bool changed;
            string? effortFix = null;
            lock (_thinkingGate)
            {
                prev = _thinkingMode;
                lvl = _thinkingMode;
                ThinkingModeHelper.InjectThinkingMode(root, ref lvl, out effortFix); // DOM 版：原地改树，不再 parse/serialize
                changed = lvl != prev;
                _thinkingMode = lvl;
            }
            if (changed)
            {
                Log?.Invoke($"思考模式已切换为「{ThinkingModeHelper.LabelOf(lvl)}」（{(ThinkingModeHelper.EffortOf(lvl) is var e && e != null ? $"reasoning_effort={e}, " : "")}enable_thinking={(lvl == ThinkingLevel.Off ? "false" : "true")}）。");
                ThinkingModeChanged?.Invoke(lvl);
            }
            if (effortFix != null)
                Log?.Invoke($"思考参数清洗：{effortFix}。");
        }

        // 槽位亲和路由（单槽/多槽均启用）：指纹绑定 + 注入 n_slots 固定槽位；槽忙时 llama.cpp 原生排队，不跨槽漂移
        var aff = _affinity;
        bool didKvRestore = false;
        if (aff != null && p.Contains("completion", StringComparison.OrdinalIgnoreCase))
        {
            (routedSlot, routedKey, didKvRestore) = await ApplySlotAffinityAsync(req, aff, root);
        }

        // Token Guard（仅 chat/completions）：计量 + 裁剪，防 "exceeds context size" 400
        // MeasureAsync：每次调用强制输出 [TOKEN-GUARD] 计量日志（消除排查盲区），再执行裁剪
        // KV restore 后强制重跑校验：saved_n 残留 + 新 prompt 叠加可能击穿窗口（本次故障根因之一）
        if (RequestProcessor.IsChatCompletions(p) && _cfg.TokenGuardEnabled && root != null)
        {
            var budget = _cfg.GetInputBudget(); // 多槽均分总容量：CtxSize ÷ Parallel − 输出预留 − Prompt头部开销预留
            var (ok, _, note) = await TokenGuard.MeasureAsync(root, _hc, _backendPort, budget, _cfg.ReservedOutputTokens, _cfg.ReservedPromptOverhead);
            if (!ok)
            {
                Log?.Invoke($"Token Guard 拒绝：{note}");
                RequestProcessor.WriteError(ctx, 400, note ?? "上下文超长");
                return null;
            }
            if (note != null) Log?.Invoke(note);
            if (didKvRestore)
            {
                Log?.Invoke("[TOKEN-GUARD] KV restore 后重跑校验通过（saved_n 残留 + 新 prompt 未超预算）");
                // restore 命中 = 快照已加载到槽位：标记新鲜，避免本轮完成后立即冗余重存
                if (routedKey != null) lock (_kvStateGate) _freshSnapshotKeys.Add(routedKey);
            }
        }

        // 非流式请求检测 + 可选强制流式改写：
        // 非流式时 llama-server 会缓存整个响应直到生成完毕才返回，期间无任何字节流动，
        // 客户端读超时→断开→agent 重试全量上下文→重新预填。流式则边生成边发字节，不会读超时。
        bool streaming;
        if (root != null)
        {
            // DOM 直读替代对数 MB body 的正则扫描（E-1）
            streaming = false;
            try { if (root["stream"]?.GetValue<bool>() == true) streaming = true; } catch { /* 非 bool 值：按 false */ }
        }
        else
            streaming = System.Text.RegularExpressions.Regex.IsMatch(body, @"""stream""\s*:\s*true");

        if (!streaming)
        {
            if (_cfg.ForceStream)
            {
                if (root != null)
                {
                    RequestProcessor.EnsureStreamTrue(root); // DOM 版：直接树上置 stream=true
                    Log?.Invoke("强制流式：已将非流式请求改写为 stream=true（SSE 直通）。");
                }
                else
                {
                    // C-005 降级：非法 JSON 走字符串级改写；改写失败透传原始请求，禁止下发损坏 JSON
                    var rewritten = RequestProcessor.EnsureStreamTrue(body);
                    if (rewritten != null)
                        bodyBytes = System.Text.Encoding.UTF8.GetBytes(rewritten);
                    Log?.Invoke("警告：强制流式改写失败（请求体不是合法 JSON），已透传原始请求。");
                }
            }
            else
            {
                WarnNonStreamOnce();
            }
        }

        // §8 可观测：前缀哈希 HIT/MISS 判定（原生 KV 前缀复用；TokenGuard 之后按实际下发体计算）
        if (routedKey != null)
        {
            bool? wrapperHit = LogPrefixHash(routedKey, root);
            // 3.1：入队 restore 判定上下文（仅该 key 存在快照时；FIFO + TTL 防错位，prompt eval 行到达时弹最旧条目判定）
            var rs = _restoreStats;
            var kvc = _kvCache;
            if (rs != null && routedSlot is int rsSlot && kvc != null && kvc.HasCache(routedKey))
                rs.RecordRequest(routedKey, rsSlot, wrapperHit ?? false, kvc.SavedTokens(routedKey));
        }

        // 管道末端：唯一一次序列化 + 编码转换（E-1/E-3）
        if (root != null)
        {
            body = root.ToJsonString();
            bodyBytes = System.Text.Encoding.UTF8.GetBytes(body);
        }

        return (bodyBytes, body, streaming || _cfg.ForceStream, routedSlot, routedKey, root);
    }

    /// <summary>槽位亲和阶段：指纹绑定（LRU 驱逐 / §4.2 自动强占）→ §4.5 Tool 链锁定 → 驱逐前 KV save → restore 自愈 → n_slots 注入。
    /// E-1：直接操作调用方持有的同一棵 DOM（root=null 时跳过 DOM 步骤，等价旧实现 parse 失败透传）。
    /// 返回（路由槽位、绑定 key、是否执行了 KV restore——restore 后需重跑 TokenGuard 校验）。</summary>
    private async Task<(int? RoutedSlot, string? RoutedKey, bool DidRestore)> ApplySlotAffinityAsync(
        HttpListenerRequest req, SlotAffinity aff, JsonObject? root)
    {
        // §4.2 自动冻结：应用类型前缀在 AutoPreemptiveApps → 绑定强制强占（暂停 LRU 驱逐）
        var autoPre = ParseAutoPreemptivePrefixes();
        var (slot, key, isNew, evicted, evictedSlot, evictedKvCache) = aff.GetSlot(req.Headers, autoPre);
        int? routedSlot = slot;
        string? routedKey = key;

        // §4.5 Tool 链会话锁定：末条消息 role=tool → agent 工具循环进行中 → 锁槽位防驱逐；循环结束自动解锁
        if (key != null && root != null)
        {
            bool inToolLoop = RequestProcessor.DetectToolLoop(root);
            bool didLock = false, didUnlock = false;
            // O-15：锁内只做 _toolLockedKeys 集合判定；aff 调用（自带内部锁 + 文件 I/O）全部移出，消除锁嵌套
            bool alreadyPreemptive = aff.IsPreemptive(key);
            lock (_kvStateGate)
            {
                if (inToolLoop)
                {
                    if (!_toolLockedKeys.Contains(key) && !alreadyPreemptive)
                    {
                        _toolLockedKeys.Add(key);
                        didLock = true;
                    }
                }
                else if (_toolLockedKeys.Remove(key))
                {
                    didUnlock = true;
                }
            }
            if (didLock)
            {
                aff.MarkToolLocked(key); // 标记到 SlotAffinity（驱逐优先级：Tool 锁定 > 手动/自动强占）
                aff.SetPreemptive(key, true); // 移出锁外（O-15）
                EmitSlot($"[KV-LOCK] Tool 链会话锁定：{key} → slot{slot}（强占，不驱逐）");
            }
            else if (didUnlock)
            {
                aff.UnmarkToolLocked(key);
                aff.SetPreemptive(key, false);
                EmitSlot($"[KV-UNLOCK] Tool 链结束，解除锁定：{key}");
            }
        }

        var kv = _kvCache;

        // KV Cache：驱逐前 save（仅当被驱逐者的 KvCache=true；evicted != null 已蕴含 evictedSlot 有效，SlotAffinity 仅驱逐时置位）
        if (evicted != null && kv != null && evictedKvCache)
        {
            try
            {
                var saveTask = kv.SaveAsync(evictedSlot, evicted);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await saveTask;
                EmitSlot($"KV Cache 保存：{evicted} → slot{evictedSlot}（{sw.Elapsed.TotalSeconds:F1}s）");
            }
            catch (Exception ex)
            {
                EmitSlot($"KV Cache 保存失败：{evicted}（{ex.Message}），降级为全量 prefill。");
            }
        }
        else if (evicted != null && !evictedKvCache)
        {
            EmitSlot($"驱逐 {evicted}（KV Cache 已关闭，不保存）");
        }

        // KV Cache：restore（两种触发：① isNew 重绑定；② 进程重启后该 key 首次使用——休眠唤醒 KV 自愈。
        // 无论是否命中 restore，都把 key 记入 _servedKeysThisRun：本进程服务过即不再 restore，防误用磁盘旧快照回退内存新状态）
        bool didRestore = false;
        if (key != null)
        {
            bool firstUseThisRun;
            lock (_kvStateGate) firstUseThisRun = _servedKeysThisRun.Add(key);
            if (kv != null && kv.HasCache(key) && (isNew || firstUseThisRun))
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    bool ok = await kv.RestoreAsync(slot, key);
                    if (ok)
                    {
                        EmitSlot($"[KV-RESTORE] KV Cache 恢复：{key} → slot{slot}（{sw.Elapsed.TotalSeconds:F1}s，跳过全量 prefill）");
                        // §8：restore 后重建前缀哈希基线（旧哈希对应驱逐前状态，避免下次请求误报 MISS）
                        lock (_kvStateGate) _prefixHashes.Remove(key);
                        didRestore = true; // restore 成功：标记需重跑 TokenGuard（saved_n 残留 + 新 prompt 叠加可能击穿窗口）
                    }
                    else
                    {
                        EmitSlot($"KV Cache 恢复失败：{key}（槽位可能忙），降级为全量 prefill。");
                    }
                }
                catch (Exception ex)
                {
                    EmitSlot($"KV Cache 恢复异常：{key}（{ex.Message}），降级为全量 prefill。");
                }
            }
        }

        if (isNew)
        {
            var evt = $"槽位绑定：{key} → slot{slot}{(evicted != null ? $"（驱逐 {evicted}）" : "")}";
            EmitSlot(evt);
            SlotBindingChanged?.Invoke();
        }
        // E-1：n_slots 注入直接改树（已有 n_slots 时不覆盖，尊重客户端显式指定）
        if (root != null)
            ThinkingModeHelper.InjectNSlots(root, slot);
        return (routedSlot, routedKey, didRestore);
    }

    /// <summary>转发阶段：构造后端请求（过滤逐跳头）→ 连接异常 500ms 重试一次 → 400 上下文超限自愈 → 响应管道（崩溃恢复/断点快照清理/客户端断开兜底）。</summary>
    private async Task SendAndPipeAsync(
        HttpListenerContext ctx, Uri uri, string path, HttpListenerRequest req,
        byte[]? bodyBytes, string? finalBody, bool effStreaming, int? routedSlot, string? routedKey, JsonObject? root)
    {
        using var msg = RequestProcessor.BuildBackendRequest(req, uri, bodyBytes);

        HttpResponseMessage resp = await TryConnectWithRetryAsync(msg);
        using (resp)
        {
            var outResp = ctx.Response;

            // 400 上下文超限自愈（激进裁剪 + KV 废弃 + 重发）；已处理则返回
            if (await TryRecoverContextOverflowAsync(resp, outResp, req, uri, path, root, finalBody, effStreaming, routedSlot, routedKey))
                return;

            // 响应管道 + 崩溃恢复 + 断点快照清理 + 存档（含客户端断开兜底）
            await PumpResponseAsync(resp, outResp, uri, path, finalBody, effStreaming, routedSlot, routedKey);
        }
    }

    /// <summary>连接异常 500ms 重试一次：后端刚重启/连接被重置时稍等重发（SendAndPipeAsync 子流程①）。</summary>
    private async Task<HttpResponseMessage> TryConnectWithRetryAsync(HttpRequestMessage msg)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _hc.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (HttpRequestException)
        {
            // 连接层瞬时失败（后端刚重启 / 连接被重置）：稍等后重试一次
            Log?.Invoke("转发连接异常，正在重试…");
            await Task.Delay(500);
            resp = await _hc.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead);
        }
        return resp;
    }

    /// <summary>400 上下文超限自愈（SendAndPipeAsync 子流程②）：读取 errBody → TokenGuard 激进裁剪 → KV 废弃 → 重发。
    /// 前置 TokenGuard 是快速预估（BuildMessagesText 不含 tools/Jinja 模板），ReservedPromptOverhead 预留不足时仍可能击穿；
    /// 此分支是最后一道防线。返回 true = 已处理（调用方应 return）；false = 未触发自愈（继续正常流程）。</summary>
    private async Task<bool> TryRecoverContextOverflowAsync(
        HttpResponseMessage resp, HttpListenerResponse outResp, HttpListenerRequest req, Uri uri,
        string path, JsonObject? root, string? finalBody, bool effStreaming, int? routedSlot, string? routedKey)
    {
        if (resp.StatusCode != System.Net.HttpStatusCode.BadRequest || !RequestProcessor.IsChatCompletions(path) || root == null || finalBody == null)
            return false;
        if (resp.StatusCode == System.Net.HttpStatusCode.BadRequest && RequestProcessor.IsChatCompletions(path) && root != null && finalBody != null)
        {
            string errBody = "";
            try { errBody = await resp.Content.ReadAsStringAsync(); } catch { /* 读取失败按非超限处理 */ }
            if (errBody.Contains("exceeds the available context size", StringComparison.OrdinalIgnoreCase))
            {
                Log?.Invoke("[EDGE-CASE-CONTEXT-OVERFLOW-400] llama.cpp 上下文超限 400，触发自愈（aggressive trim + KV 废弃 + 重发）");
                Log?.Invoke("[TOKEN-GUARD-FATAL] real prompt overflow，aggressive trim + KV 废弃 + 重发");
                // 1. 激进裁剪：预算收紧 50%（比正常预算更严格）
                int tightBudget = Math.Max(AppConfig.MinInputBudgetTokens, _cfg.GetInputBudget() / 2);
                var (ok, modified, note) = await TokenGuard.GuardAsync(root, _hc, _backendPort, tightBudget);
                if (!ok)
                {
                    // 裁剪失败：原样返回 400
                    outResp.StatusCode = 400;
                    outResp.ContentType = "application/json";
                    var bytes = System.Text.Encoding.UTF8.GetBytes(errBody);
                    outResp.ContentLength64 = bytes.Length;
                    await outResp.OutputStream.WriteAsync(bytes);
                    return true;
                }
                // 2. 废弃 slot KV 缓存（旧 saved_n 残留与新裁剪后 prompt 不匹配，强制全量 prefill）
                if (routedKey != null && _kvCache != null)
                {
                    try
                    {
                        _kvCache.DeleteCache(routedKey);
                        lock (_kvStateGate) _prefixHashes.Remove(routedKey);
                        Log?.Invoke($"[TOKEN-GUARD-FATAL] KV 缓存废弃：{routedKey}（强制全量 prefill）");
                    }
                    catch { /* 清理失败不影响重发 */ }
                }
                // 3. 重新提交请求（用裁剪后的 root 序列化）
                string newBody = modified ? root.ToJsonString() : finalBody;
                var newMsg = RequestProcessor.BuildBackendRequest(req, uri, System.Text.Encoding.UTF8.GetBytes(newBody));
                HttpResponseMessage retryResp;
                try
                {
                    retryResp = await _hc.SendAsync(newMsg, HttpCompletionOption.ResponseHeadersRead);
                }
                catch (HttpRequestException)
                {
                    outResp.StatusCode = 502;
                    outResp.ContentType = "application/json";
                    await outResp.OutputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"error\":\"400 自愈重发连接失败\"}"));
                    return true;
                }
                using (retryResp)
                {
                    outResp.StatusCode = (int)retryResp.StatusCode;
                    var ct2 = retryResp.Content.Headers.ContentType?.ToString();
                    outResp.ContentType = string.IsNullOrEmpty(ct2) ? "application/octet-stream" : ct2!;
                    if (retryResp.IsSuccessStatusCode)
                    {
                        // 重发成功：走正常响应管道
                        (bool completed, string accumulated) = await PipeResponseAsync(
                            retryResp, outResp, uri, path, newBody, effStreaming, routedSlot, routedKey);
                        Log?.Invoke($"[TOKEN-GUARD-FATAL] 400 自愈重发{(completed ? "成功" : "失败")}");
                        return true;
                    }
                    else
                    {
                        // 重发仍失败：返回错误
                        string retryErr = "";
                        try { retryErr = await retryResp.Content.ReadAsStringAsync(); } catch { }
                        outResp.ContentType = "application/json";
                        var bytes2 = System.Text.Encoding.UTF8.GetBytes(retryErr);
                        outResp.ContentLength64 = bytes2.Length;
                        await outResp.OutputStream.WriteAsync(bytes2);
                        Log?.Invoke($"[TOKEN-GUARD-FATAL] 400 自愈重发仍失败（{(int)retryResp.StatusCode}）");
                        return true;
                    }
                }
            }
        }
        return false;
    }

    /// <summary>响应管道编排（SendAndPipeAsync 子流程③）：设置响应头 → PipeResponseAsync（输出续接/崩溃识别）
    /// → 崩溃恢复（keep-alive 保活 + KV 快照接续/全量重放）→ 续接成功清理过期断点快照
    /// → 首请求存档 + 每轮条件式后台 save；含客户端断开兜底（catch）与响应关闭（finally）。</summary>
    private async Task PumpResponseAsync(
        HttpResponseMessage resp, HttpListenerResponse outResp, Uri uri, string path,
        string? finalBody, bool effStreaming, int? routedSlot, string? routedKey)
    {
        outResp.StatusCode = (int)resp.StatusCode;
        var ct = resp.Content.Headers.ContentType?.ToString();
        outResp.ContentType = string.IsNullOrEmpty(ct) ? "application/octet-stream" : ct!;
        try
        {
            (bool completed, string accumulated) = await PipeResponseAsync(
                resp, outResp, uri, path, finalBody, effStreaming, routedSlot, routedKey);

            // 崩溃恢复：流中断/5xx bad_alloc → keep-alive 保活 + KV 快照接续 / 进程重启全量重放
            if (!completed && _cfg.CrashRecoveryEnabled && effStreaming && finalBody != null)
            {
                var log2 = (string s) => Log?.Invoke(s);
                await TryCrashRecoverAsync(uri, outResp, finalBody, accumulated, routedSlot, routedKey, log2);
            }

            // §6.3：续接成功 → 清理过期断点快照（槽活 KV 已领先断点，旧快照 restore 会回退状态）；失败则保留供下次 rebinding/崩溃恢复 restore
            if (completed && routedKey != null)
            {
                bool wasPending;
                lock (_kvStateGate) wasPending = _truncPending.Remove(routedKey);
                if (wasPending)
                {
                    try
                    {
                        _kvCache?.DeleteCache(routedKey);
                        Log?.Invoke($"[KV-CLEANUP] 续接成功，清理过期断点快照：{routedKey}");
                    }
                    catch { /* 清理失败不影响主流程 */ }
                }
            }

            // 1.1 首请求存档：自动快照 key 首次真实 prefill 完成后立即落盘快照（每唤醒周期一次），
            // 防进程崩溃未休眠时磁盘快照停留在旧状态（缺最新 KV）。失败不阻塞主流程，下请求重试。
            if (completed && routedKey != null && _kvCache != null && routedSlot is int saveSlot
                && IsAutoSnapshotKey(routedKey))
            {
                bool alreadySaved;
                lock (_kvStateGate) alreadySaved = _savedKeysThisRun.Contains(routedKey);
                if (!alreadySaved)
                {
                    var swSave = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        await _kvCache.SaveAsync(saveSlot, routedKey);
                        lock (_kvStateGate) { _savedKeysThisRun.Add(routedKey); _freshSnapshotKeys.Add(routedKey); }
                        Log?.Invoke($"[KV-SAVE] 首请求存档：{routedKey} → slot{saveSlot}（{swSave.Elapsed.TotalSeconds:F1}s）");
                    }
                    catch (Exception ex)
                    {
                        Log?.Invoke($"[EDGE-CASE-SAVE-FAILED] {routedKey}：首请求存档失败（{ex.Message}），废弃旧快照，下次请求重试。");
                        _kvCache.DeleteCache(routedKey);
                    }
                }
            }

            // 1.2 每轮条件式后台 save（RAMDisk 快照全权接管）：快照非新鲜（上一轮后 KV 有增量）→ 异步后台 save，
            // 不阻塞响应返回（零额外延迟）；成功 → 标记新鲜；失败 → [EDGE-CASE-SAVE-FAILED] + 废弃快照（下轮自动重试）。
            // 并发安全：KvCacheManager._inflightSaves 按 key 去重，与驱逐前/休眠前同步 save 共享在途任务。
            if (completed && routedKey != null && _kvCache != null && routedSlot is int bgSaveSlot
                && IsAutoSnapshotKey(routedKey))
            {
                bool fresh;
                lock (_kvStateGate) fresh = _freshSnapshotKeys.Contains(routedKey);
                if (!fresh)
                {
                    var bgKey = routedKey;
                    var bgSlot = bgSaveSlot;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _kvCache.SaveAsync(bgSlot, bgKey);
                            lock (_kvStateGate) _freshSnapshotKeys.Add(bgKey);
                            Log?.Invoke($"[KV-SAVE] 每轮后台快照：{bgKey} → slot{bgSlot}");
                        }
                        catch (Exception ex)
                        {
                            Log?.Invoke($"[EDGE-CASE-SAVE-FAILED] {bgKey}：每轮后台快照失败（{ex.Message}），废弃旧快照。");
                            _kvCache.DeleteCache(bgKey);
                        }
                    });
                }
            }
        }
        catch (Exception)
        {
            // 客户端断开/写入失败：方法退出时 dispose resp 关闭后端连接，
            // llama-server 检测到断开会取消任务并保留部分槽位 KV（f_keep），释放 GPU。
            // 多 agent 模式下这是预期行为（agent 超时/重试），非致命错误。
            Log?.Invoke("客户端断开，已中止本次生成（多 agent 下属正常重试）。");
        }
        finally
        {
            outResp.Close();
        }
    }

    /// <summary>响应管道：chat/completions 走输出续接 + 崩溃识别（截断断点快照闭包 / 5xx bad_alloc 判定），其余透传。</summary>
    /// 返回 (是否完整完成, 已累积输出文本)。</summary>
    private async Task<(bool Completed, string Accumulated)> PipeResponseAsync(
        HttpResponseMessage resp, HttpListenerResponse outResp, Uri uri, string path,
        string? finalBody, bool effStreaming, int? routedSlot, string? routedKey)
    {
        if (!(RequestProcessor.IsChatCompletions(path) && finalBody != null))
        {
            await resp.Content.CopyToAsync(outResp.OutputStream);
            return (true, "");
        }

        // 输出续接 + 崩溃识别：finish_reason=length 自动续接；流中断/5xx bad_alloc → Completed=false
        var log2 = (string s) => Log?.Invoke(s);

        // §4.1 截断断点快照闭包：finish_reason=length 时、续接请求发出前 save 槽位 KV（此时槽位 KV 仍完整）
        Func<Task>? onTrunc = null;
        var kvForTrunc = _kvCache;
        if (kvForTrunc != null && routedSlot is int truncSlot && !string.IsNullOrEmpty(routedKey))
        {
            var truncKey = routedKey;
            onTrunc = async () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await kvForTrunc.SaveAsync(truncSlot, truncKey);
                EmitSlot($"[KV-SAVE] 截断断点快照：{truncKey} → slot{truncSlot}（{sw.Elapsed.TotalSeconds:F1}s）");
                lock (_kvStateGate) _truncPending.Add(truncKey); // 标记「截断待续接」
            };
        }

        bool completed;
        string accumulated = ""; // bad_alloc 崩溃路径无输出累积（保持与原实现一致的初始值）
        if (resp.IsSuccessStatusCode)
        {
            if (effStreaming)
            {
                // SSE 流式响应：必须设置 text/event-stream（llama-server 返回 application/json，
                // 直接复制会导致客户端按 JSON 解析 SSE 行报错 "Unexpected non-whitespace character after JSON"）
                outResp.ContentType = "text/event-stream";
                (completed, accumulated) = await OutputContinuer.HandleStreamAsync(_hc, uri, _backendPort, finalBody, resp, outResp, _cfg, log2, onTrunc);
            }
            else
                (completed, accumulated) = await OutputContinuer.HandleNonStreamAsync(_hc, uri, _backendPort, finalBody, resp, outResp, _cfg, log2, _cfg.CrashRecoveryEnabled);
        }
        else
        {
            // 5xx 错误响应：判定是否 bad_alloc 崩溃（恢复启用 → 不转发，交给崩溃恢复）
            string errBody = System.Text.Encoding.UTF8.GetString(await resp.Content.ReadAsByteArrayAsync());
            bool isBadAlloc = errBody.Contains("bad allocation", StringComparison.OrdinalIgnoreCase)
                             || CrashRecovery.WasBadAlloc(BadAllocEvidenceWindow);
            if (isBadAlloc && _cfg.CrashRecoveryEnabled && effStreaming)
            {
                completed = false; // 交给 TryCrashRecoverAsync
            }
            else
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(errBody);
                outResp.ContentType = "application/json";
                outResp.ContentLength64 = bytes.Length;
                await outResp.OutputStream.WriteAsync(bytes);
                completed = true;
            }
        }
        return (completed, accumulated);
    }

    // ==================== KV 全场景复用辅助（§4.2/§4.5/§8） ====================

    /// <summary>解析 AutoPreemptiveApps 配置为前缀集合（§4.2 自动冻结）。</summary>
    private List<string> ParseAutoPreemptivePrefixes()
    {
        return _cfg.AutoPreemptiveApps.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    /// <summary>判定亲和 key 是否匹配任一自动强占前缀（§4.2 槽位冻结语义，public 供测试）。</summary>
    public bool IsAutoPreKey(string key)
    {
        return ParseAutoPreemptivePrefixes().Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>解析 AutoSnapshotKeys 配置为前缀集合（仅快照持久化：首请求存档 + Warming eager restore，不锁槽）。</summary>
    private List<string> ParseAutoSnapshotPrefixes()
    {
        return _cfg.AutoSnapshotKeys.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    /// <summary>判定亲和 key 是否匹配任一自动快照前缀（1.1 首请求存档 / 3.2 Warming eager restore 条件；不参与强占/驱逐拒绝，public 供测试）。</summary>
    public bool IsAutoSnapshotKey(string key)
    {
        return ParseAutoSnapshotPrefixes().Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>§8 可观测：前缀哈希 HIT/MISS 判定。一致 → 原生 KV 前缀复用（增量 prefill）；不一致 → 全量重算。
    /// 返回 wrapper 指纹判定结果（true=HIT / false=MISS / null=无指纹数据），供 3.1 RestoreStats FIFO 归属。
    /// KV-MISS 条件式日志：
    /// - HitByDelta（上一轮 restore 命中 + 增量 prefill）→ [KV-MISS-DEBUG]（降级，agent 每轮 messages 必变是设计预期）；
    /// - FullPrefill/MidRange（真实全量重算）或无判定数据 → [KV-MISS]（保留 INFO，用于快照损坏等故障排查）。
    /// Metrics 埋点不受影响：RestoreStats.OnPromptEval 持续统计 false_miss。</summary>
    private bool? LogPrefixHash(string key, JsonObject? root)
    {
        var hash = root != null ? RequestProcessor.PrefixHash(root) : null;
        if (hash == null) return null;
        lock (_kvStateGate)
        {
            if (_prefixHashes.TryGetValue(key, out var prev))
            {
                bool hit = prev == hash;
                if (hit)
                    Log?.Invoke($"[KV-HIT] {key}：前缀未变 → 原生 KV 复用（增量 prefill）");
                else
                {
                    // MISS 分支：区分 HitByDelta 虚假 MISS vs 真实 MISS
                    var lj = _restoreStats?.LastJudgeResult;
                    if (lj != null && lj.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && lj.Reason == "HitByDelta")
                        Log?.Invoke($"[KV-MISS-DEBUG] {key}：消息指纹变更，HitByDelta 增量复用，增量 prefill={lj.PromptEvalTokens} tokens");
                    else
                        Log?.Invoke($"[KV-MISS] {key}：前缀变更 → 全量重算");
                }
                _prefixHashes[key] = hash;
                return hit;
            }
            _prefixHashes[key] = hash;
            return null; // 该 key 首次请求：无历史指纹可比
        }
    }

    // ==================== 崩溃自动恢复（bad_alloc） ====================

    /// <summary>
    /// bad_alloc 崩溃自动恢复管道（三分支）：
    /// - 分支 A（服务端存活 + 客户端连接可持有）：抢 save 槽位 KV 快照 → SSE keep-alive 保活客户端
    ///   → 内存余量检查 → 快照接续（restore + 回填已生成部分 + 续接指令）或全量重放（严格预算）→ 输出灌入同一条流（客户端无感）
    /// - 分支 B（进程死亡）：重启至多 MaxAutoRestarts 次并等就绪 → 严格预算全量重放（无快照）
    /// - 分支 C（客户端已断开）：不重放；agent 侧重试走现有 KV restore 路径
    /// 熔断器：10 分钟窗口内 ≥3 次确认崩溃 → 停止自动恢复，醒目报错，等待人工介入。
    /// </summary>
    private async Task TryCrashRecoverAsync(
        Uri uri, HttpListenerResponse outResp, string finalBody, string accumulated,
        int? routedSlot, string? routedKey, Action<string>? log)
    {
        // ── 诊断增强：崩溃瞬间记录系统资源（判定主机 RAM 还是显存打满 → 长期方案：降 ctx / 换 mmap / 加内存）──
        var m = new SystemMetrics();
        var (usedGb, totalGb) = m.GetMemory();
        double freeGb = totalGb - usedGb;
        int? vramUsedMb = await SystemMetrics.GetVramUsedMbAsync();
        log?.Invoke($"崩溃恢复触发。崩溃时刻诊断：空闲 RAM {freeGb:F1}/{totalGb:F1} GB，显存 {(vramUsedMb is int v ? $"{v} MB" : "未知")}");

        // ── 熔断器：10 分钟窗口内 ≥3 次确认崩溃 → 停止自动恢复（需人工介入）──
        CrashRecovery.RecordCrash();
        if (!CrashRecovery.AllowRecover())
        {
            log?.Invoke($"熔断器已跳闸：10 分钟内 {CrashRecovery.ConfirmedCount} 次崩溃 ≥ {CrashRecovery.MaxCrashesInWindow}，停止自动恢复。请加内存 / 降上下文后手动重试。");
            RaiseStatus("⚠ 崩溃熔断：自动恢复已停止，需人工介入");
            return;
        }

        // 分支 C（客户端已断开）由各分支内的探测写判定：立即写一行 keep-alive，写失败 = 客户端已断开 → 不重放。

        if (_server.IsRunning)
            await RecoverAliveAsync(uri, outResp, finalBody, accumulated, routedSlot, routedKey, freeGb, log);
        else
            await RestartAndReplayAsync(uri, outResp, finalBody, log);
    }

    /// <summary>分支 A：服务端存活 + 客户端连接可持有 → 抢 save 快照（抢在 release 前）→ 内存余量检查 → 快照接续或全量重放。
    /// keep-alive 保活 / 分支 C 探测 / 异常兜底由 RunCrashRecoveryAsync 公共骨架提供（审计 O-10）。</summary>
    private Task RecoverAliveAsync(
        Uri uri, HttpListenerResponse outResp, string finalBody, string accumulated,
        int? routedSlot, string? routedKey, double freeGb, Action<string>? log)
        => RunCrashRecoveryAsync(outResp, log, async writeGate =>
        {
            // ── 抢 save 槽位 KV（llama.cpp 崩溃即 release 槽位；抢到 n_saved>0 = 有效快照，否则全量路径）──
            var kv = _kvCache;
            bool snapshotOk = false;
            if (kv != null && routedSlot is int slot && !string.IsNullOrEmpty(routedKey))
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    await kv.SaveAsync(slot, routedKey);
                    int nSaved = kv.SavedTokens(routedKey);
                    if (nSaved > 0)
                    {
                        snapshotOk = true;
                        log?.Invoke($"崩溃快照抢获：{routedKey} → slot{slot}（{sw.Elapsed.TotalSeconds:F1}s，{nSaved} tokens）");
                    }
                    else
                    {
                        log?.Invoke("崩溃快照为空（槽位已 release，n_saved=0）：降级全量重放路径。");
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke($"崩溃快照保存失败：{ex.Message}，降级全量重放路径。");
                }
            }

            // ── 内存余量检查：空闲 RAM < 4GB → 预算收紧 25%（防同点再崩）──
            bool tightBudget = freeGb < TightMemoryFreeGb;
            int budget = _cfg.GetInputBudget();
            if (tightBudget)
            {
                budget = Math.Max(AppConfig.MinInputBudgetTokens, (int)(budget * TightBudgetFactor));
                log?.Invoke($"内存余量不足（空闲 {freeGb:F1} GB < {TightMemoryFreeGb} GB）：重放预算收紧 25% 防再崩。");
            }

            string? replayBody = null;
            bool usedSnapshot = false; // 实际走快照接续路径的标志（末行日志准确反映路径）

            // ── 快照接续：restore 快照 + 回填 assistant（已生成部分）+ 续接指令 ──
            if (snapshotOk && kv != null && routedSlot is int slot2 && !string.IsNullOrEmpty(routedKey))
            {
                bool restored = false;
                try { restored = await kv.RestoreAsync(slot2, routedKey); }
                catch (Exception ex) { log?.Invoke($"快照 restore 异常：{ex.Message}"); }

                if (restored)
                {
                    // accumulated 为空（prefill 阶段崩溃无输出）→ 不构造空 assistant 续接体，原请求直接重放（restore 的 KV 供前缀复用）
                    string? contBody = string.IsNullOrEmpty(accumulated)
                        ? null
                        : OutputContinuer.BuildContinuationBody(finalBody, accumulated);
                    bool useSnapshot = contBody != null || string.IsNullOrEmpty(accumulated);
                    if (!useSnapshot)
                        log?.Invoke("续接体构造失败：降级全量重放路径。");

                    if (useSnapshot)
                    {
                        var target = contBody ?? finalBody;
                        var (ok, guarded, note) = await TokenGuard.GuardAsync(_hc, _backendPort, target, budget);
                        if (!ok)
                        {
                            log?.Invoke($"续接中止：{note}（内存余量不足且上下文无法裁剪）。");
                            return; // 中止并明确报错（客户端流结束，agent 侧重试走现有机制）
                        }
                        if (note != null) log?.Invoke(note);
                        replayBody = guarded ?? target;
                        usedSnapshot = true;
                    }
                }
                else
                {
                    log?.Invoke("快照 restore 失败（槽位忙？）：降级全量重放路径。");
                }
            }

            // ── 全量重放路径（无快照 / restore 失败）：严格预算 TokenGuard 裁剪 + 原请求重发 ──
            if (replayBody == null)
            {
                var (ok, guarded, note) = await TokenGuard.GuardAsync(_hc, _backendPort, finalBody, budget);
                if (!ok)
                {
                    log?.Invoke($"重放中止：{note}（内存余量不足且上下文无法裁剪）。");
                    return;
                }
                if (note != null) log?.Invoke(note);
                replayBody = guarded ?? finalBody;
            }

            log?.Invoke(usedSnapshot ? "崩溃快照接续：restore KV + 回填已生成部分 + 续接指令…" : "全量重放：原请求重发（严格预算）…");
            var (replayCompleted, _) = await OutputContinuer.SendAndPipeStreamAsync(_hc, uri, _backendPort, replayBody, outResp, _cfg, log, writeGate);
            if (!replayCompleted)
                log?.Invoke("重放流再次中断（二次崩溃？）：本次恢复失败，agent 侧重试将走现有机制。");
        });

    /// <summary>崩溃恢复公共骨架（审计 O-10：收敛 A/B 分支重复的 keep-alive 启动 + 分支 C 探测 + 异常兜底 + 收尾样板）：
    /// 立即启动 SSE keep-alive（保活客户端）→ 探测客户端连接（断开即放弃重放）→ 执行分支体 → 统一异常兜底与 keep-alive 收尾。</summary>
    private async Task RunCrashRecoveryAsync(HttpListenerResponse outResp, Action<string>? log, Func<SemaphoreSlim, Task> body)
    {
        // ── SSE keep-alive（立即启动：从崩溃检测时刻起保活客户端，Trae 看到停顿后继续出字）──
        var keepAliveCts = new CancellationTokenSource();
        var writeGate = new SemaphoreSlim(1, 1); // 写门控：keep-alive 与重放管道并发写互斥，防 SSE 行交错
        Task keepAliveTask = RunKeepAliveAsync(outResp, writeGate, keepAliveCts.Token, log);
        try
        {
            // ── 分支 C 探测：客户端已断开 → 不重放（agent 侧重试走现有 KV restore 路径）──
            if (!await ProbeClientConnectedAsync(outResp, writeGate))
            {
                log?.Invoke("客户端已断开：跳过重放（agent 侧重试将走现有 KV restore 路径）。");
                return;
            }
            await body(writeGate);
        }
        catch (Exception ex)
        {
            log?.Invoke($"崩溃恢复异常：{ex.Message}");
        }
        finally
        {
            keepAliveCts.Cancel();
            try { await keepAliveTask; } catch { } // 等在途 keep-alive 写入完成再返回（调用方负责关连接）
        }
    }

    /// <summary>分支 B：进程死亡 → 重启至多 MaxAutoRestarts 次并等就绪 → 严格预算全量重放（无快照，防同点再崩）。
    /// keep-alive 保活 / 分支 C 探测 / 异常兜底由 RunCrashRecoveryAsync 公共骨架提供（审计 O-10）。</summary>
    private Task RestartAndReplayAsync(Uri uri, HttpListenerResponse outResp, string finalBody, Action<string>? log)
        => RunCrashRecoveryAsync(outResp, log, async writeGate =>
        {
            int maxRestarts = Math.Max(0, _cfg.MaxAutoRestarts);
            if (maxRestarts == 0)
            {
                log?.Invoke("进程已死且 MaxAutoRestarts=0（自动重启禁用）：无法自动恢复，请手动启动。");
                return;
            }

            bool restarted = false;
            for (int attempt = 1; attempt <= maxRestarts && !restarted; attempt++)
            {
                log?.Invoke($"崩溃恢复：重启 llama-server（{attempt}/{maxRestarts}）…");
                RaiseStatus($"崩溃恢复：正在重启后端服务（{attempt}/{maxRestarts}）…");
                try
                {
                    await EnsureRunningAsync();
                    restarted = true;
                }
                catch (Exception ex)
                {
                    log?.Invoke($"重启失败（{attempt}/{maxRestarts}）：{ex.Message}");
                }
            }

            if (!restarted)
            {
                log?.Invoke("全部重启失败：无法自动恢复，请手动启动。");
                return;
            }

            // 重启后后端端口可能变化（自动探测空闲端口），重建 URI
            var replayUri = new Uri($"http://localhost:{_backendPort}{uri.AbsolutePath}{uri.Query}");

            // 严格预算全量重放（无快照）：重启后内存状态未知，统一收紧 25% 防同点再崩
            int budget = Math.Max(AppConfig.MinInputBudgetTokens, (int)(_cfg.GetInputBudget() * TightBudgetFactor));
            var (ok, guarded, note) = await TokenGuard.GuardAsync(_hc, _backendPort, finalBody, budget);
            if (!ok)
            {
                log?.Invoke($"重放中止：{note}（上下文无法裁剪到严格预算）。");
                return;
            }
            if (note != null) log?.Invoke(note);

            log?.Invoke("全量重放：原请求重发（严格预算，无快照）…");
            var (replayCompleted, _) = await OutputContinuer.SendAndPipeStreamAsync(_hc, replayUri, _backendPort, guarded ?? finalBody, outResp, _cfg, log, writeGate);
            if (!replayCompleted)
                log?.Invoke("重放流再次中断：本次恢复失败。");
        });

    /// <summary>探测客户端连接是否存活：立即写一行 keep-alive 注释；写失败 = 客户端已断开（分支 C）。</summary>
    private static async Task<bool> ProbeClientConnectedAsync(HttpListenerResponse outResp, SemaphoreSlim writeGate)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(": keepalive\n");
        await writeGate.WaitAsync();
        try
        {
            await outResp.OutputStream.WriteAsync(bytes);
            await outResp.OutputStream.FlushAsync();
            return true;
        }
        catch
        {
            return false; // 写入失败 = 客户端已断开
        }
        finally
        {
            writeGate.Release();
        }
    }

    /// <summary>SSE keep-alive：每 N 秒写一行注释（客户端忽略但连接不断），直到取消或客户端断开。</summary>
    private async Task RunKeepAliveAsync(HttpListenerResponse outResp, SemaphoreSlim writeGate, CancellationToken ct, Action<string>? log)
    {
        var intervalSec = Math.Max(1, _cfg.RecoveryKeepAliveIntervalSeconds);
        var bytes = System.Text.Encoding.UTF8.GetBytes(": keepalive\n");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSec), ct);
                await writeGate.WaitAsync(ct);
                try
                {
                    await outResp.OutputStream.WriteAsync(bytes);
                    await outResp.OutputStream.FlushAsync();
                }
                finally
                {
                    writeGate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止（恢复流程完成）
        }
        catch (Exception ex)
        {
            log?.Invoke($"keep-alive 停止：{ex.Message}");
        }
    }

    /// <summary>请求体 dump（应用识别分析用）：原始 body + headers 入统一日志管道 Dump 流（request_dump.log，2MB 轮切）。
    /// 时间戳由管道 Enqueue 侧统一添加（秒级精度）。</summary>
    private void DumpRequest(HttpListenerContext ctx, byte[] bodyBytes)
    {
        try
        {
            var req = ctx.Request;
            var path = req.Url?.AbsolutePath ?? "";
            var bodyStr = System.Text.Encoding.UTF8.GetString(bodyBytes);

            var headers = new StringBuilder();
            foreach (var key in req.Headers.AllKeys)
            {
                headers.AppendLine($"{key}: {req.Headers[key]}");
            }

            // 请求体截断（DumpBodyMaxLength 字符）：避免日志爆炸，system prompt 通常在前部
            if (bodyStr.Length > DumpBodyMaxLength)
                bodyStr = bodyStr.Substring(0, DumpBodyMaxLength) + $"...(truncated, total {System.Text.Encoding.UTF8.GetByteCount(bodyStr)} bytes)";

            var dumpBlock = $"POST {path}\n--- Headers ---\n{headers}--- Body ---\n{bodyStr}\n{new string('=', 80)}\n\n";
            LogFile.DumpAppend(dumpBlock); // 异步管道：请求路径零磁盘 I/O
        }
        catch { /* dump 失败不影响主流程 */ }
    }

    private int _nonStreamWarned; // 每会话只告警一次，唤醒时重置

    /// <summary>非流式推理请求告警（每会话一次）：非流式是"断开→全量重填"循环的常见诱因。</summary>
    private void WarnNonStreamOnce()
    {
        if (Interlocked.Increment(ref _nonStreamWarned) == 1)
            Log?.Invoke("警告：检测到非流式推理请求。llama-server 会阻塞整个生成后才返回，客户端读超时可能触发断开→重试全量重新预填。" +
                        "建议：Agent 侧启用流式（stream=true）或加大请求超时；也可在启动器开启「强制流式」。");
    }

    // ==================== 闲置休眠（15 分钟无请求自动释放） ====================

    /// <summary>刷新闲置倒计时基准点（Interlocked 原子写，供多线程读取）。</summary>
    private void Touch() => Interlocked.Exchange(ref _lastTouchTicks, DateTime.Now.Ticks);

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

    // ==================== 对外控制接口 ====================

    /// <summary>启动 / 唤醒按钮：立即拉起后端服务（含就绪等待）。</summary>
    public Task LaunchNowAsync() => EnsureRunningAsync();

    /// <summary>停止按钮 / 关闭前：终止进程树。</summary>
    public void StopNow()
    {
        Log?.Invoke("正在停止 llama-server…");
        SetPhase(Phase.Standby); // 先置位，Exited 回调不再重复报告
        RaiseStatus(AutoMode ? "已停止，监听待机中。" : "已停止。");
        _server.Stop();
    }

    /// <summary>智能模式开关（可实时切换）。</summary>
    public void SetAutoMode(bool on)
    {
        if (on == AutoMode) return;
        AutoMode = on;
        if (on)
        {
            if (_server.IsRunning)
            {
                Log?.Invoke("切换到智能模式：先停止当前服务。");
                StopNow();
            }
            StartListening();
            RaiseStatus($"待机 · 监听 {_cfg.Port}，等待请求唤醒。");
        }
        else
        {
            StopListening();
            if (_server.IsRunning)
            {
                Log?.Invoke("切换到手动模式：停止当前服务。");
                StopNow();
            }
            RaiseStatus("手动模式：点击「启动 / 唤醒」运行 llama-server。");
        }
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

    // ==================== 状态与工具 ====================

    private void SetPhase(Phase p)
    {
        if (CurrentPhase == p) return;
        Volatile.Write(ref _phase, (int)p);
        PhaseChanged?.Invoke(p);
    }

    private void RaiseStatus(string text) => StatusChanged?.Invoke(text);

}