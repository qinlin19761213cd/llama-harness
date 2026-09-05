using System.Text;
using System.Text.Json;

namespace LlamaHarness;

/// <summary>
/// KV Cache 手动缓存控制（llama-server --slots + --slot-save-path）：
/// - 驱逐前 save：POST /slots/{id}?action=save，把槽位 KV 落盘为 {key}.bin
/// - 重绑定 restore：POST /slots/{id}?action=restore，从 {key}.bin 恢复槽位 KV
/// - 擦除：POST /slots/{id}?action=erase
/// - 清空缓存：删除缓存目录下所有 *.bin + erase 全部槽位
/// 异步 + 在途去重（_inflightSaves），restore 前检查 save 是否完成。
/// </summary>
public sealed class KvCacheManager
{
    private readonly IBackendClient _backend;
    private readonly string _cachePath;
    private readonly int _slotCount;

    private readonly int _ctxSize;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, Task> _inflightSaves = new(StringComparer.OrdinalIgnoreCase);
    // A2 修复：复合键必须含 slot，否则多 slot 并发保存同一 key 会静默复用/移除错误的 in-flight Task
    private static string SaveKey(int slot, string key) => slot + "#" + key;
    /// <summary>per-slot save/restore 串行闸（AH-23：同一槽位并发 /slots/save 会干扰 llama-server 槽位状态机，串行化消除竞争）。</summary>
    private readonly SemaphoreSlim[] _slotSems;

    /// <summary>缓存索引持久化文件（exe 同目录）。</summary>
    /// <summary>KV Cache 索引路径：项目目录下 config/kv_cache_index.json。</summary>
    private const int StaleEntryDays = 30;
    private static readonly string IndexPath = AppPaths.KvCacheIndexJson;
    /// <summary>P2-5：缓存目录磁盘占用上限（1 GB）。SaveAsync 结束后台触发 LRU 清理，
    /// 防长期运行后无上限累积旧 .bin 撑爆磁盘。</summary>
    private const long MaxCacheBytes = 1L << 30;

    /// <summary>key → (slot, savedAt, nTokens, sizeBytes)。</summary>
    private readonly Dictionary<string, CacheEntry> _index = new(StringComparer.OrdinalIgnoreCase);

    internal struct CacheEntry
    {
        public int Slot;
        public DateTime SavedAt;
        public int NTokens;
        public long SizeBytes;
    }

    public KvCacheManager(IBackendClient backend, string cachePath, int slotCount, int ctxSize = 0, Action<string>? log = null)
    {
        _backend = backend;
        _cachePath = cachePath.TrimEnd('/');
        _slotCount = Math.Max(1, slotCount);
        _slotSems = new SemaphoreSlim[_slotCount];
        for (int i = 0; i < _slotCount; i++) _slotSems[i] = new SemaphoreSlim(1, 1);
        _ctxSize = ctxSize;
        _log = log;
        LoadIndex();
    }

    /// <summary>释放 SemaphoreSlim 资源。</summary>
    public void Dispose()
    {
        foreach (var sem in _slotSems)
            try { sem.Dispose(); } catch { /* 忽略 */ }
    }


    /// <summary>缓存目录路径。</summary>
    public string CachePath => _cachePath;

