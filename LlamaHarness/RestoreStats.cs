using System.Text.Json;

namespace LlamaHarness;

/// <summary>
/// Restore 命中率可观测（3.1）：以 llama-server print_timing 的 prompt eval tokens 作为命中判定唯一真值源，
/// 与 wrapper 轻量指纹（前缀哈希）对照计算误报率。
/// - FIFO 归属：请求侧入队 (key, slot, wrapperHit, savedN)，收到 prompt eval 行时弹最旧条目判定（TTL 防错位）。
/// - 四象限判定：≤4096 → 命中 (HitByDelta)；≥全量 50% → 未命中 (FullPrefill)；中间态保守算未命中 (MidRange)。
///   全量估计 = 快照 token 数 savedN 作尺度参照（全量 ≥ savedN；savedN 未知时退化为 eval 值本身 → 保守 miss）。
/// - 持久化：config/restore_stats.json（原子写：临时文件 + rename；节流自动保存 + 休眠/退出显式 Save）。
/// - 告警：总命中率 &lt;80% 黄色预警 / &lt;50% 红色告警（状态迁移触发不重复告警，≥5 样本才评估）。
/// </summary>
public sealed class RestoreStats
{
    /// <summary>命中阈值：prompt eval tokens ≤ 此值 → restore 命中（增量 prefill）。</summary>
    public const int HitThresholdTokens = 4096;

    /// <summary>告警最小样本数（防启动初期单次 miss 误报）。</summary>
    public const int MinSamplesForAlert = 5;

    /// <summary>前缀漂移告警阈值：某 key 存在快照（savedN>0）却连续 N 次全量 prefill → 判定前缀漂移（系统提示词/tools 组装不稳定或 TokenGuard 裁剪变化，KV 增量复用失效）。v2.23.10。</summary>
    public const int DriftChainThreshold = 3;

    /// <summary>[P1-M13] _pending 队列容量上限（默认 256）——后端长时间不输出 prompt eval 时，RecordRequest 持续入队；
    /// 超限后丢弃最旧条目（Dequeue），保证判定正确性优先。</summary>
    public const int MaxPendingQueueCapacity = 256;

    private readonly object _gate = new();
    private readonly string _statsPath;
    private readonly Queue<Pending> _pending = new();
    private readonly Dictionary<string, KeyStats> _byKey = new(StringComparer.OrdinalIgnoreCase);

    private int _totalAttempts, _totalHits, _totalFalseMiss, _totalFalseHit;
    private int _totalFullPrefill; // 全量 prefill 累计（kv_full_prefill 累积型指标源）
    private long _reuseTokens;   // KV 复用累计 token 数（HitByDelta 判定时 saved_n 累计，v2.23.11 ROI）
    private double _reuseSavedMs; // KV 复用累计节省的 prefill 时间 ms（saved_n/参考tps 折算，v2.23.11 ROI）
    private readonly Dictionary<string, double> _refPrefillTpsBy = new(StringComparer.OrdinalIgnoreCase); // key → 全量 prefill 参考吞吐（v2.23.11 ROI 折算基准）
    private int _maxSavedN; // 会话最大 token 偏移（可观测 kv 累积型指标 saved_n 源）
    private readonly HashSet<string> _driftAlertedKeys = new(StringComparer.OrdinalIgnoreCase); // 已告警漂移 key（链归零时移除，允许再次告警）
    private int _driftAlertCount; // 前缀漂移告警累计次数（状态栏展示）
    private AlertLevel _lastAlert = AlertLevel.None;
    private DateTime _lastSaveAt = DateTime.MinValue;
    private bool _dirty;
    private LastJudge? _lastJudge;

    /// <summary>最近一次判定结果（供 SmartScheduler 在请求侧同步读取，区分 HitByDelta 虚假 MISS vs 真实 MISS）。
    /// 线程安全：volatile 引用赋值原子；record 不可变。</summary>
    public volatile JudgeResult? LastJudgeResult;

