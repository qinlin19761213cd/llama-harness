using System.Threading;

namespace LlamaHarness;

/// <summary>
/// 性能周期采样器（v2.21）：常驻后台定时采集，复用 SystemMetrics + LlamaCppMonitorCollector。
/// 双节奏：1s 轻量指标（CPU/内存/inflight）+ 每 5s 慢指标（显存 nvidia-smi / llama.cpp 三接口）；
/// 慢指标结果缓存，延续填充到后续 1s 点（趋势曲线连续，避免 4/5 采样点为 null）。
/// cpp 采样按后端端口门控：backendPort=0（未唤醒/手动未启动）自动跳过并复用上一次缓存。
/// 慢采集走独立异步任务（SemaphoreSlim 防重叠），不阻塞 1s 快 tick 队列。
/// 采样点写入 <see cref="Series"/>（PerfSeries 环形缓冲，线程安全）并触发 <see cref="Sampled"/> 事件。
/// </summary>
public sealed class PerfSampler : IDisposable
{
    /// <summary>轻量指标节奏（CPU/内存/inflight），毫秒。</summary>
    public const int FastIntervalMs = 1000;
    /// <summary>每 N 个快 tick 采一次慢指标（显存/cpp）。</summary>
    public const int SlowEveryTicks = 5;
    /// <summary>时间序列窗口容量：1s × 3600 ≈ 1 小时（内存恒定不增长）。</summary>
    public const int SeriesCapacity = 3600;

    private readonly Func<int> _backendPortProvider;
    private readonly Func<int> _inflightProvider;
    private readonly Func<(int Hits, int FalseMiss, int SavedN, int FullPrefill, long ReuseTokens, double ReuseSavedMs)>? _kvStatsProvider; // v2.22 KV 累积型快照源（v2.23.11 增 ROI 复用）
    private readonly Func<(int Evict, int Preempt)>? _schedStatsProvider;   // v2.22 调度累积型快照源
    private readonly Func<(long Dropped, double FlushAvgMs)>? _logStatsProvider; // v2.22 日志管道累积型快照源
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _slowGate = new(1, 1);
    private readonly SystemMetrics _metrics = new();
    private LlamaCppMonitorCollector? _monitor; // 懒建：端口有效时按当前端口创建；端口变化重建
    private int _monitorPort;                   // _monitor 绑定的后端端口
    private int _slowCounter;                   // 快 tick 计数（_gate 保护）
    private double? _vramUsedMb, _vramTotalMb;
    private double? _ppTps, _tgTps;
    private long? _tokensCached;
    private double? _ctxUsedPct;
    private int? _slotsProcessing;
    private bool _disposed;

    /// <summary>采样时间序列（1h 滑动窗口；UI/分析器通过 Snapshot/Last 读）。</summary>
    public PerfSeries<PerfPoint> Series { get; } = new(SeriesCapacity);

    /// <summary>每次采样完成触发（约 1s 一次）；供 perf.log 与实时展示订阅。</summary>
    public event Action<PerfPoint>? Sampled;

    /// <summary>最近一次采样点（UI 实时数字展示用；null = 尚未采样）。</summary>
    public PerfPoint? LastPoint { get; private set; }

