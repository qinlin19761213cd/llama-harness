using System.Collections.Specialized;
using System.Net;

namespace LlamaHarness;

/// <summary>
/// 多槽亲和绑定（--parallel &gt; 1 时启用）：
/// - 指纹识别：从请求头识别四大业务，生成唯一亲和 Key（零客户端侵入）
/// - 槽位绑定：Key → 槽号，持久化 slot_bindings.json（重启恢复）
/// - 强占模式：preemptive=true 的绑定不可被驱逐；全被强占占满时排队等待（上限 30s），超时降级随机槽
/// - KV Cache 开关：驱逐时是否保存 KV Cache（kvCache=false → 直接丢弃不保存）
/// - LRU 驱逐：跳过强占绑定，驱逐最久未活跃的非强占绑定
/// - 未知请求：随机槽位，不建立永久绑定
/// </summary>
public sealed class SlotAffinity
{
    private const int MaxWaitSecondsDefault = 30;
    private const int PollIntervalMs = 1000;
    private const int StaleBindingDays = 30;
    private readonly IReadOnlyList<AffinityRule> _rules;
    private readonly int _slotCount;
    private readonly object _gate = new();
    private readonly Dictionary<string, Binding> _bindings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>绑定表持久化文件（exe 同目录）。</summary>
    /// <summary>槽位绑定持久化路径：项目目录下 config/slot_bindings.json。</summary>
    private static readonly string BindingsPath = AppPaths.SlotBindingsJson;

    internal struct Binding
    {
        public int Slot;
        public DateTime LastActive;
        public bool Preemptive;
        public bool KvCache;
    }

    /// <summary>排队等待上限（秒）。全槽被强占时新请求最多等这么久。</summary>
    private readonly int _maxWaitSeconds;

    /// <summary>Tool 链锁定集合（§4.5）：本层执行过 SetPreemptive(true) 的 key。
    /// 驱逐优先级：Tool 链锁定 > 手动/自动强占（Tool 是瞬态的，循环结束自动解锁）。</summary>
    private readonly HashSet<string> _toolLockedKeys = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>AH-7：未知请求轮转分配计数器（Interlocked 原子递增，替代 Random——并发下每个未知请求分到不同槽，避免同槽碰撞）。</summary>
    private int _nextRandomSlot;
    private int _evictCount;    // v2.22 可观测：驱逐事件累计次数
    private int _preemptCount;  // v2.22 可观测：强占触发累计次数

    /// <summary>v2.22 可观测：性能事件通道（槽选择耗时 / 驱逐 / 强占）。由宿主（SmartScheduler）注入；null = 不采集。</summary>
    public PerfEventTracker? PerfEvents { get; set; }

    public SlotAffinity(int slotCount, int maxWaitSeconds = MaxWaitSecondsDefault, IReadOnlyList<AffinityRule>? rules = null)
    {
        _slotCount = Math.Max(1, slotCount);
        _maxWaitSeconds = Math.Max(1, maxWaitSeconds);
        _rules = rules ?? AppConfig.DefaultAffinityRules();
        Load();
        EnforcePreemptiveCap();
    }

    /// <summary>v2.22 可观测：调度累积型计数快照（驱逐 / 强占）。</summary>
    public (int Evict, int Preempt) PerfSnapshot()
    {
        lock (_gate) return (_evictCount, _preemptCount);
    }

    /// <summary>槽位数。</summary>
    public int SlotCount => _slotCount;

    /// <summary>指纹识别：按配置规则（affinity_rules）识别业务返回亲和 Key；null = 未知请求（不建立绑定）。
    /// 规则有序按 Priority 匹配，新增业务 = 配置追加规则，零代码改动（v2.16 替代原硬编码 4 组 if）。</summary>
    public string? GetAffinityKey(NameValueCollection h) => AffinityRuleMatcher.Match(h, _rules);

    /// <summary>按配置规则（affinity_rules）派生应用显示名。</summary>
    public string AppNameOf(string key) => AffinityRuleMatcher.AppNameOf(key, _rules);