    /// <summary>key 是否有已保存的缓存文件。</summary>
    public bool HasCache(string key)
    {
        try
        {
            return File.Exists(CacheFilePath(key));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>最近一次 save 记录的 token 数（崩溃快照有效性判断：0 = 槽位已 release 的空快照）。</summary>
    public int SavedTokens(string key)
    {
        lock (_gate)
        {
            return _index.TryGetValue(key, out var e) ? e.NTokens : 0;
        }
    }

    /// <summary>删除指定 key 的缓存文件 + 元数据 json + 索引条目（§6.3：续接成功后清理过期断点快照，防 restore 回退旧状态）。</summary>
    public bool DeleteCache(string key)
    {
        try
        {
            var path = CacheFilePath(key);
            if (File.Exists(path)) File.Delete(path);
            var metaPath = Path.Combine(_cachePath, $"{Sanitize(key)}.meta.json");
            if (File.Exists(metaPath)) File.Delete(metaPath);
            lock (_gate) _index.Remove(key);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>缓存文件完整路径。</summary>
    public string CacheFilePath(string key) => Path.Combine(_cachePath, $"{Sanitize(key)}.bin");

    /// <summary>
    /// 保存槽位 KV 到 {key}.bin（异步 + 完成标记）。
    /// 同一 key 的并发 save 复用同一 Task（防重复）。
    /// </summary>
    public Task SaveAsync(int slot, string key, CancellationToken ct = default)
    {
        ValidateSlot(slot);
        lock (_gate)
        {
            if (_inflightSaves.TryGetValue(SaveKey(slot, key), out var existing))
                return existing; // 复用同一 slot 上同 key 进行中的 save
            var task = DoSaveAsync(slot, key, ct);
            // P2-7：写入顺序在 lock 内原子完成——TryGetValue → 未命中 → 创建 Task → 写入 map → return
            // 全过程共享 _gate，其他线程要么看到"未命中并等待"，要么看到"命中并复用"，
            // 不会出现"map 已被清掉但返回的是旧 task"的窗口。Task 完成后由 DoSaveAsync.finally 显式移除。
            _inflightSaves[SaveKey(slot, key)] = task;
            return task;
        }
    }

    /// <summary>slot 上下界校验（B-3 修复）：负数/越界必须显式失败，禁止静默取模。</summary>
    private void ValidateSlot(int slot)
    {
        if (slot < 0 || slot >= _slotCount)
            throw new ArgumentOutOfRangeException(nameof(slot), slot, $"slot 必须在 [0, {_slotCount}) 内，实际 {slot}");
    }

    private async Task DoSaveAsync(int slot, string key, CancellationToken ct)
    {
        // KV-CACHE 兜底：save 一律 30s 上限（无论调用方是否传超时），防后端 /slots/save 不响应导致
        // save 无限挂起、占住同槽 sem、阻塞后续 restore / 驱逐前 save（请求路径）。超时抛 OCE 走调用方失败降级。
        using var saveTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        saveTimeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        var effCt = saveTimeoutCts.Token;

        var sem = _slotSems[slot];
        bool held = false;
        try
        {
            await sem.WaitAsync(effCt); // AH-23：同槽 save 串行（防并发 /slots/save 干扰槽位状态机）
            held = true;
            // P2-6：文件名不含 slot 编号——依赖"每 key 由亲和路由绑定固定 slot"的上层不变量（Pipeline 侧
            // _savedKeysThisRun 单 key 去重）保证同名 .bin 不被并发覆写；若上层引入同 key 多 slot 并行 save，
            // 必须在此把文件名改为 `slot{slot}_{Sanitize(key)}.bin` 并同步 Verify/Restore 侧路径。
            var resp = await _backend.SlotSaveAsync(slot, $"{Sanitize(key)}.bin", effCt);
            var text = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode)
            {
                // 解析响应：n_saved / n_written（P2-2：ValueKind 防御，字段类型变化不抛）
                int nSaved = 0, nWritten = 0;
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("n_saved", out var ns) && ns.ValueKind == JsonValueKind.Number && ns.TryGetInt32(out var nsv)) nSaved = nsv;
                    if (root.TryGetProperty("n_written", out var nw) && nw.ValueKind == JsonValueKind.Number && nw.TryGetInt32(out var nww)) nWritten = nww;
                }
                catch { /* 响应格式变化：忽略 */ }
                RecordSave(key, slot, nSaved, nWritten);
                // 快照落盘校验 + 元数据 json（RAMDisk 快照全权接管：快照是会话唯一可信数据源，必须校验）
                VerifyAndWriteMetadata(key, nSaved);
            }
            else
            {
                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}");
            }
        }
        catch (OperationCanceledException)
        {
            _log?.Invoke($"[KV-CACHE-TIMEOUT] save 超时：key={key}，槽位 KV 未持久化（调用方走失败降级）");
            throw; // 抛给调用方：驱逐前 save / 首存档 / 后台快照 / 截断断点 / 休眠前统一走各自失败降级
        }
        finally
        {
            if (held) sem.Release();
            // P2-1：显式把 save 从 Pending 置为 Done——即从 _inflightSaves 移除条目。
            // finally 覆盖全部异常分支：正常路径、HTTP 失败 throw、OCE 重抛、下游非 OCE 异常（如 JSON/元数据异常）均会执行到此处，
            // 保证不会出现"任务已终态但 map 仍挂着旧 Task"的窗口（SaveAsync 幂等读取时能拿到的都是真正进行中的 Task）。
            lock (_gate)
            {
                // A2：按 (slot, key) 移除，防误删同一 key 但不同 slot 的在途 save
                _inflightSaves.Remove(SaveKey(slot, key));
            }
            // P2-5：save 结束后异步触发磁盘配额 LRU 清理（fire-and-forget，不阻塞主流程）
            _ = Task.Run(() => TrimToQuota());
        }
    }

