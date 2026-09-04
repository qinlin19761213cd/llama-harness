namespace LlamaHarness;

/// <summary>性能趋势摘要（实时窗口统计，供监控页数字区展示）。</summary>
public sealed class PerfSummary
{
    public int PointCount { get; init; }
    public double? AvgCpu { get; init; }
    public double? MaxCpu { get; init; }
    public double? AvgVramMb { get; init; }
    public double? MaxVramMb { get; init; }
    public double? AvgTgTps { get; init; }
    public double? MinTgTps { get; init; }
    public double? MaxCtxPct { get; init; }
    public double? LastVramMb { get; init; }
    public int? MaxInflight { get; init; }
}

/// <summary>perf.log 离线解析摘要（跨会话对比 / 异常窗口定位）。</summary>
public sealed class PerfLogSummary
{
    public string Path { get; init; } = "";
    public int TotalLines { get; init; }
    public DateTime? FirstTs { get; init; }
    public DateTime? LastTs { get; init; }
    public int SystemCount { get; init; }
    public int CppCount { get; init; }
    public int TimingCount { get; init; }
    public long Requests { get; init; }
    public long FailedRequests { get; init; }
    public double AvgTotalMs { get; init; }
    public double MaxTotalMs { get; init; }
    public double? MaxVramMb { get; init; }
    public double? MinTgTps { get; init; }
    /// <summary>文件为空/不存在。</summary>
    public bool IsEmpty => TotalLines == 0;
    /// <summary>请求失败率（0~1；无请求为 0）。</summary>
    public double FailureRate => Requests > 0 ? (double)FailedRequests / Requests : 0;
}

/// <summary>单会话摘要（v2.22 可观测：perf.log 按 sid 聚合，跨会话/跨版本对比单元）。</summary>
public sealed class PerfSessionSummary
{
    public string Sid { get; init; } = "";
    public string? Version { get; init; }
    public DateTime? Start { get; init; }
    public DateTime? End { get; init; }
    public int KvSaveCount { get; init; }
    public int KvRestoreCount { get; init; }
    public double? AvgKvSaveMs { get; init; }
    public double? AvgKvRestoreMs { get; init; }
    public int SlotSelectCount { get; init; }
    public double? AvgSlotSelectMs { get; init; }
    public double? MaxSlotSelectMs { get; init; }
    public int WakeupCount { get; init; }
    public double? AvgWakeupMs { get; init; }
    public long KvHit { get; init; }
    public long KvFalseMiss { get; init; }
    public long SavedN { get; init; }
    public long Evict { get; init; }
    public long Preempt { get; init; }
    public long LogDropped { get; init; }
    public double? LogFlushAvgMs { get; init; }
    public long KvFullPrefill { get; init; } // v2.23.10 全量 prefill 累计
    public long KvReuseTokens { get; init; } // v2.23.11 复用 token 累计（ROI）
    public double? KvReuseSavedMs { get; init; } // v2.23.11 节省时间 ms 累计（ROI）
    public long Requests { get; init; }
    public double? AvgTotalMs { get; init; }
    public double? MinTgTps { get; init; }
    public double? MaxVramMb { get; init; }
    /// <summary>KV 命中率（hit/(hit+false_miss)；无计数为 NaN）。</summary>
    public double KvHitRate => KvHit + KvFalseMiss > 0 ? (double)KvHit / (KvHit + KvFalseMiss) : double.NaN;
}

/// <summary>退化归因项（当前会话 vs 基线会话的单项对比）。</summary>
public sealed class PerfRegressionItem
{
    public string Metric { get; init; } = "";
    public string Label { get; init; } = "";
    public double? Before { get; init; }
    public double? After { get; init; }
    /// <summary>变化百分比（正 = 上涨，负 = 下降；无基线为 null）。</summary>
    public double? DeltaPct { get; init; }
    public string? Cause { get; init; }
}

/// <summary>退化归因分析结果（当前 vs 基线）。</summary>
public sealed class PerfRegression
{
    public List<PerfRegressionItem> Items { get; } = new();
}