    /// <summary>FIFO 条目 TTL：超时视为错位丢弃（如非判定上下文任务的 print_timing、预热 dummy 请求）。</summary>
    public TimeSpan PendingTtl { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>告警级别。</summary>
    public enum AlertLevel { None, Yellow, Red }

    /// <summary>单次判定结果（供 [KV-RESTORE-JUDGE] 日志与 UI 展示）。Alert=None 表示本次未触发新告警。</summary>
    public sealed record JudgeResult(
        string Key, int Slot, bool Hit, string Reason,
        int PromptEvalTokens, int SavedN, bool WrapperHit,
        bool FalseMiss, bool FalseHit, double HitRate, AlertLevel Alert,
        bool DriftAlert);

    /// <summary>最近一次判定明细（UI「最近一次明细」数据源）。</summary>
    public sealed record LastJudge(string Key, bool Hit, string Reason, int PromptEvalTokens, int SavedN, bool WrapperHit, DateTime Time);

    internal sealed class Pending
    {
        public string Key = "";
        public int Slot;
        public bool WrapperHit;
        public int SavedN;
        public DateTime EnqueuedAt;
    }

    internal sealed class KeyStats
    {
        public int Attempts, Hits, FalseMiss, FalseHit;
        public long PromptEvalSum;
        public int PromptEvalCount;
        public int FullPrefillCount;  // 该 key 全量 prefill 累计次数
        public int FullPrefillChain;  // 连续全量 prefill 链（漂移检测，savedN>0 才累计）
    }

    /// <summary>静态正则：prompt eval time = X ms / N tokens（与 LlamaStatsParser.PromptRe 同口径）。</summary>
    private static readonly System.Text.RegularExpressions.Regex PromptEvalRe =
        new(@"prompt eval time\s*=\s*([\d.]+)\s*ms\s*/\s*(\d+)\s*tokens", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex PrefillTpsRe = // v2.23.11 ROI：llama.cpp 两种 prefill 吞吐行格式
        new(@"([\d.]+)\s*tokens per second", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex PerTokenMsRe =
        new(@"([\d.]+)\s*ms/token", System.Text.RegularExpressions.RegexOptions.Compiled);

    public RestoreStats(string? statsPath = null)
    {
        _statsPath = string.IsNullOrEmpty(statsPath)
            ? AppPaths.RestoreStatsJson
            : statsPath!;
        Load();
    }

    /// <summary>四象限判定（static 供测试）：prompt eval tokens vs 快照 token 数（全量代理）。</summary>
    public static (bool Hit, string Reason) Judge(int promptEvalTokens, int savedN)
    {
        if (promptEvalTokens <= HitThresholdTokens) return (true, "HitByDelta");
        // 全量估计：快照 token 数为尺度参照（全量 ≥ savedN；savedN 未知时退化为 eval 值本身 → 恒为 miss）
        int fullEstimate = Math.Max(savedN, promptEvalTokens);
        if (promptEvalTokens * 2 >= fullEstimate) return (false, "FullPrefill");
        return (false, "MidRange"); // 中间态：保守算未命中
    }

    /// <summary>从 llama-server 输出行解析 prompt eval tokens（mini 状态机入口）。</summary>
    public static bool TryParsePromptEvalTokens(string line, out int tokens)
    {
        tokens = 0;
        var m = PromptEvalRe.Match(line);
        return m.Success && int.TryParse(m.Groups[2].Value, out tokens);
    }
    /// <summary>扩展解析（v2.23.11）：prompt eval tokens + 耗时 ms + prefill 吞吐 t/s（ROI 量化数据源，防重复正则匹配）。
    /// 吞吐支持两种 llama.cpp 行格式："( 1.04 ms per token, 961.60 tokens per second)" 与 "( 5.324 ms/token)"。</summary>
    public static bool TryParsePromptEvalLine(string line, out int tokens, out double prefillMs, out double tps)
    {
        tokens = 0; prefillMs = 0; tps = 0;
        var m = PromptEvalRe.Match(line);
        if (!m.Success) return false;
        int.TryParse(m.Groups[2].Value, out tokens);
        double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out prefillMs);
        var t = PrefillTpsRe.Match(line);
        if (t.Success) { double.TryParse(t.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out tps); }
        else if (PerTokenMsRe.Match(line) is { Success: true } tp
                 && double.TryParse(tp.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mpt) && mpt > 0)
            tps = 1000.0 / mpt;
        return tokens > 0;
    }

    /// <summary>请求侧：入队判定上下文（路由完成且该 key 存在快照时调用）。</summary>
    public void RecordRequest(string key, int slot, bool wrapperHit, int savedN)
    {
        lock (_gate)
        {
            if (savedN > _maxSavedN) _maxSavedN = savedN;
            // [P1-M13] 容量保护：队列超限时丢弃最旧条目，防止长期运行内存缓慢累积
            while (_pending.Count >= MaxPendingQueueCapacity)
                _pending.Dequeue();
            _pending.Enqueue(new Pending { Key = key, Slot = slot, WrapperHit = wrapperHit, SavedN = savedN, EnqueuedAt = DateTime.Now });
        }
    }

    /// <summary>输出侧：收到 prompt eval 行时弹最旧条目并判定。无判定上下文（如非亲和 key 任务）返回 null。
    /// v2.23.11 增 prefill 吞吐 tps 参数（ROI 量化：hit 时按 saved_n/tps 折算节省时间）。</summary>
    public JudgeResult? OnPromptEval(int tokens, double tps = 0)
    {
        Pending p;
        lock (_gate)
        {
            var now = DateTime.Now;
            while (_pending.Count > 0 && now - _pending.Peek().EnqueuedAt > PendingTtl)
                _pending.Dequeue(); // TTL 防错位：丢弃过期条目
            if (_pending.Count == 0) return null;
            p = _pending.Dequeue();
        }

        var (hit, reason) = Judge(tokens, p.SavedN);
        bool falseMiss = hit && !p.WrapperHit;   // wrapper 报 MISS + 实际命中
        bool falseHit = !hit && p.WrapperHit;    // wrapper 报 HIT + 实际未命中

        double rate;
        AlertLevel alertRaised;
        bool driftRaised;
        lock (_gate)
        {
            _totalAttempts++;
            if (hit)
            {
                _totalHits++;
                // v2.23.11 ROI 量化：命中时 saved_n 即本次复用的 token 数；节省时间用「全量 prefill 参考吞吐」折算
                // （增量小批 tps 因 token 少达不到批量并行吞吐，直接折算会高估 10~100 倍——实测 4 token 12.64tps vs 全量 961tps）
                _reuseTokens += p.SavedN;
                if (_refPrefillTpsBy.TryGetValue(p.Key, out var tpsVal) && tpsVal > 0)
                    _reuseSavedMs += p.SavedN / tpsVal * 1000.0;
            }
            else if (reason == "FullPrefill" && tps > 0)
            {
                _refPrefillTpsBy[p.Key] = tps; // 全量 prefill 吞吐 = 真实参考（该 key 完整 prefill 的批量吞吐）
            }
            if (falseMiss) _totalFalseMiss++;
            if (falseHit) _totalFalseHit++;
            if (!_byKey.TryGetValue(p.Key, out var ks))
                _byKey[p.Key] = ks = new KeyStats();
            ks.Attempts++;
            if (hit) ks.Hits++;
            if (falseMiss) ks.FalseMiss++;
            if (falseHit) ks.FalseHit++;
            ks.PromptEvalSum += tokens;
            ks.PromptEvalCount++;
            _lastJudge = new LastJudge(p.Key, hit, reason, tokens, p.SavedN, p.WrapperHit, DateTime.Now);
            _dirty = true;

            // —— 前缀漂移检测（v2.23.10）：存在快照（savedN>0）仍全量 prefill = 前缀漂移候选；连续 DriftChainThreshold 次告警 ——
            driftRaised = false;
            if (hit)
            {
                ks.FullPrefillChain = 0; // 命中（增量）打断链；前缀恢复稳定
                _driftAlertedKeys.Remove(p.Key);
            }
            else if (reason == "FullPrefill")
            {
                _totalFullPrefill++;
                ks.FullPrefillCount++;
                if (p.SavedN > 0)
                {
                    ks.FullPrefillChain++;
                    if (ks.FullPrefillChain >= DriftChainThreshold && !_driftAlertedKeys.Contains(p.Key))
                    {
                        _driftAlertedKeys.Add(p.Key);
                        _driftAlertCount++;
                        driftRaised = true;
                    }
                }
                else ks.FullPrefillChain = 0; // 无快照全量 = 正常首存档/无缓存，不算漂移
            }
            else
            {
                ks.FullPrefillChain = 0; // MidRange 中间态：打断链
            }

            rate = (double)_totalHits / _totalAttempts;
            var level = _totalAttempts >= MinSamplesForAlert ? ComputeAlertLevel(rate) : AlertLevel.None;
            alertRaised = level != _lastAlert ? level : AlertLevel.None; // 状态迁移触发：同级别不重复告警
            _lastAlert = level;
            LastJudgeResult = new JudgeResult(p.Key, p.Slot, hit, reason, tokens, p.SavedN, p.WrapperHit, falseMiss, falseHit, rate, alertRaised, driftRaised);
        }

        TryAutoSave(); // 节流持久化（≥10s 一次）
        return new JudgeResult(p.Key, p.Slot, hit, reason, tokens, p.SavedN, p.WrapperHit, falseMiss, falseHit, rate, alertRaised, driftRaised);
    }

    /// <summary>UI/对账快照：总命中率、误报率、单 key 明细、最近一次判定。</summary>
    public sealed record StatsSnapshot(
        int TotalAttempts, int TotalHits, int TotalFalseMiss, int TotalFalseHit,
        double HitRate, double FalseRate, LastJudge? Last, IReadOnlyList<KeyRow> ByKey);

    public sealed record KeyRow(string Key, int Attempts, int Hits, double AvgPromptEval);

    public StatsSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new StatsSnapshot(
                _totalAttempts, _totalHits, _totalFalseMiss, _totalFalseHit,
                _totalAttempts > 0 ? (double)_totalHits / _totalAttempts : 1.0,
                _totalAttempts > 0 ? (double)(_totalFalseMiss + _totalFalseHit) / _totalAttempts : 0.0,
                _lastJudge,
                _byKey.Select(kv => new KeyRow(
                    kv.Key, kv.Value.Attempts, kv.Value.Hits,
                    kv.Value.PromptEvalCount > 0 ? (double)kv.Value.PromptEvalSum / kv.Value.PromptEvalCount : 0))
                .ToList());
        }
    }