    /// <summary>
    /// 快照落盘校验（文件大小 > 0、saved_n > 0）+ 写元数据 json（{key}.meta.json：session_id/saved_n_tokens/save_timestamp/ctx_size）。
    /// 校验不通过抛异常（调用方发 [EDGE-CASE-SAVE-FAILED] 并 DeleteCache 标记快照失效）。
    /// </summary>
    private void VerifyAndWriteMetadata(string key, int nSaved)
    {
        var path = CacheFilePath(key);
        if (!File.Exists(path)) throw new InvalidOperationException("快照文件不存在");
        long size = 0;
        try { size = new FileInfo(path).Length; } catch { /* 忽略 */ }
        if (size <= 0 || nSaved <= 0)
            throw new InvalidOperationException($"快照校验失败：size={size}, saved_n={nSaved}");

        // 元数据 json（slot-load 阶段校验 + metrics 观测用）
        try
        {
            var meta = new System.Text.Json.Nodes.JsonObject
            {
                ["session_id"] = key,
                ["saved_n_tokens"] = nSaved,
                ["save_timestamp"] = (long)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["ctx_size"] = _ctxSize
            };
            File.WriteAllText(Path.Combine(_cachePath, $"{Sanitize(key)}.meta.json"), meta.ToJsonString());
        }
        catch
        {
            // 元数据写入失败不影响快照本身（restore 侧按"无元数据"降级处理）
        }
    }

    /// <summary>
    /// restore 前置校验：快照文件存在、大小合法、元数据 saved_n > 0。
    /// 损坏/缺失 → [EDGE-CASE-SNAPSHOT-CORRUPT] + DeleteCache（废弃快照，调用方走全量 prefill 兜底）。
    /// </summary>
    private bool ValidateSnapshot(string key)
    {
        var path = CacheFilePath(key);
        if (!File.Exists(path)) return false;
        long size = 0;
        try { size = new FileInfo(path).Length; } catch { /* 忽略 */ }
        if (size <= 0)
        {
            OnCorrupt(key, $"快照文件大小为 0：{path}");
            return false;
        }
        // 元数据校验（无元数据文件视为旧版快照，放行；有但 saved_n<=0 判定损坏）
        var metaPath = Path.Combine(_cachePath, $"{Sanitize(key)}.meta.json");
        if (File.Exists(metaPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                if (doc.RootElement.TryGetProperty("saved_n_tokens", out var sn) && sn.GetInt32() <= 0)
                {
                    OnCorrupt(key, $"元数据 saved_n_tokens={sn.GetInt32()}（异常值）");
                    return false;
                }
            }
            catch
            {
                OnCorrupt(key, "元数据 json 解析失败");
                return false;
            }
        }
        return true;
    }

    private void OnCorrupt(string key, string reason)
    {
        _log?.Invoke($"[EDGE-CASE-SNAPSHOT-CORRUPT] {key}：{reason}，废弃快照走全量 prefill 兜底。");
        DeleteCache(key);
    }