    /// <summary>AH-7：轮转分配下一个槽位（Interlocked 原子，线程安全；并发下分配唯一槽，杜绝 Random 同槽碰撞）。</summary>
    private int NextRoundRobinSlot() => (int)((uint)Interlocked.Increment(ref _nextRandomSlot) % (uint)_slotCount);

    /// <summary>
    /// 获取请求的槽位：已绑定 → 其槽位（刷新活跃时间）；新 Key → 空闲槽或 LRU 驱逐。
    /// 全被强占占满 → 排队等待（上限 _maxWaitSeconds），超时降级随机槽。
    /// E-5：两阶段——锁内只做判定（阶段 1），排队 Sleep 在锁外（阶段 2），
    /// 等待期间其他请求的 GetSlot/SetPreemptive/Snapshot 不再被阻塞（旧实现 Sleep-in-lock 最长卡 30s）。
    /// </summary>
    /// <param name="autoPreemptive">自动强占前缀集合（§4.2 主力会话冻结）：key 匹配任一前缀 → 强制 Preemptive=true（暂停 LRU 驱逐）。</param>
    /// <returns>(slot, key, isNewBinding, evictedKey, evictedSlot, evictedKvCache)</returns>
    public (int Slot, string? Key, bool NewBinding, string? Evicted, int EvictedSlot, bool EvictedKvCache) GetSlot(
        NameValueCollection headers, IReadOnlyList<string>? autoPreemptive = null)
    {
        // v2.22 可观测：槽路由选择耗时（从进入排队到分配完成，含排队等待）
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = GetSlotCore(headers, autoPreemptive);
        PerfEvents?.Record(new PerfEvent("sched", "slot_select", sw.Elapsed.TotalMilliseconds, r.Key));
        return r;
    }

    private (int Slot, string? Key, bool NewBinding, string? Evicted, int EvictedSlot, bool EvictedKvCache) GetSlotCore(
        NameValueCollection headers, IReadOnlyList<string>? autoPreemptive = null)
    {
        var key = GetAffinityKey(headers);
        if (string.IsNullOrEmpty(key))
            return (NextRoundRobinSlot(), null, false, null, -1, false);

        // §4.2：应用类型在自动强占集合 → 强制冻结（新绑定创建时 + 已有绑定每次访问）
        bool autoPre = autoPreemptive != null && autoPreemptive.Any(p => !string.IsNullOrEmpty(p) && key.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        // ── 阶段 1（锁内）：已有绑定刷新 / 空闲槽 / LRU 驱逐判定 ──
        lock (_gate)
        {
            if (_bindings.TryGetValue(key, out var b))
            {
                // 已有绑定刷新：autoPre 想设强占，但若当前非强占且 cap 已满 → 不设（防"启动裁剪→下次请求又变回强占"死循环）
                bool newPre = b.Preemptive || autoPre;
                if (newPre && !b.Preemptive)
                {
                    int cap = Math.Max(0, _slotCount - 1);
                    int preemptiveCount = _bindings.Count(kv => kv.Value.Preemptive);
                    if (preemptiveCount >= cap)
                        newPre = false; // cap 已满：放弃强占，走 LRU 驱逐
                }
                _bindings[key] = new Binding { Slot = b.Slot, LastActive = DateTime.Now, Preemptive = newPre, KvCache = b.KvCache };
                return (b.Slot, key, false, null, -1, false);
            }

            var alloc = TryAllocateLocked(key, autoPre);
            if (alloc.Slot != null)
                return (alloc.Slot!.Value, key, true, alloc.Evicted, alloc.EvictedSlot, alloc.EvictedKvCache);
            // 全被强占占满 → 锁外排队（E-5）
        }

        // ── 阶段 2（锁外）：排队等待（上限 _maxWaitSeconds），Sleep 不持锁 ──
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < _maxWaitSeconds)
        {
            Thread.Sleep(PollIntervalMs);
            lock (_gate)
            {
                var alloc = TryAllocateLocked(key, autoPre);
                if (alloc.Slot != null)
                    return (alloc.Slot!.Value, key, true, alloc.Evicted, alloc.EvictedSlot, alloc.EvictedKvCache);
            }
        }

        // 超时降级：轮转槽，不建绑定（AH-7：与未知请求一致用轮转，避免 Random 同槽碰撞）
        return (NextRoundRobinSlot(), null, false, null, -1, false);
    }