    public PerfSampler(Func<int> backendPortProvider, Func<int> inflightProvider, Func<(int Hits, int FalseMiss, int SavedN, int FullPrefill, long ReuseTokens, double ReuseSavedMs)>? kvStatsProvider = null, Func<(int Evict, int Preempt)>? schedStatsProvider = null, Func<(long Dropped, double FlushAvgMs)>? logStatsProvider = null)
    {
        _backendPortProvider = backendPortProvider;
        _inflightProvider = inflightProvider;
        _kvStatsProvider = kvStatsProvider;
        _schedStatsProvider = schedStatsProvider;
        _logStatsProvider = logStatsProvider;
        _timer = new System.Threading.Timer(OnTick, null, System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
    }

    /// <summary>启动采样（立即采第一点，随后 1s 节奏）。</summary>
    public void Start() => _timer.Change(0, FastIntervalMs);

    /// <summary>停止采样（保留已有序列）。</summary>
    public void Stop() => _timer.Change(System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);

    private void OnTick(object? state)
    {
        if (_disposed) return;

        // —— 轻量指标（同步、便宜，直接采）——
        double cpu = 0, memUsed = 0, memTotal = 0;
        try
        {
            cpu = _metrics.GetCpuPercent();
            var m = _metrics.GetMemory();
            memUsed = m.usedGb;
            memTotal = m.totalGb;
        }
        catch
        {
            // 采样失败：本轮轻量指标置 0，不中断后续
        }
        int inflight = 0;
        try { inflight = _inflightProvider(); } catch { }

        // —— KV 累积型快照（v2.22）：命中 / false_miss / 最大 savedN——
        int? kvHit = null, kvFalse = null, kvSaved = null, kvFull = null; long? kvReuseTok = null; double? kvReuseMs = null;
        if (_kvStatsProvider != null)
        {
            try { var k = _kvStatsProvider(); kvHit = k.Hits; kvFalse = k.FalseMiss; kvSaved = k.SavedN; kvFull = k.FullPrefill; kvReuseTok = k.ReuseTokens; kvReuseMs = k.ReuseSavedMs; } catch { }
        }

        // —— 调度累积型快照（v2.22）：驱逐 / 强占——
        int? evict = null, preempt = null;
        if (_schedStatsProvider != null)
        {
            try { var s = _schedStatsProvider(); evict = s.Evict; preempt = s.Preempt; } catch { }
        }

        // —— 日志管道累积型快照（v2.22）：丢弃行数 / flush 平均耗时——
        long? logDropped = null; double? logFlush = null;
        if (_logStatsProvider != null)
        {
            try { var l = _logStatsProvider(); logDropped = l.Dropped; logFlush = l.FlushAvgMs; } catch { }
        }

        // —— 慢指标节奏判定 + 异步触发（不阻塞本 tick）——
        bool slow;
        lock (_gate) { _slowCounter++; slow = (_slowCounter % SlowEveryTicks) == 0; }
        if (slow) _ = SampleSlowAsync();

        // —— 组装采样点（慢指标取最近缓存值延续填充）——
        double? vramU, vramT, pp, tg; long? tok; double? ctx; int? sp;
        lock (_gate)
        {
            vramU = _vramUsedMb; vramT = _vramTotalMb; pp = _ppTps; tg = _tgTps;
            tok = _tokensCached; ctx = _ctxUsedPct; sp = _slotsProcessing;
        }

        var point = new PerfPoint
        {
            Ts = DateTime.Now,
            CpuPercent = cpu,
            MemUsedGb = memUsed,
            MemTotalGb = memTotal,
            VramUsedMb = vramU,
            VramTotalMb = vramT,
            PpTps = pp,
            TgTps = tg,
            TokensCached = tok,
            CtxUsedPct = ctx,
            SlotsProcessing = sp,
            Inflight = inflight,
            KvHitDelta = kvHit,
            KvFalseMiss = kvFalse,
            SavedN = kvSaved,
            KvFullPrefill = kvFull,
            KvReuseTokens = kvReuseTok,
            KvReuseSavedMs = kvReuseMs,
            EvictCount = evict,
            PreemptTrigger = preempt,
            LogDroppedLines = logDropped,
            LogFlushCostMs = logFlush,
        };
        Series.Add(point);
        LastPoint = point;
        Sampled?.Invoke(point);
    }

    /// <summary>慢指标异步采集：显存（nvidia-smi）+ llama.cpp 三接口；SemaphoreSlim 防上一轮未完成时重叠。</summary>
    private async Task SampleSlowAsync()
    {
        // M-07 修复：使用 acquired 标志确保仅在成功获取信号量时才 Release，防止异常时 count > 1
        bool acquired = await _slowGate.WaitAsync(0);
        if (!acquired) return; // 上一轮慢采集未完成：跳过本轮，下一轮再采
        try
        {
            // —— 显存（"used/total MB" 文本，解析出两个数值）——
            try
            {
                var s = await _metrics.GetVramTextAsync();
                if (s != null)
                {
                    var (u, tot) = ParseVramText(s);
                    if (u != null)
                    {
                        lock (_gate) { _vramUsedMb = u; _vramTotalMb = tot; }
                    }
                }
            }
            catch
            {
                // nvidia-smi 异常：保留上次缓存
            }

            // —— llama.cpp 三接口快照（按后端端口门控；端口变化重建 monitor）——
            int port = 0;
            try { port = _backendPortProvider(); } catch { }
            if (port <= 0) return; // 未唤醒/手动未启动：跳过，保留上次缓存
            var mon = GetOrCreateMonitor(port);
            if (mon == null) return;
            try
            {
                var snap = await mon.CaptureSnapshotAsync();
                var slots = snap.Slots;
                // —— b10676 兼容：新版 /slots 精简（仅 id/n_ctx/speculative/is_processing，无 tg_tps/pp_tps/tokens_cached），
                //    吞吐改从 /metrics（Prometheus）的 predicted_tokens_seconds / prompt_tokens_seconds 兜底 ——
                double? mTg = null, mPp = null;
                try { (mTg, mPp) = ParseMetricsPrometheus(snap.RawMetricsText); } catch { }
                if (slots.Count > 0)
                {
                    double ppSum = 0, tgSum = 0;
                    long tokSum = 0;
                    int proc = 0;
                    foreach (var s in slots)
                    {
                        ppSum += s.pp_tps;
                        tgSum += s.tg_tps;
                        tokSum += s.tokens_cached;
                        if (s.is_processing) proc++;
                    }
                    int n = slots.Count;
                    long ctx = snap.GlobalProps.ctx_size > 0 ? snap.GlobalProps.ctx_size : slots[0].n_ctx;
                    double ctxPct = ctx > 0 ? (double)tokSum / ctx : 0;
                    lock (_gate)
                    {
                        // 吞吐优先 /metrics（新版字段所在，含 0）；/slots 均值仅作旧版兜底
                        _ppTps = mPp ?? (n > 0 ? ppSum / n : 0);
                        _tgTps = mTg ?? (n > 0 ? tgSum / n : 0);
                        _tokensCached = tokSum;
                        _ctxUsedPct = ctxPct;
                        _slotsProcessing = proc;
                    }
                }
            }
            catch
            {
                // 后端未就绪/接口失败：保留上次缓存
            }
        }
        finally
        {
            if (acquired) _slowGate.Release();
        }
    }

    /// <summary>解析 llama.cpp /metrics（Prometheus 文本）平均吞吐：predicted_tokens_seconds → 生成 t/s、
    /// prompt_tokens_seconds → prompt t/s；b10676 版 /slots 精简（无 tg_tps/pp_tps）时的兜底来源。
    /// 缺失字段/空文本返回 (null, null)；解析异常不抛出。</summary>
    internal static (double? TgTps, double? PpTps) ParseMetricsPrometheus(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        double? tg = null, pp = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int sp = line.IndexOf(' ');
            if (sp <= 0) continue;
            var name = line.Substring(0, sp);
            var val = line.Substring(sp + 1).Trim();
            if (name == "llamacpp:predicted_tokens_seconds" &&
                double.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t))
                tg = t;
            else if (name == "llamacpp:prompt_tokens_seconds" &&
                double.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p))
                pp = p;
        }
        return (tg, pp);
    }

    /// <summary>解析显存文本 → (已用MB, 总量MB)。兼容 nvidia-smi 逗号格式（"16546, 20480"）
    /// 与 GetVramTextAsync 斜杠格式（"16546/20480 MB"）；格式不符返回 (null, null)。</summary>
    internal static (double? used, double? total) ParseVramText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        string first, second;
        int comma = text.IndexOf(',');
        int slash = text.IndexOf('/');
        if (comma > 0) { first = text.Substring(0, comma); second = text.Substring(comma + 1); }
        else if (slash > 0) { first = text.Substring(0, slash); second = text.Substring(slash + 1); }
        else return (null, null);
        if (!double.TryParse(first.Trim(), out double used)) return (null, null);
        var secondPart = second.Trim().Split(' ', 2)[0].Trim(); // "20480 MB" / "20480" → "20480"
        double? total = double.TryParse(secondPart, out double t) ? t : null;
        return (used, total);
    }

    /// <summary>按当前端口懒建 LlamaCppMonitorCollector；端口变化时释放旧实例重建。</summary>
    private LlamaCppMonitorCollector? GetOrCreateMonitor(int port)
    {
        lock (_gate)
        {
            if (_monitor != null && _monitorPort == port) return _monitor;
            _monitor?.Dispose();
            _monitor = new LlamaCppMonitorCollector($"http://127.0.0.1:{port}");
            _monitorPort = port;
            return _monitor;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
        lock (_gate)
        {
            _monitor?.Dispose();
            _monitor = null;
        }
        _slowGate.Dispose();
        _timer.Dispose();
    }
}
