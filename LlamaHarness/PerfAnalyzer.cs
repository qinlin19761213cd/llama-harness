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

/// <summary>
/// 性能分析器（v2.21 方案③双源共享分析内核）：实时（周期采样点连续窗口阈值 + 单请求时延）与离线（perf.log 解析）
/// 共用同一套指标键（<see cref="ValueOf"/>）与阈值规则语义，避免"实时图与历史分析对不上"。
/// 纯函数、无内部状态（连续窗口在 <see cref="EvaluatePoints"/> 内局部判定），可单测。
/// </summary>
public static class PerfAnalyzer
{
    /// <summary>从采样点取指定指标值（未知键返回 null，调用方跳过）。</summary>
    public static double? ValueOf(PerfPoint p, string metric) => metric switch
    {
        "cpu" => p.CpuPercent,
        "vram_mb" => p.VramUsedMb,
        "mem_gb" => p.MemUsedGb,
        "pp_tps" => p.PpTps,
        "tg_tps" => p.TgTps,
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
                var v = ValueOf(pt, rule.Metric);
                if (v == null) { runWarn = 0; runCrit = 0; continue; }
                bool overCrit = IsOver(v.Value, rule);
                if (overCrit)
                {
                    runCrit++;
                    runWarn = 0;
                    if (runCrit >= rule.MinDurationSeconds)
                    {
                        alarms.Add(MakeAlarm(pt.Ts, rule.Metric, PerfAlarmLevel.Crit, v.Value));
                        runCrit = 0;
                    }
                }
                else if (IsOverWarn(v.Value, rule))
                {
                    runWarn++;
                    runCrit = 0;
                    if (runWarn >= rule.MinDurationSeconds)
                    {
                        alarms.Add(MakeAlarm(pt.Ts, rule.Metric, PerfAlarmLevel.Warn, v.Value));
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

    /// <summary>单请求时延阈值检测（total_ms 等请求级指标；规则 MinDurationSeconds 语义 = 单次即触发）。</summary>
    public static List<PerfAlarm> EvaluateTiming(RequestTiming t, IReadOnlyList<PerfThresholdRule> rules)
    {
        var alarms = new List<PerfAlarm>();
        if (rules == null) return alarms;
        foreach (var rule in rules)
        {
            if (rule == null || !string.Equals(rule.Metric, "total_ms", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsOver(t.TotalMs, rule))
                alarms.Add(MakeAlarm(t.Ts, rule.Metric, PerfAlarmLevel.Crit, t.TotalMs));
            else if (IsOverWarn(t.TotalMs, rule))
                alarms.Add(MakeAlarm(t.Ts, rule.Metric, PerfAlarmLevel.Warn, t.TotalMs));
        }
        return alarms;
    }

    /// <summary>实时窗口趋势摘要（均值/峰值/最新）。</summary>
    public static PerfSummary ComputeSummary(IReadOnlyList<PerfPoint> points)
    {
        int n = 0;
        double cpuSum = 0, cpuMax = 0, vramSum = 0, vramMax = 0, tgSum = 0, tgMin = double.MaxValue, ctxMax = 0;
        double? lastVram = null;
        int maxInflight = 0;
        foreach (var p in points)
        {
            n++;
            if (p.CpuPercent is double c) { cpuSum += c; if (c > cpuMax) cpuMax = c; }
            if (p.VramUsedMb is double vu)
            {
                vramSum += vu;
                if (vu > vramMax) vramMax = vu;
                lastVram = vu;
            }
            if (p.TgTps is double tg) { tgSum += tg; if (tg < tgMin) tgMin = tg; }
            if (p.CtxUsedPct is double ctx && ctx > ctxMax) ctxMax = ctx;
            if (p.Inflight is int inf && inf > maxInflight) maxInflight = inf;
        }
        if (n == 0) return new PerfSummary();
        return new PerfSummary
        {
            PointCount = n,
            AvgCpu = Math.Round(cpuSum / n, 1),
            MaxCpu = Math.Round(cpuMax, 1),
            AvgVramMb = Math.Round(vramSum / n, 0),
            MaxVramMb = Math.Round(vramMax, 0),
            AvgTgTps = Math.Round(tgSum / n, 1),
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
            foreach (var line in File.ReadLines(path))
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

    // —— 内部辅助 ——

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

    private static PerfAlarm MakeAlarm(DateTime ts, string metric, PerfAlarmLevel level, double value)
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
        string dir = "超过阈值";
        return new PerfAlarm
        {
            Ts = ts,
            Metric = metric,
            Level = level,
            Value = value,
            Message = $"{name} {value:0.##} {dir}（{level}）",
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