    /// <summary>
    /// 恢复 {key}.bin 到槽位（restore 前检查 save 是否完成）。
    /// 若 key 正在 save 中，等待其完成后再 restore。
    /// P2-8：跨 slot 的在途 save 与本 slot restore 无直接依赖，不做等待（避免不同 slot 互相阻塞）。
    /// </summary>
    public async Task<bool> RestoreAsync(int slot, string key)
    {
        ValidateSlot(slot);
        // 等待进行中的 save 完成（防 save/restore 竞态）
        Task? saveTask = null;
        lock (_gate)
        {
            // A2：仅等待同 slot 同 key 的在途 save（跨 slot 的 save 与本 slot restore 无直接依赖）
            if (_inflightSaves.TryGetValue(SaveKey(slot, key), out var t)) saveTask = t;
        }
        if (saveTask != null)
        {
            try
            {
                // M-03 修复：增加超时保护，避免上游 save 异常时后续请求永久挂起
                await saveTask.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch
            {
                /* save 失败或超时不影响 restore 尝试 */
            }
        }

        // AH-23：同槽 save/restore 互斥（防 restore 与并发 save 竞争同一槽位状态）
        var sem = _slotSems[slot];
        bool held = false;
        using var restoreCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await sem.WaitAsync(restoreCts.Token);
            held = true;
            // restore 前置校验：快照文件/元数据损坏 → [EDGE-CASE-SNAPSHOT-CORRUPT] + 废弃（全量 prefill 兜底）
            if (!ValidateSnapshot(key)) return false;

            var resp = await _backend.SlotRestoreAsync(slot, $"{Sanitize(key)}.bin", restoreCts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException)
        {
            _log?.Invoke($"[KV-CACHE-TIMEOUT] restore 超时：key={key}");
            return false;
        }
        finally
        {
            if (held) { try { sem.Release(); } catch { /* 已释放或异常时忽略 */ } }
        }
    }

    /// <summary>擦除槽位 KV（不删缓存文件）。30s 超时兜底：防后端 /slots/erase 不响应导致清空缓存/驱逐挂起。</summary>
    public async Task<bool> EraseAsync(int slot)
    {
        ValidateSlot(slot);
        using var eraseCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var resp = await _backend.SlotEraseAsync(slot, eraseCts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException)
        {
            _log?.Invoke($"[KV-CACHE-TIMEOUT] erase 超时：slot{slot}");
            return false;
        }
    }

    /// <summary>
    /// 清空缓存：删除缓存目录下所有 *.bin + erase 全部槽位。
    /// AH-10：先等待在途 save 完成（后台每轮 save 与清空并发时，避免删除后文件被重写、索引"复活"）。
    /// </summary>
    public async Task<int> ClearAllAsync()
    {
        // AH-10 + [P1-M15]：等待在途 save 结束（最长 ~5s），再执行删除
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            for (int i = 0; i < 50; i++)
            {
                Task[] inflight;
                lock (_gate)
                {
                    if (_inflightSaves.Count == 0) break;
                    inflight = _inflightSaves.Values.ToArray();
                }
                // [P1-M15] 改用 WhenAny + Delay(ct) 实现真正的超时——WhenAll 本身不接受 CancellationToken，
                // 如果 inflight 中有任务不响应取消，原代码的 IsCancellationRequested 检查被 WhenAll 阻塞住
                var allDone = Task.WhenAll(inflight);
                var completed = await Task.WhenAny(allDone, Task.Delay(-1, cts.Token));
                if (completed != allDone) break; // 5s 超时：跳出循环，继续删文件
                try { await allDone; } catch { /* save 失败不影响清空 */ }
            }
        }
        finally
        {
            cts.Dispose();
        }

        int deleted = 0;
        try
        {
            if (Directory.Exists(_cachePath))
            {
                foreach (var f in Directory.GetFiles(_cachePath, "*.bin"))
                {
                    try { File.Delete(f); deleted++; } catch { /* 忽略单文件失败 */ }
                }
            }
        }
        catch { /* 目录不存在：忽略 */ }

        // erase 全部槽位
        for (int i = 0; i < _slotCount; i++)
        {
            try { await EraseAsync(i); } catch { /* 忽略 */ }
        }

        // O-17：_index 变更统一在 lock(_gate) 内（与 RecordSave/Snapshot/LoadIndex 一致）；索引写盘移锁外（AH-13）
        lock (_gate)
        {
            _index.Clear();
        }
        SaveIndex();
        return deleted;
    }