/// <summary>
/// 性能分析器（v2.21 方案③双源共享分析内核）：实时（周期采样点连续窗口阈值 + 单请求时延）与离线（perf.log 解析）
/// 共用同一套指标键（<see cref="ValueOf"/>）与阈值规则语义，避免"实时图与历史分析对不上"。
/// 纯函数、无内部状态（连续窗口在 <see cref="EvaluatePoints"/> 内局部判定），可单测。
/// </summary>
public static class PerfAnalyzer
{
    // —— P3-A：跨窗口冷却 + 恢复通知 ——
    // 事件宿主：PerfAnalyzer 保持 static（不破坏既有静态调用点），事件用静态字段承载。
    /// <summary>告警升级事件（None→Warn / Warn→Crit 触发）。</summary>
    public static event EventHandler<PerfAlarmEventArgs>? AlarmRaised;
    /// <summary>告警恢复事件（Warn/Crit→None 触发）。</summary>
    public static event EventHandler<PerfAlarmEventArgs>? AlarmRecovered;

    // 冷却字典与状态表用独立锁隔离：多线程调用 EvaluatePoints/EvaluateTiming 时互不干扰。
    private static readonly object _alarmLock = new();
    private static readonly Dictionary<string, DateTime> _lastAlarmUtc = new();
    private static readonly Dictionary<string, PerfAlarmLevel> _curLevel = new();

    /// <summary>同 metric 相邻告警的最小间隔（秒），避免刷屏。</summary>
    private const int AlarmCooldownSeconds = 60;

    /// <summary>分发事件（不抛异常）：订阅方异常隔离，避免影响告警主路径。</summary>
    private static void RaiseAlarm(EventHandler<PerfAlarmEventArgs>? evt, PerfAlarmEventArgs args)
    {
        try { evt?.Invoke(null, args); } catch { /* 订阅方异常隔离 */ }
    }

    /// <summary>
    /// 评估并维护告警状态机：跨窗口冷却 + None↔Warn/Crit 迁移事件。
    /// 返回 false = 冷却期内跳过（不生成告警、不改状态、不重置 run 计数语义）；返回 true = 生成告警且状态推进。
    /// 冷却期内**不重置**当前告警级别（保持状态机"仍在告警中"）。
    /// </summary>
    private static bool TryRaiseAlarm(string metric, PerfAlarmLevel newLevel, double value, double threshold, DateTime ts)
    {
        lock (_alarmLock)
        {
            // 跨窗口冷却：同 metric 相邻告警间隔 < 冷却期 → 跳过（保留 _curLevel）
            if (_lastAlarmUtc.TryGetValue(metric, out var last) && (DateTime.UtcNow - last).TotalSeconds < AlarmCooldownSeconds)
                return false;

            _lastAlarmUtc[metric] = DateTime.UtcNow;
            var prev = _curLevel.TryGetValue(metric, out var p) ? p : (PerfAlarmLevel?)null;
            _curLevel[metric] = newLevel;

            // 状态迁移事件（Raise：新级别 > 前一级别 或 从无到 Warn/Crit）
            if (prev == null || prev < newLevel)
                RaiseAlarm(AlarmRaised, new PerfAlarmEventArgs(metric, newLevel, value, threshold, ts, prev));
            // prev >= newLevel（同级别或降级）：冷却期外同级别告警也允许（连续窗口外新触发），但仍走状态机
            return true;
        }
    }

    /// <summary>
    /// 状态迁移到 None：Warn/Crit→None 时触发 AlarmRecovered。
    /// 与告警冷却解耦——恢复事件按真实迁移即刻通知，不受 60s 冷却门限影响（避免"恢复事件被延迟 60s"）。
    /// 只有当前确实处于告警态（_curLevel 已设）时才触发，避免离线重放或多次扫描重复发恢复事件。
    /// </summary>
    private static void OnRecover(string metric, double value, DateTime ts, PerfThresholdRule rule)
    {
        lock (_alarmLock)
        {
            if (!_curLevel.TryGetValue(metric, out var prev)) return;
            _curLevel.Remove(metric);
            // 恢复阈值参考：方向为 Above 时用 WarnValue（越过即恢复）；Below 时同样用 WarnValue
            double threshold = rule.WarnValue;
            RaiseAlarm(AlarmRecovered, new PerfAlarmEventArgs(metric, prev, value, threshold, ts, null));
        }
    }