    /// <summary>锁内原子分配：空闲槽 → LRU 驱逐非强占 → 建绑定 + 持久化。
    /// Slot=null = 全被强占占满（调用方锁外排队）。重复 key 并发时采纳已有绑定（保持旧单锁语义）。</summary>
    private (int? Slot, string? Evicted, int EvictedSlot, bool EvictedKvCache) TryAllocateLocked(string key, bool autoPre)
    {
        if (_bindings.TryGetValue(key, out var existing))
            return (existing.Slot, null, -1, false); // 重复 key 并发：采纳已有绑定

        // 新 Key：优先分配无其他绑定的槽；全占则驱逐最久未活跃的非强占绑定
        int? slot = FindFreeSlotLocked();
        string? evicted = null;
        int evictedSlot = -1;
        bool evictedKvCache = false;
        if (slot < 0)
        {
            // 找可驱逐目标（非强占）
            var lruKey = _bindings.Where(kv => !kv.Value.Preemptive).OrderBy(kv => kv.Value.LastActive).FirstOrDefault().Key;
            if (!string.IsNullOrEmpty(lruKey))
            {
                slot = _bindings[lruKey].Slot;
                evictedSlot = slot.Value;
                evictedKvCache = _bindings[lruKey].KvCache;
                _bindings.Remove(lruKey);
                _evictCount++; // v2.22 可观测：LRU 驱逐计数
                evicted = lruKey;
            }
            else
            {
                // 全被强占占满：若新 key 也是强占 → 驱逐最早活跃的强占绑定（保"至少 1 槽给非强占"不变量）
                if (autoPre)
                {
                    var victim = FindEvictablePreemptiveLocked();
                    if (!string.IsNullOrEmpty(victim))
                    {
                        slot = _bindings[victim].Slot;
                        evictedSlot = slot.Value;
                        evictedKvCache = _bindings[victim].KvCache;
                        _bindings.Remove(victim);
                        _evictCount++; // v2.22 可观测：强占驱逐计数
                        evicted = victim;
                    }
                    else
                    {
                        return (null, null, -1, false); // 无可驱逐（理论上不会：cap 保证 ≤ slotCount-1）
                    }
                }
                else
                {
                    return (null, null, -1, false); // 非强占新 key + 全槽强占 → 排队
                }
            }
        }
        // 新建绑定时同样检查 cap（防"驱逐了一个强占又建一个新的强占"导致 cap 失效）
        bool finalPre = autoPre;
        if (finalPre)
        {
            int cap = Math.Max(0, _slotCount - 1);
            // 此时 victim 已被移除，preemptiveCount 是移除后的值
            int preemptiveCount = _bindings.Count(kv => kv.Value.Preemptive);
            if (preemptiveCount >= cap)
                finalPre = false; // cap 已满：放弃强占，走 LRU 驱逐
        }
        if (finalPre) _preemptCount++; // v2.22 可观测：强占触发计数（新绑定成功冻结槽位）
        _bindings[key] = new Binding { Slot = slot!.Value, LastActive = DateTime.Now, Preemptive = finalPre, KvCache = true };
        Save();
        return (slot.Value, evicted, evictedSlot, evictedKvCache);
    }

