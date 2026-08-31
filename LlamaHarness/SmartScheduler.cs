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
public sealed partial class SmartScheduler : IDisposable
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
    private readonly InFlightTracker _inflightTracker = new(); // 在途任务明细（状态栏服务阶段卡片展示，v2.18）
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
    /// <summary>在途任务登记/移除变更（可能来自任意线程），UI 侧据此刷新服务阶段卡片明细。</summary>
    public event Action? InFlightChanged;
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

    /// <summary>获取在途任务明细快照（右侧状态栏「服务阶段」卡片展示）。空 = 无在途。</summary>
    public IReadOnlyList<InFlightTracker.InFlightTask> GetInFlightTasks() => _inflightTracker.Snapshot();

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

    /// <summary>实际运行时后端端口（智能模式探测/手动模式=_cfg.Port；唤醒后有效，未唤醒为 0）。供监控采集等使用。</summary>
    public int BackendPort => _backendPort;

    /// <summary>在途请求计数（含排队等待唤醒）；供性能采样/监控读取（v2.21）。</summary>
    public int InflightCount => Volatile.Read(ref _inflight);

    /// <summary>请求时延追踪器（v2.21 性能埋点）：四段时延 + 最近请求环形缓冲 + 会话聚合统计。</summary>
    private readonly RequestTimingTracker _timing = new();

    /// <summary>请求时延追踪器（供 perf.log / 监控页订阅 Completed 与读 Recent/Stats）。</summary>
    public RequestTimingTracker Timing => _timing;

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

    // ==================== 请求处理（排队唤醒 + 代理转发） ====================

    // ==================== KV 全场景复用辅助（§4.2/§4.5/§8） ====================

    // ==================== 崩溃自动恢复（bad_alloc） ====================

    private int _nonStreamWarned; // 每会话只告警一次，唤醒时重置

    // ==================== 闲置休眠（15 分钟无请求自动释放） ====================

    /// <summary>刷新闲置倒计时基准点（Interlocked 原子写，供多线程读取）。</summary>
    private void Touch() => Interlocked.Exchange(ref _lastTouchTicks, DateTime.Now.Ticks);

    // ==================== 对外控制接口 ====================

    /// <summary>启动 / 唤醒按钮：立即拉起后端服务（含就绪等待）。</summary>
    public Task LaunchNowAsync() => EnsureRunningAsync();

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

    // ==================== 状态与工具 ====================

    private void RaiseStatus(string text) => StatusChanged?.Invoke(text);

}