    /// <summary>从采样点取指定指标值（未知键返回 null，调用方跳过）。
    /// D2 修复：为兼容旧命名，每个指标同时支持短名与长名两种键（长短名返回同一值）。</summary>
    public static double? ValueOf(PerfPoint p, string metric) => metric switch
    {
        "cpu" or "cpu_percent" => p.CpuPercent,
        "vram_mb" or "vram_used_mb" => p.VramUsedMb,
        "vram_total_mb" => p.VramTotalMb,
        "mem_gb" => p.MemUsedGb,
        "pp_tps" or "prompt_tokens_per_second" => p.PpTps,
        "tg_tps" or "tokens_per_second" => p.TgTps,
        "tok" => p.TokensCached,
        "ctx" => p.CtxUsedPct,
        "slots" => p.SlotsProcessing,
        "inflight" => p.Inflight,
        _ => null,
    };

    /// <summary>
    /// 周期采样点连续窗口阈值检测：对每个规则独立扫描（时间升序），
    /// 连续 MinDurationSeconds 个点越过 Warn/Crit 触发一次告警（触发后该连续段复位，防重复刷屏）。
    /// 空值打断连续计数（该秒无该指标不视为越过）。
    /// </summary>
    public static List<PerfAlarm> EvaluatePoints(IReadOnlyList<PerfPoint> points, IReadOnlyList<PerfThresholdRule> rules)
    {
        var alarms = new List<PerfAlarm>();
        if (rules == null) return alarms;
        foreach (var rule in rules)
        {
            if (rule == null || rule.MinDurationSeconds < 1) continue;
            int runWarn = 0, runCrit = 0;
            foreach (var pt in points)
            {
                // 吞吐类规则负载门控：无在途请求且无处理槽的空闲点跳过（不累加不重置）——
                // 空闲 0 吞吐非异常，避免每 5 分钟误报；负载期连续低吞吐仍触发
                if ((rule.Metric == "tg_tps" || rule.Metric == "pp_tps") && IsIdlePoint(pt)) continue;
                var v = ValueOf(pt, rule.Metric);
                if (v == null) { runWarn = 0; runCrit = 0; continue; }
                bool overCrit = IsOver(v.Value, rule);
                if (overCrit)
                {
                    runCrit++;
                    runWarn = 0;
                    if (runCrit >= rule.MinDurationSeconds)
                    {
                        alarms.Add(MakeAlarm(pt.Ts, rule.Metric, rule, PerfAlarmLevel.Crit, v.Value));
                        runCrit = 0;
                    }
                }
                else if (IsOverWarn(v.Value, rule))
                {
                    runWarn++;
                    runCrit = 0;
                    if (runWarn >= rule.MinDurationSeconds)
                    {
                        alarms.Add(MakeAlarm(pt.Ts, rule.Metric, rule, PerfAlarmLevel.Warn, v.Value));
                        runWarn = 0;
                    }
                }
                else
                {
                    runWarn = 0;
                    runCrit = 0;
                }
            }
        }
        return alarms;
    }