    /// <summary>启动时强制：裁剪超额强占到 ≤ slotCount-1（保"至少 1 槽给非强占新任务"不变量）。
    /// 驱逐优先级：Tool 链锁定 > 最早活跃的手动/自动强占。返回被取消强占的 key 列表。</summary>
    public List<string> EnforcePreemptiveCap()
    {
        lock (_gate)
        {
            var preemptiveKeys = _bindings.Where(kv => kv.Value.Preemptive).Select(kv => kv.Key).ToList();
            int cap = Math.Max(0, _slotCount - 1);
            if (preemptiveKeys.Count <= cap) return new List<string>();

            // 需驱逐数 = 当前强占数 - cap
            int toEvict = preemptiveKeys.Count - cap;
            var evicted = new List<string>();

            // 优先级 1：Tool 链锁定（瞬态，循环结束自动解锁）——按最早活跃排序
            for (int i = 0; i < toEvict; i++)
            {
                var toolKey = _bindings.Where(kv => kv.Value.Preemptive && _toolLockedKeys.Contains(kv.Key))
                                         .OrderBy(kv => kv.Value.LastActive).FirstOrDefault().Key;
                if (string.IsNullOrEmpty(toolKey)) break;
                _bindings[toolKey] = new Binding { Slot = _bindings[toolKey].Slot, LastActive = _bindings[toolKey].LastActive, Preemptive = false, KvCache = _bindings[toolKey].KvCache };
                _toolLockedKeys.Remove(toolKey);
                evicted.Add(toolKey);
            }

            // 优先级 2：最早活跃的手动/自动强占（最久持有 = 最该让位）
            int remaining = toEvict - evicted.Count;
            for (int i = 0; i < remaining; i++)
            {
                var key = _bindings.Where(kv => kv.Value.Preemptive)
                                    .OrderBy(kv => kv.Value.LastActive).FirstOrDefault().Key;
                if (string.IsNullOrEmpty(key)) break;
                _bindings[key] = new Binding { Slot = _bindings[key].Slot, LastActive = _bindings[key].LastActive, Preemptive = false, KvCache = _bindings[key].KvCache };
                evicted.Add(key);
            }

            Save();
            return evicted;
        }
    }

    /// <summary>锁内查找可驱逐的强占绑定（TryAllocateLocked 用）：Tool 链锁定优先，其次最早活跃。
    /// 返回 null = 无可驱逐（cap 保证 ≤ slotCount-1，理论上不会发生）。</summary>
    private string? FindEvictablePreemptiveLocked()
    {
        // Tool 链锁定优先（瞬态）
        var toolKey = _bindings.Where(kv => kv.Value.Preemptive && _toolLockedKeys.Contains(kv.Key))
                                .OrderBy(kv => kv.Value.LastActive).FirstOrDefault().Key;
        if (!string.IsNullOrEmpty(toolKey)) return toolKey;
        // 其次最早活跃的手动/自动强占
        return _bindings.Where(kv => kv.Value.Preemptive)
                         .OrderBy(kv => kv.Value.LastActive).FirstOrDefault().Key;
    }

    /// <summary>标记 key 为 Tool 链锁定（SmartScheduler 调用，驱逐优先级用）。</summary>
    public void MarkToolLocked(string key)
    {
        lock (_gate) { _toolLockedKeys.Add(key); }
    }

    /// <summary>取消 Tool 链锁定标记（SmartScheduler 调用）。</summary>
    public void UnmarkToolLocked(string key)
    {
        lock (_gate) { _toolLockedKeys.Remove(key); }
    }

    /// <summary>指定 Key 当前是否为强占（Tool 链锁定判定用）。</summary>
    public bool IsPreemptive(string key)
    {
        lock (_gate)
        {
            return _bindings.TryGetValue(key, out var b) && b.Preemptive;
        }
    }

    /// <summary>设置指定 Key 的强占模式（UI 调用）。</summary>
    public void SetPreemptive(string key, bool value)
    {
        lock (_gate)
        {
            if (_bindings.TryGetValue(key, out var b))
            {
                _bindings[key] = new Binding { Slot = b.Slot, LastActive = b.LastActive, Preemptive = value, KvCache = b.KvCache };
                Save();
            }
        }
    }

    /// <summary>是否应跳过 Tool 链会话锁定（§4.5）：单槽位（parallel=1，cap=slotCount-1=0）时返回 true。
    /// 单槽位无多槽驱逐竞争，Tool 链锁定 SetPreemptive(true) 会独占唯一槽位，违反"至少 1 槽给非强占新任务"
    /// 不变量，其他 key 任务最长排队 30s（v2.23.7 修复依据）。多槽位返回 false（保留锁定保护）。</summary>
    public bool ShouldSkipToolLoopLock() => _slotCount <= 1;

