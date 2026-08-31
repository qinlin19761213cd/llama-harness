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

    private readonly object _gate = new();
    private readonly string _statsPath;
    private readonly Queue<Pending> _pending = new();
    private readonly Dictionary<string, KeyStats> _byKey = new(StringComparer.OrdinalIgnoreCase);

    private int _totalAttempts, _totalHits, _totalFalseMiss, _totalFalseHit;
    private int _maxSavedN; // 会话最大 token 偏移（可观测 kv 累积型指标 saved_n 源）
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
        bool FalseMiss, bool FalseHit, double HitRate, AlertLevel Alert);

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
    }

    /// <summary>静态正则：prompt eval time = X ms / N tokens（与 LlamaStatsParser.PromptRe 同口径）。</summary>
    private static readonly System.Text.RegularExpressions.Regex PromptEvalRe =
        new(@"prompt eval time\s*=\s*([\d.]+)\s*ms\s*/\s*(\d+)\s*tokens", System.Text.RegularExpressions.RegexOptions.Compiled);

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

    /// <summary>请求侧：入队判定上下文（路由完成且该 key 存在快照时调用）。</summary>
    public void RecordRequest(string key, int slot, bool wrapperHit, int savedN)
    {
        lock (_gate)
        {
            if (savedN > _maxSavedN) _maxSavedN = savedN;
            _pending.Enqueue(new Pending { Key = key, Slot = slot, WrapperHit = wrapperHit, SavedN = savedN, EnqueuedAt = DateTime.Now });
        }
    }

    /// <summary>输出侧：收到 prompt eval 行时弹最旧条目并判定。无判定上下文（如非亲和 key 任务）返回 null。</summary>
    public JudgeResult? OnPromptEval(int tokens)
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
        lock (_gate)
        {
            _totalAttempts++;
            if (hit) _totalHits++;
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

            rate = (double)_totalHits / _totalAttempts;
            var level = _totalAttempts >= MinSamplesForAlert ? ComputeAlertLevel(rate) : AlertLevel.None;
            alertRaised = level != _lastAlert ? level : AlertLevel.None; // 状态迁移触发：同级别不重复告警
            _lastAlert = level;
            LastJudgeResult = new JudgeResult(p.Key, p.Slot, hit, reason, tokens, p.SavedN, p.WrapperHit, falseMiss, falseHit, rate, alertRaised);
        }

        TryAutoSave(); // 节流持久化（≥10s 一次）
        return new JudgeResult(p.Key, p.Slot, hit, reason, tokens, p.SavedN, p.WrapperHit, falseMiss, falseHit, rate, alertRaised);
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

    /// <summary>轻量性能快照（可观测累积型指标源，v2.22）：命中数 / false_miss / 最大 savedN。开销远低于全量 Snapshot（无 ByKey 构建）。</summary>
    public (int TotalHits, int TotalFalseMiss, int MaxSavedN) PerfSnapshot()
    {
        lock (_gate) return (_totalHits, _totalFalseMiss, _maxSavedN);
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
        catch
        {
            // 尽力而为：磁盘满/权限等问题不影响主流程
        }
    }

    /// <summary>构建持久化载荷（调用方已持 _gate）。</summary>
    private object BuildPayload()
    {
        return new
        {
            total = new { attempts = _totalAttempts, hits = _totalHits, false_miss = _totalFalseMiss, false_hit = _totalFalseHit },
            by_key = _byKey.Select(kv => new
            {
                key = kv.Key,
                attempts = kv.Value.Attempts,
                hits = kv.Value.Hits,
                false_miss = kv.Value.FalseMiss,
                false_hit = kv.Value.FalseHit,
                avg_prompt_eval = kv.Value.PromptEvalCount > 0 ? Math.Round((double)kv.Value.PromptEvalSum / kv.Value.PromptEvalCount, 1) : 0.0
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
                    if (t.TryGetProperty("attempts", out var a)) _totalAttempts = a.GetInt32();
                    if (t.TryGetProperty("hits", out var h)) _totalHits = h.GetInt32();
                    if (t.TryGetProperty("false_miss", out var fm)) _totalFalseMiss = fm.GetInt32();
                    if (t.TryGetProperty("false_hit", out var fh)) _totalFalseHit = fh.GetInt32();
                }
                if (root.TryGetProperty("by_key", out var bk) && bk.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in bk.EnumerateArray())
                    {
                        string keyName = e.TryGetProperty("key", out var kn) ? (kn.GetString() ?? "") : "";
                        if (string.IsNullOrEmpty(keyName)) continue;
                        var ks = new KeyStats();
                        if (e.TryGetProperty("attempts", out var a)) ks.Attempts = a.GetInt32();
                        if (e.TryGetProperty("hits", out var h)) ks.Hits = h.GetInt32();
                        if (e.TryGetProperty("false_miss", out var fm)) ks.FalseMiss = fm.GetInt32();
                        if (e.TryGetProperty("false_hit", out var fh)) ks.FalseHit = fh.GetInt32();
                        _byKey[keyName] = ks;
                    }
                }
            }
            catch
            {
                // 文件损坏：从零开始（不抛出）
            }
        }
    }
}