    /// <summary>采样点是否空闲（无在途请求且无处理中槽位）——吞吐类告警（tg_tps/pp_tps）的负载门控。</summary>
    private static bool IsIdlePoint(PerfPoint pt) =>
        (pt.Inflight == null || pt.Inflight == 0) && (pt.SlotsProcessing == null || pt.SlotsProcessing == 0);
    /// <summary>单请求时延阈值检测（total_ms 等请求级指标；规则 MinDurationSeconds 语义 = 单次即触发）。</summary>
    public static List<PerfAlarm> EvaluateTiming(RequestTiming t, IReadOnlyList<PerfThresholdRule> rules)
    {
        var alarms = new List<PerfAlarm>();
        if (rules == null) return alarms;
        foreach (var rule in rules)
        {
            if (rule == null || !string.Equals(rule.Metric, "total_ms", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsOver(t.TotalMs, rule))
                alarms.Add(MakeAlarm(t.Ts, rule.Metric, rule, PerfAlarmLevel.Crit, t.TotalMs));
            else if (IsOverWarn(t.TotalMs, rule))
                alarms.Add(MakeAlarm(t.Ts, rule.Metric, rule, PerfAlarmLevel.Warn, t.TotalMs));
        }
        return alarms;
    }

    /// <summary>实时窗口趋势摘要（均值/峰值/最新）。</summary>
    public static PerfSummary ComputeSummary(IReadOnlyList<PerfPoint> points)
    {
        int nCpu = 0, nVram = 0, nTg = 0;
        double cpuSum = 0, cpuMax = 0, vramSum = 0, vramMax = 0, tgSum = 0, tgMin = double.MaxValue, ctxMax = 0;
        double? lastVram = null;
        int maxInflight = 0;
        foreach (var p in points)
        {
            if (p.CpuPercent is double c) { nCpu++; cpuSum += c; if (c > cpuMax) cpuMax = c; }
            if (p.VramUsedMb is double vu)
            {
                nVram++;
                vramSum += vu;
                if (vu > vramMax) vramMax = vu;
                lastVram = vu;
            }
            if (p.TgTps is double tg) { nTg++; tgSum += tg; if (tg < tgMin) tgMin = tg; }
            if (p.CtxUsedPct is double ctx && ctx > ctxMax) ctxMax = ctx;
            if (p.Inflight is int inf && inf > maxInflight) maxInflight = inf;
        }
        int n = points.Count;
        if (n == 0) return new PerfSummary();
        // P3 修复：均值分母改为该指标的非空点数，避免含 null 点稀释分母导致均值失真
        return new PerfSummary
        {
            PointCount = n,
            AvgCpu = nCpu > 0 ? Math.Round(cpuSum / nCpu, 1) : (double?)null,
            MaxCpu = nCpu > 0 ? Math.Round(cpuMax, 1) : (double?)null,
            AvgVramMb = nVram > 0 ? Math.Round(vramSum / nVram, 0) : (double?)null,
            MaxVramMb = nVram > 0 ? Math.Round(vramMax, 0) : (double?)null,
            AvgTgTps = nTg > 0 ? Math.Round(tgSum / nTg, 1) : (double?)null,
            MinTgTps = tgMin == double.MaxValue ? null : Math.Round(tgMin, 1),
            MaxCtxPct = Math.Round(ctxMax, 3),
            LastVramMb = lastVram is double lv ? Math.Round(lv, 0) : null,
            MaxInflight = maxInflight,
        };
    }

    /// <summary>
    /// 解析 perf.log（离线源）：逐行读取，统计三类记录数量、时间范围、请求聚合（成功/失败/时延）、
    /// 峰值显存与最低生成吞吐。格式容错：非法行跳过、缺失字段按空处理（尽力而为不抛）。
    /// </summary>
    public static PerfLogSummary ParsePerfLog(string path)
    {
        var empty = new PerfLogSummary { Path = path };
        if (!File.Exists(path)) return empty;
        int total = 0, sys = 0, cpp = 0, timing = 0;
        long req = 0, failed = 0;
        double sumTotal = 0, maxTotal = 0;
        double? maxVram = null, minTg = null;
        DateTime? first = null, last = null;
        try
        {
            foreach (var line in ReadLinesShared(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                total++;
                var parts = line.Split(',');
                if (parts.Length < 2) continue;
                DateTime? ts = DateTime.TryParse(parts[1], out var t) ? t : null;
                first ??= ts;
                if (ts != null) last = ts;
                var kv = ParseKeyValues(parts, 2);
                switch (parts[0])
                {
                    case "system":
                        sys++;
                        var vu = GetD(kv, "vram");
                        if (vu != null && (maxVram == null || vu > maxVram)) maxVram = vu;
                        break;
                    case "cpp":
                        cpp++;
                        var tg = GetD(kv, "tg_tps");
                        if (tg != null && (minTg == null || tg < minTg)) minTg = tg;
                        break;
                    case "timing":
                        timing++;
                        req++;
                        if (GetD(kv, "success") == 0) failed++;
                        var tm = GetD(kv, "total") ?? 0;
                        sumTotal += tm;
                        if (tm > maxTotal) maxTotal = tm;
                        break;
                }
            }
        }
        catch
        {
            // 文件被占用/读取中断：返回已解析部分
        }
        return new PerfLogSummary
        {
            Path = path,
            TotalLines = total,
            FirstTs = first,
            LastTs = last,
            SystemCount = sys,
            CppCount = cpp,
            TimingCount = timing,
            Requests = req,
            FailedRequests = failed,
            AvgTotalMs = req > 0 ? Math.Round(sumTotal / req, 1) : 0,
            MaxTotalMs = Math.Round(maxTotal, 1),
            MaxVramMb = maxVram is double mv ? Math.Round(mv, 0) : null,
            MinTgTps = minTg is double mt ? Math.Round(mt, 1) : null,
        };
    }

    /// <summary>
    /// 解析 perf.log 为会话分组（v2.22）：按 session 边界（sid）聚合，每个会话输出统计摘要。
    /// count 行取会话内最后一行（累积终值，分析端按相邻 count 行差分得增量）；事件行（kv/sched）聚合次数与均值。
    /// 会话内行解析与 <see cref="ParsePerfLog"/> 同源（ParseKeyValues/GetD 共用）。格式容错：非法行跳过不抛。
    /// </summary>
    public static List<PerfSessionSummary> ParseSessions(string path)
    {
        var sessions = new List<PerfSessionSummary>();
        var sb = new SessionBuilder();
        if (!File.Exists(path)) return sessions;
        try
        {
            foreach (var line in ReadLinesShared(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(',');
                if (parts.Length < 2) continue;
                var kv = ParseKeyValues(parts, 2);
                switch (parts[0])
                {
                    case "session":
                        if (GetS(kv, "type") == "start") { sb = new SessionBuilder { Sid = GetS(kv, "sid") ?? "", Version = GetS(kv, "ver"), Start = ParseTs(parts[1]) }; }
                        else if (GetS(kv, "type") == "end")
                        {
                            sb.End = ParseTs(parts[1]);
                            sessions.Add(sb.Build());
                            sb = new SessionBuilder();
                        }
                        break;
                    case "kv":
                        sb.AddKvEvent(GetS(kv, "op"), GetD(kv, "ms"));
                        break;
                    case "sched":
                        sb.AddSchedEvent(GetS(kv, "op"), GetD(kv, "ms"));
                        break;
                    case "count":
                        sb.SetCounts(GetL(kv, "kv_hit"), GetL(kv, "kv_false"), GetL(kv, "saved_n"),
                            GetL(kv, "evict"), GetL(kv, "preempt"), GetL(kv, "log_dropped"), GetD(kv, "log_flush"),
                            GetL(kv, "kv_full"), GetL(kv, "kv_reuse_tok"), GetD(kv, "kv_reuse_ms")); // v2.23.11 ROI 复用
                        break;
                    case "timing":
                        sb.Requests++;
                        sb.TotalSum += GetD(kv, "total") ?? 0;
                        break;
                    case "cpp":
                        var tg = GetD(kv, "tg_tps");
                        if (tg != null && (sb.MinTg == null || tg < sb.MinTg)) sb.MinTg = tg;
                        break;
                    case "system":
                        var vu = GetD(kv, "vram");
                        if (vu != null && (sb.MaxVram == null || vu > sb.MaxVram)) sb.MaxVram = vu;
                        break;
                }
            }
        }
        catch { /* 尽力而为 */ }
        // 文件末未闭合会话：强制收尾
        if (sb.Sid.Length > 0) sessions.Add(sb.Build());
        return sessions;
    }

    /// <summary>退化归因：当前会话 vs 基线会话，找出显著劣化（≥10% 相对变化）的指标并给出归因提示。</summary>
    public static PerfRegression CompareSessions(PerfSessionSummary? baseline, PerfSessionSummary current)
    {
        var r = new PerfRegression();
        if (baseline == null || current == null) return r;
        AddReg(r, "avg_total_ms", "请求平均总时延", baseline.AvgTotalMs, current.AvgTotalMs, above: true,
            cause: current.AvgTotalMs > baseline.AvgTotalMs && baseline.AvgTotalMs > 0 ? InferTotalMsCause(baseline, current) : null);
        AddReg(r, "min_tg_tps", "生成吞吐(最低)", baseline.MinTgTps, current.MinTgTps, above: false, cause: "推理吞吐下降 → prefill/generate 变慢或后端排队");
        AddReg(r, "kv_hit_rate", "KV 命中率", RateOrNull(baseline), RateOrNull(current), above: false, cause: "KV 命中下降 → 前缀变更频繁或驱逐未保存快照");
        AddReg(r, "avg_slot_select_ms", "槽选择耗时均值", baseline.AvgSlotSelectMs, current.AvgSlotSelectMs, above: true, cause: "槽选择变慢 → 全槽强占排队或并发请求超槽位");
        AddReg(r, "avg_kv_restore_ms", "KV 恢复耗时均值", baseline.AvgKvRestoreMs, current.AvgKvRestoreMs, above: true, cause: "KV 恢复变慢 → restore 命中链长或磁盘快照读取慢");
        AddReg(r, "log_dropped", "日志丢弃累计", baseline.LogDropped, current.LogDropped, above: true, cause: "日志队列溢出 → 写盘压力大或突发高日志量");
        return r;
    }

    // —— 内部辅助 ——

    /// <summary>以 FileShare.ReadWrite 逐行读取（兼容运行中 PerfLog 写线程的独占写句柄）。
    /// 故障实证（v2.23.1）：File.ReadLines 默认 FileShare.Read，与持有 FileAccess.Write 的 perf.log 写句柄
    /// 冲突抛 IOException → 被调用方 catch 吞掉 → TotalLines=0 → 误报"perf.log 为空或不存在"（文件实际存在且非空）。</summary>
    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        while (!sr.EndOfStream)
        {
            var line = sr.ReadLine();
            if (line != null) yield return line;
        }
    }

    /// <summary>会话内聚合构建器（ParseSessions 内部状态）。</summary>
    private sealed class SessionBuilder
    {
        public string Sid = "";
        public string? Version;
        public DateTime? Start, End;
        public int KvSaveCount, KvRestoreCount;
        public double KvSaveSum, KvRestoreSum;
        public int SlotSelectCount, WakeupCount;
        public double SlotSelectSum, SlotSelectMax, WakeupSum;
        public long KvHit, KvFalseMiss, SavedN, Evict, Preempt, LogDropped, Requests, KvFullPrefill, KvReuseTokens;
public double KvReuseMs;
        public double TotalSum;
        public double? LogFlushAvg, MinTg, MaxVram;
        public void AddKvEvent(string? op, double? ms)
        {
            if (ms == null) return;
            if (op == "save") { KvSaveCount++; KvSaveSum += ms.Value; }
            else if (op == "restore") { KvRestoreCount++; KvRestoreSum += ms.Value; }
        }
        public void AddSchedEvent(string? op, double? ms)
        {
            if (ms == null) return;
            if (op == "slot_select") { SlotSelectCount++; SlotSelectSum += ms.Value; if (ms.Value > SlotSelectMax) SlotSelectMax = ms.Value; }
            else if (op == "wakeup") { WakeupCount++; WakeupSum += ms.Value; }
        }
        public void SetCounts(long? kvHit, long? kvFalse, long? savedN, long? evict, long? preempt, long? logDropped, double? logFlush, long? kvFull = null, long? kvReuseTok = null, double? kvReuseMs = null)
        {
            if (kvHit != null) KvHit = kvHit.Value;
            if (kvFalse != null) KvFalseMiss = kvFalse.Value;
            if (savedN != null) SavedN = savedN.Value;
            if (evict != null) Evict = evict.Value;
            if (preempt != null) Preempt = preempt.Value;
            if (logDropped != null) LogDropped = logDropped.Value;
            if (logFlush != null) LogFlushAvg = logFlush.Value;
            if (kvFull != null) KvFullPrefill = kvFull.Value; // v2.23.10
            if (kvReuseTok != null) KvReuseTokens = kvReuseTok.Value; // v2.23.11
            if (kvReuseMs != null) KvReuseMs = kvReuseMs.Value; // v2.23.11
        }
        public PerfSessionSummary Build() => new()
        {
            Sid = Sid, Version = Version, Start = Start, End = End,
            KvSaveCount = KvSaveCount, KvRestoreCount = KvRestoreCount,
            AvgKvSaveMs = KvSaveCount > 0 ? Math.Round(KvSaveSum / KvSaveCount, 1) : null,
            AvgKvRestoreMs = KvRestoreCount > 0 ? Math.Round(KvRestoreSum / KvRestoreCount, 1) : null,
            SlotSelectCount = SlotSelectCount,
            AvgSlotSelectMs = SlotSelectCount > 0 ? Math.Round(SlotSelectSum / SlotSelectCount, 1) : null,
            MaxSlotSelectMs = SlotSelectCount > 0 ? Math.Round(SlotSelectMax, 1) : null,
            WakeupCount = WakeupCount,
            AvgWakeupMs = WakeupCount > 0 ? Math.Round(WakeupSum / WakeupCount, 1) : null,
            KvHit = KvHit, KvFalseMiss = KvFalseMiss, SavedN = SavedN,
            Evict = Evict, Preempt = Preempt, LogDropped = LogDropped, LogFlushAvgMs = LogFlushAvg, KvFullPrefill = KvFullPrefill,
            KvReuseTokens = KvReuseTokens, KvReuseSavedMs = KvReuseMs,
            Requests = Requests, AvgTotalMs = Requests > 0 ? Math.Round(TotalSum / Requests, 1) : null,
            MinTgTps = MinTg is double mt ? Math.Round(mt, 1) : null,
            MaxVramMb = MaxVram is double mv ? Math.Round(mv, 0) : null,
        };
    }

    private static double? RateOrNull(PerfSessionSummary s) => double.IsNaN(s.KvHitRate) ? null : s.KvHitRate;

    private static void AddReg(PerfRegression r, string metric, string label, double? before, double? after, bool above, string? cause)
    {
        if (before == null || after == null || before == 0) return;
        double deltaPct = (after.Value - before.Value) / Math.Abs(before.Value) * 100;
        bool worse = above ? deltaPct >= 10 : deltaPct <= -10;
        if (!worse) return;
        r.Items.Add(new PerfRegressionItem
        {
            Metric = metric, Label = label, Before = Math.Round(before.Value, 2),
            After = Math.Round(after.Value, 2), DeltaPct = Math.Round(deltaPct, 1), Cause = cause,
        });
    }

    /// <summary>总时延劣化归因：尝试分解为调度/KV/推理环节（按各自均值占比）。</summary>
    private static string? InferTotalMsCause(PerfSessionSummary baseline, PerfSessionSummary current)
    {
        var parts = new List<string>();
        if (current.MinTgTps != null && baseline.MinTgTps != null && current.MinTgTps < baseline.MinTgTps * 0.9)
            parts.Add("推理吞吐下降");
        if (current.AvgSlotSelectMs != null && baseline.AvgSlotSelectMs != null && current.AvgSlotSelectMs > baseline.AvgSlotSelectMs * 1.1)
            parts.Add("槽选择排队变长");
        if (current.AvgKvRestoreMs != null && baseline.AvgKvRestoreMs != null && current.AvgKvRestoreMs > baseline.AvgKvRestoreMs * 1.1)
            parts.Add("KV 恢复变慢");
        return parts.Count > 0 ? string.Join(" + ", parts) : null;
    }

    private static DateTime? ParseTs(string s) => DateTime.TryParse(s, out var t) ? t : null;
    private static string? GetS(Dictionary<string, string> kv, string key)
        => kv.TryGetValue(key, out var s) ? s : null;
    private static long? GetL(Dictionary<string, string> kv, string key)
        => kv.TryGetValue(key, out var s) && long.TryParse(s, out var v) ? v : null;

    private static bool IsOver(double v, PerfThresholdRule rule) => rule.Direction switch
    {
        PerfThresholdDirection.Above => v > rule.CritValue,
        _ => v < rule.CritValue,
    };

    private static bool IsOverWarn(double v, PerfThresholdRule rule) => rule.Direction switch
    {
        PerfThresholdDirection.Above => v > rule.WarnValue,
        _ => v < rule.WarnValue,
    };

    /// <summary>触发该级别的判定阈值（Warn 取 WarnValue，Crit 取 CritValue）。</summary>
    private static PerfAlarm MakeAlarm(DateTime ts, string metric, PerfThresholdRule rule, PerfAlarmLevel level, double value)
    {
        string name = metric switch
        {
            "cpu" => "CPU 占用",
            "vram_mb" => "显存占用",
            "mem_gb" => "内存占用",
            "tg_tps" => "生成吞吐",
            "pp_tps" => "处理吞吐",
            "ctx" => "KV 上下文占用",
            "total_ms" => "请求总时延",
            _ => metric,
        };
        double threshold = level == PerfAlarmLevel.Warn ? rule.WarnValue : rule.CritValue;
        string dir = rule.Direction == PerfThresholdDirection.Above ? "超过" : "低于";
        return new PerfAlarm
        {
            Ts = ts,
            Metric = metric,
            Level = level,
            Value = value,
            Threshold = threshold,
            Message = $"{name} {value:0.##} {dir}阈值 {threshold:0.##}（{level}）",
        };
    }

    /// <summary>解析 "key=value" 段（从索引 start 起）为字典（逗号已由 PerfLog 转义，值内无逗号）。</summary>
    private static Dictionary<string, string> ParseKeyValues(string[] parts, int start)
    {
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = start; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=');
            if (eq <= 0) continue;
            kv[parts[i].Substring(0, eq)] = parts[i].Substring(eq + 1);
        }
        return kv;
    }

    private static double? GetD(Dictionary<string, string> kv, string key)
        => kv.TryGetValue(key, out var s) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
}