    /// <summary>设置指定 Key 的 KV Cache 开关（UI 调用）。</summary>
    public void SetKvCache(string key, bool value)
    {
        lock (_gate)
        {
            if (_bindings.TryGetValue(key, out var b))
            {
                _bindings[key] = new Binding { Slot = b.Slot, LastActive = b.LastActive, Preemptive = b.Preemptive, KvCache = value };
                Save();
            }
        }
    }

    /// <summary>当前绑定快照（状态展示用，含应用名/强占/KV缓存配置）。</summary>
    public List<(string Key, string App, int Slot, DateTime LastActive, bool Preemptive, bool KvCache)> Snapshot()
    {
        lock (_gate)
        {
            return _bindings.Select(kv => (kv.Key, AppNameOf(kv.Key), kv.Value.Slot, kv.Value.LastActive, kv.Value.Preemptive, kv.Value.KvCache))
                              .OrderByDescending(t => t.LastActive).ToList();
        }
    }

    private int FindFreeSlotLocked()
    {
        var used = new HashSet<int>(_bindings.Values.Select(b => b.Slot));
        for (int i = 0; i < _slotCount; i++)
            if (!used.Contains(i)) return i;
        return -1; // 全占
    }

    /// <summary>从 slot_bindings.json 恢复绑定；兼容旧格式（缺字段取默认值）。</summary>
    private void Load()
    {
        try
        {
            if (!File.Exists(BindingsPath)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(BindingsPath));
            var root = System.Text.Json.Nodes.JsonNode.Parse(doc.RootElement.GetRawText());
            if (root?["bindings"] is not System.Text.Json.Nodes.JsonObject bn) return;
            foreach (var kv in bn)
            {
                var v = kv.Value;
                int slot = v?["slot"]?.GetValue<int>() ?? -1;
                string lastActive = v?["lastActive"]?.GetValue<string>() ?? "";
                bool preemptive = v?["preemptive"]?.GetValue<bool>() ?? false;
                bool kvCache = v?["kvCache"]?.GetValue<bool>() ?? true;
                if (slot < 0 || slot >= _slotCount) continue; // --parallel 缩减：丢弃越界绑定
                if (!DateTime.TryParse(lastActive, out var dt)) dt = DateTime.Now.AddDays(-StaleBindingDays);
                _bindings[kv.Key] = new Binding { Slot = slot, LastActive = dt, Preemptive = preemptive, KvCache = kvCache };
            }
        }
        catch
        {
            // 绑定文件损坏：忽略，从零开始
        }
    }

    /// <summary>持久化绑定表（含应用名/强占/KV缓存配置）。AH-8：原子写（tmp + move），中断不产生半写文件。</summary>
    private void Save()
    {
        try
        {
            AppPaths.EnsureConfigDir();
            var bindings = new System.Text.Json.Nodes.JsonObject();
            foreach (var kv in _bindings)
            {
                bindings[kv.Key] = new System.Text.Json.Nodes.JsonObject
                {
                    ["app"] = AppNameOf(kv.Key),
                    ["slot"] = kv.Value.Slot,
                    ["preemptive"] = kv.Value.Preemptive,
                    ["kvCache"] = kv.Value.KvCache,
                    ["lastActive"] = kv.Value.LastActive.ToString("o")
                };
            }
            var obj = new System.Text.Json.Nodes.JsonObject
            {
                ["slotCount"] = _slotCount,
                ["bindings"] = bindings
            };
            var tmp = BindingsPath + ".tmp";
            File.WriteAllText(tmp, obj.ToJsonString());
            File.Move(tmp, BindingsPath, overwrite: true);
        }
        catch
        {
            // 持久化失败不影响路由（内存绑定仍有效）
        }
    }

    private static bool TryGetHeader(NameValueCollection h, string name, out string value)
    {
        // NameValueCollection 索引器对缺失键返回 null 而不抛异常，无需 try/catch（审计：原死防御删除）
        value = h[name] ?? "";
        return true;
    }
}