    /// <summary>轻量性能快照（可观测累积型指标源，v2.22）：命中数 / false_miss / 最大 savedN / 全量 prefill 累计。
    /// v2.23.10 增全量 prefill 计数；v2.23.11 增 ROI 复用 token 数 + 节省时间 ms。开销远低于全量 Snapshot（无 ByKey 构建）。</summary>
    public (int TotalHits, int TotalFalseMiss, int MaxSavedN, int TotalFullPrefill, long ReuseTokens, double ReuseSavedMs) PerfSnapshot()
    {
        lock (_gate) return (_totalHits, _totalFalseMiss, _maxSavedN, _totalFullPrefill, _reuseTokens, _reuseSavedMs);
    }

    /// <summary>前缀漂移告警累计次数（v2.23.10，状态栏/UI 展示）。</summary>
    public int DriftAlertCount
    {
        get { lock (_gate) return _driftAlertCount; }
    }

    /// <summary>持久化（原子写：临时文件 + rename）。休眠/退出时显式调用；无新数据时跳过。AH-13：锁内只判定+快照，磁盘 IO 锁外执行（不再阻塞统计更新）。</summary>
    public void Save()
    {
        object? payload = null;
        lock (_gate)
        {
            if (_dirty)
            {
                payload = BuildPayload();
                _dirty = false;
                _lastSaveAt = DateTime.Now;
            }
        }
        if (payload != null)
            WritePayloadAtomically(payload);
    }