    /// <summary>记录 save 成功（更新索引）。AH-13：锁内只更新内存索引，索引写盘移锁外（不阻塞并发 save/restore）。</summary>
    private void RecordSave(string key, int slot, int nTokens, long sizeBytes)
    {
        lock (_gate)
        {
            _index[key] = new CacheEntry { Slot = slot, SavedAt = DateTime.Now, NTokens = nTokens, SizeBytes = sizeBytes };
        }
        SaveIndex();
    }

    /// <summary>缓存索引快照（UI 展示用）。</summary>
    public List<(string Key, int Slot, DateTime SavedAt, int NTokens, long SizeBytes)> Snapshot()
    {
        lock (_gate)
        {
            return _index.Select(kv => (kv.Key, kv.Value.Slot, kv.Value.SavedAt, kv.Value.NTokens, kv.Value.SizeBytes))
                          .OrderByDescending(t => t.SavedAt).ToList();
        }
    }

    private void LoadIndex()
    {
        try
        {
            if (!File.Exists(IndexPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(IndexPath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            foreach (var prop in root.EnumerateObject())
            {
                // P2-2：字段类型变化 / 缺失统一回退默认值（缺字段返回 0，不抛异常），
                // 兼容旧版索引、字段被外部修改、Json 类型漂移等边界。
                int slot = -1;
                string savedAt = "";
                int nTokens = 0, sizeBytes = 0;
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    if (prop.Value.TryGetProperty("slot", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var sv)) slot = sv;
                    if (prop.Value.TryGetProperty("savedAt", out var sa)) savedAt = sa.ValueKind == JsonValueKind.String ? (sa.GetString() ?? "") : "";
                    if (prop.Value.TryGetProperty("nTokens", out var nt) && nt.ValueKind == JsonValueKind.Number && nt.TryGetInt32(out var ntv)) nTokens = ntv;
                    if (prop.Value.TryGetProperty("sizeBytes", out var sb) && sb.ValueKind == JsonValueKind.Number && sb.TryGetInt32(out var sbv)) sizeBytes = sbv;
                }
                if (!DateTime.TryParse(savedAt, out var dt)) dt = DateTime.Now.AddDays(-StaleEntryDays);
                _index[prop.Name] = new CacheEntry { Slot = slot, SavedAt = dt, NTokens = nTokens, SizeBytes = sizeBytes };
            }
        }
        catch
        {
            // 索引损坏：忽略
        }
    }

    private void SaveIndex()
    {
        try
        {
            string json;
            lock (_gate)
            {
                var obj = new System.Text.Json.Nodes.JsonObject();
                foreach (var kv in _index)
                {
                    obj[kv.Key] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["slot"] = kv.Value.Slot,
                        ["savedAt"] = kv.Value.SavedAt.ToString("o"),
                        ["nTokens"] = kv.Value.NTokens,
                        ["sizeBytes"] = kv.Value.SizeBytes
                    };
                }
                json = obj.ToJsonString();
            }
            AppPaths.EnsureConfigDir();
            File.WriteAllText(IndexPath, json);
        }
        catch
        {
            // 索引持久化失败不影响运行
        }
    }

    /// <summary>P2-5：磁盘配额 LRU 清理。当缓存目录占用 &gt; MaxCacheBytes 时，按 SavedAt 升序
    /// 删除最旧的 .bin（连带 .meta.json 与索引条目），直至占用回落到阈值以下。
    /// 由 DoSaveAsync 完成路径 fire-and-forget 触发；单线程执行、异常吞掉，不阻塞主流程。</summary>
    private void TrimToQuota()
    {
        try
        {
            if (!Directory.Exists(_cachePath)) return;
            long totalBytes = 0;
            var files = Directory.GetFiles(_cachePath, "*.bin");
            foreach (var f in files)
            {
                try { totalBytes += new FileInfo(f).Length; } catch { /* 单文件 stat 失败忽略 */ }
            }
            if (totalBytes <= MaxCacheBytes) return;

            // 按索引 SavedAt 升序（最旧优先）；索引缺失的 .bin 视为最旧（fallback 用文件系统 LastWriteTimeUtc）
            var indexed = new List<(string Path, DateTime At)>();
            var orphans = new List<(string Path, DateTime At)>();
            foreach (var f in files)
            {
                var key = Path.GetFileNameWithoutExtension(f);
                DateTime at = DateTime.MinValue;
                bool hasIdx;
                lock (_gate)
                {
                    hasIdx = _index.TryGetValue(key, out var e);
                    if (hasIdx) at = e.SavedAt;
                }
                if (!hasIdx)
                {
                    try { at = new FileInfo(f).LastWriteTimeUtc; } catch { }
                }
                if (hasIdx) indexed.Add((f, at)); else orphans.Add((f, at));
            }
            // 最旧优先：orphans 先删（无索引 = 遗留残留），再按 SavedAt 升序
            var all = orphans.Concat(indexed).OrderBy(x => x.At).ToList();

            int deleted = 0;
            foreach (var (path, _) in all)
            {
                if (totalBytes <= MaxCacheBytes) break;
                long len = 0;
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists)
                    {
                        len = fi.Length;
                        fi.Delete();
                    }
                }
                catch { /* 单文件删除失败忽略 */ }
                // 同 key 元数据也删
                try
                {
                    var meta = Path.ChangeExtension(path, ".meta.json");
                    if (File.Exists(meta)) File.Delete(meta);
                }
                catch { /* 忽略 */ }
                var key = Path.GetFileNameWithoutExtension(path);
                lock (_gate) _index.Remove(key);
                totalBytes -= len;
                deleted++;
            }
            if (deleted > 0)
            {
                SaveIndex();
                _log?.Invoke($"[KV-QUOTA] 缓存目录超过 {MaxCacheBytes / (1L << 20)} MB，按 LRU 清理 {deleted} 个旧快照");
            }
        }
        catch
        {
            // 配额清理是尽力而为；异常不影响主流程
        }
    }

    /// <summary>key 转文件名安全字符（防路径注入）。B-2 修复：空串/全非法字符必须显式失败，
    /// 否则不同业务 key 会全部落到同一个 `.bin`，导致快照互相覆盖、缓存污染。
    /// P2-3：控制字符（\u0000-\u001F）、Unicode 类别 Cf（格式字符，如 ZWJ/BOM）以及字节长度上限一并过滤，
    /// 防 Windows/Linux 路径穿越 / NTFS 保留名 / 超长文件名被截断后撞名。</summary>
    private static string Sanitize(string key)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("key 不能为空", nameof(key));
        var invalid = Path.GetInvalidFileNameChars();
        // P2-3：控制字符（Cc 类，含 \0-\x1F 与 DEL 0x7F）+ Unicode Cf（Zero Width Joiner、BOM、Directional Mark 等）
        char[] filtered = new char[key.Length];
        int w = 0;
        for (int i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (invalid.Contains(c) || c == '/' || c == '\\') continue;
            if (c < 0x20 || c == 0x7F) continue;                    // Cc 控制字符
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) continue; // Cf
            filtered[w++] = c;
        }
        var result = new string(filtered, 0, w);
        if (result.Length == 0)
            throw new ArgumentException($"key '{key}' 全部为非法字符，无法作为快照文件名", nameof(key));
        // P2-3：字节长度上限 128（UTF-8 计字节，非字符数），超出按字节截断
        // —— 防超长 key 撑爆文件系统名长度限制（NTFS 255 UTF-16 code units，但为跨平台留余量）
        const int MaxBytes = 128;
        int used = 0;
        int cut = result.Length;
        for (int i = 0; i < result.Length; i++)
        {
            int cb = Encoding.UTF8.GetByteCount(result.AsSpan(i, 1));
            if (used + cb > MaxBytes) { cut = i; break; }
            used += cb;
        }
        if (cut < result.Length)
            result = result.Substring(0, Math.Max(1, cut)); // 保底不空
        return result;
    }
}