    private static AlertLevel ComputeAlertLevel(double rate)
    {
        return rate < 0.5 ? AlertLevel.Red : rate < 0.8 ? AlertLevel.Yellow : AlertLevel.None;
    }

    /// <summary>节流自动保存：距上次保存 ≥10s 且有新数据才落盘。AH-13：锁外写盘（同 Save 模式）。</summary>
    private void TryAutoSave()
    {
        object? payload = null;
        lock (_gate)
        {
            var now = DateTime.Now;
            if (_dirty && (now - _lastSaveAt).TotalSeconds >= 10)
            {
                payload = BuildPayload();
                _dirty = false;
                _lastSaveAt = now;
            }
        }
        if (payload != null)
            WritePayloadAtomically(payload);
    }

    /// <summary>原子写盘（tmp + move，锁外调用）。失败尽力而为：_dirty 已置 false，下个周期再补。</summary>
    private void WritePayloadAtomically(object payload)
    {
        try
        {
            var dir = Path.GetDirectoryName(_statsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(payload);
            var tmp = _statsPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _statsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            // 尽力而为：磁盘满/权限等问题不影响主流程（含"警告"字样自动入 warn_error.log）
            LogFile.Append($"警告：[RESTORE-STATS] 持久化写盘失败 path={_statsPath} err={ex.Message}");
        }
    }

    /// <summary>构建持久化载荷（调用方已持 _gate）。</summary>
    private object BuildPayload()
    {
        return new
        {
            total = new { attempts = _totalAttempts, hits = _totalHits, false_miss = _totalFalseMiss, false_hit = _totalFalseHit, full_prefill = _totalFullPrefill, drift_alerts = _driftAlertCount, reuse_tokens = _reuseTokens, reuse_saved_ms = Math.Round(_reuseSavedMs, 1) },
            by_key = _byKey.Select(kv => new
            {
                key = kv.Key,
                attempts = kv.Value.Attempts,
                hits = kv.Value.Hits,
                false_miss = kv.Value.FalseMiss,
                false_hit = kv.Value.FalseHit,
                avg_prompt_eval = kv.Value.PromptEvalCount > 0 ? Math.Round((double)kv.Value.PromptEvalSum / kv.Value.PromptEvalCount, 1) : 0.0,
                full_prefill = kv.Value.FullPrefillCount,
                full_prefill_chain = kv.Value.FullPrefillChain
            }).ToList(),
            last_judge = _lastJudge == null ? null : new
            {
                key = _lastJudge.Key,
                hit = _lastJudge.Hit,
                reason = _lastJudge.Reason,
                prompt_eval = _lastJudge.PromptEvalTokens,
                saved_n = _lastJudge.SavedN,
                wrapper_hit = _lastJudge.WrapperHit,
                time = _lastJudge.Time.ToString("yyyy-MM-dd HH:mm:ss")
            }
        };
    }

    /// <summary>P3-C：JSON 数值反序列化健壮性辅助——非 Number 或反序列化失败时降级为 false（调用方保留默认 0），避免 JsonException 中断整个 Load。</summary>
    private static bool TryGetInt32(JsonElement el, out int v)
    {
        v = 0;
        if (el.ValueKind != JsonValueKind.Number) return false;
        return el.TryGetInt32(out v);
    }

    private static bool TryGetInt64(JsonElement el, out long v)
    {
        v = 0;
        if (el.ValueKind != JsonValueKind.Number) return false;
        return el.TryGetInt64(out v);
    }

    private static bool TryGetDouble(JsonElement el, out double v)
    {
        v = 0;
        if (el.ValueKind != JsonValueKind.Number) return false;
        return el.TryGetDouble(out v);
    }

    /// <summary>从持久化文件恢复累计统计（构造时调用；文件损坏则从零开始）。</summary>
    private void Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_statsPath)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(_statsPath));
                var root = doc.RootElement;
                if (root.TryGetProperty("total", out var t))
                {
                    if (t.TryGetProperty("attempts", out var a)) { if (TryGetInt32(a, out var v)) _totalAttempts = v; }
                    if (t.TryGetProperty("hits", out var h)) { if (TryGetInt32(h, out var v)) _totalHits = v; }
                    if (t.TryGetProperty("false_miss", out var fm)) { if (TryGetInt32(fm, out var v)) _totalFalseMiss = v; }
                    if (t.TryGetProperty("false_hit", out var fh)) { if (TryGetInt32(fh, out var v)) _totalFalseHit = v; }
                    if (t.TryGetProperty("full_prefill", out var fp)) { if (TryGetInt32(fp, out var v)) _totalFullPrefill = v; }
                    if (t.TryGetProperty("drift_alerts", out var da)) { if (TryGetInt32(da, out var v)) _driftAlertCount = v; }
                    if (t.TryGetProperty("reuse_tokens", out var rt)) { if (TryGetInt64(rt, out var v)) _reuseTokens = v; }
                    if (t.TryGetProperty("reuse_saved_ms", out var rsm)) { if (TryGetDouble(rsm, out var v)) _reuseSavedMs = v; }
                }
                if (root.TryGetProperty("by_key", out var bk) && bk.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in bk.EnumerateArray())
                    {
                        string keyName = e.TryGetProperty("key", out var kn) ? (kn.GetString() ?? "") : "";
                        if (string.IsNullOrEmpty(keyName)) continue;
                        var ks = new KeyStats();
                        if (e.TryGetProperty("attempts", out var a)) { if (TryGetInt32(a, out var v)) ks.Attempts = v; }
                        if (e.TryGetProperty("hits", out var h)) { if (TryGetInt32(h, out var v)) ks.Hits = v; }
                        if (e.TryGetProperty("false_miss", out var fm)) { if (TryGetInt32(fm, out var v)) ks.FalseMiss = v; }
                        if (e.TryGetProperty("false_hit", out var fh)) { if (TryGetInt32(fh, out var v)) ks.FalseHit = v; }
                        if (e.TryGetProperty("full_prefill", out var fpc)) { if (TryGetInt32(fpc, out var v)) ks.FullPrefillCount = v; }
                        if (e.TryGetProperty("full_prefill_chain", out var fch)) { if (TryGetInt32(fch, out var v)) ks.FullPrefillChain = v; }
                        _byKey[keyName] = ks;
                    }
                }
            }
            catch (Exception ex)
            {
                // 文件损坏：从零开始（不抛出；含"警告"字样自动入 warn_error.log）
                LogFile.Append($"警告：[RESTORE-STATS] 反序列化失败已回退初始状态 path={_statsPath} err={ex.Message}");
            }
        }
    }
}
