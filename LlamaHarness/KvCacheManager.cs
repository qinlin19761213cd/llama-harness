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
    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly int _slotCount;
    private readonly int _backendPort;
    private readonly int _ctxSize;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, Task> _inflightSaves = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>缓存索引持久化文件（exe 同目录）。</summary>
    /// <summary>KV Cache 索引路径：项目目录下 config/kv_cache_index.json。</summary>
    private static readonly string IndexPath = AppPaths.KvCacheIndexJson;

    /// <summary>key → (slot, savedAt, nTokens, sizeBytes)。</summary>
    private readonly Dictionary<string, CacheEntry> _index = new(StringComparer.OrdinalIgnoreCase);

    internal struct CacheEntry
    {
        public int Slot;
        public DateTime SavedAt;
        public int NTokens;
        public long SizeBytes;
    }

    public KvCacheManager(HttpClient http, string cachePath, int slotCount, int backendPort, int ctxSize = 0, Action<string>? log = null)
    {
        _http = http;
        _cachePath = cachePath.TrimEnd('/');
        _slotCount = Math.Max(1, slotCount);
        _backendPort = backendPort;
        _ctxSize = ctxSize;
        _log = log;
        LoadIndex();
    }

    private string SlotUrl(int slot, string action) => $"http://localhost:{_backendPort}/slots/{slot}?action={action}";

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
        lock (_gate)
        {
            if (_inflightSaves.TryGetValue(key, out var existing))
                return existing; // 复用进行中的 save
            var task = DoSaveAsync(slot, key, ct);
            _inflightSaves[key] = task;
            return task;
        }
    }

    private async Task DoSaveAsync(int slot, string key, CancellationToken ct)
    {
        try
        {
            var body = new { filename = $"{Sanitize(key)}.bin" };
            var json = JsonSerializer.Serialize(body);
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            var resp = await _http.PostAsync(SlotUrl(slot, "save"), content, ct);
            var text = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode)
            {
                // 解析响应：n_saved / n_written
                int nSaved = 0, nWritten = 0;
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("n_saved", out var ns)) nSaved = ns.GetInt32();
                    if (root.TryGetProperty("n_written", out var nw)) nWritten = nw.GetInt32();
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
        finally
        {
            lock (_gate)
            {
                _inflightSaves.Remove(key);
            }
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
    /// </summary>
    public async Task<bool> RestoreAsync(int slot, string key)
    {
        // 等待进行中的 save 完成（防 save/restore 竞态）
        Task? saveTask = null;
        lock (_gate)
        {
            if (_inflightSaves.TryGetValue(key, out var t)) saveTask = t;
        }
        if (saveTask != null)
        {
            try { await saveTask; } catch { /* save 失败不影响 restore 尝试 */ }
        }

        // restore 前置校验：快照文件/元数据损坏 → [EDGE-CASE-SNAPSHOT-CORRUPT] + 废弃（全量 prefill 兜底）
        if (!ValidateSnapshot(key)) return false;

        var body = new { filename = $"{Sanitize(key)}.bin" };
        var json = JsonSerializer.Serialize(body);
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        var resp = await _http.PostAsync(SlotUrl(slot, "restore"), content, CancellationToken.None);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>擦除槽位 KV（不删缓存文件）。</summary>
    public async Task<bool> EraseAsync(int slot)
    {
        var resp = await _http.PostAsync(SlotUrl(slot, "erase"), null, CancellationToken.None);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>
    /// 清空缓存：删除缓存目录下所有 *.bin + erase 全部槽位。
    /// </summary>
    public async Task<int> ClearAllAsync()
    {
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

        // O-17：_index 变更统一在 lock(_gate) 内（与 RecordSave/Snapshot/LoadIndex 一致）
        lock (_gate)
        {
            _index.Clear();
            SaveIndex();
        }
        return deleted;
    }

    /// <summary>记录 save 成功（更新索引）。</summary>
    private void RecordSave(string key, int slot, int nTokens, long sizeBytes)
    {
        lock (_gate)
        {
            _index[key] = new CacheEntry { Slot = slot, SavedAt = DateTime.Now, NTokens = nTokens, SizeBytes = sizeBytes };
            SaveIndex();
        }
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
                int slot = -1;
                string savedAt = "";
                int nTokens = 0, sizeBytes = 0;
                if (prop.Value.TryGetProperty("slot", out var s)) slot = s.GetInt32();
                if (prop.Value.TryGetProperty("savedAt", out var sa)) savedAt = sa.GetString() ?? "";
                if (prop.Value.TryGetProperty("nTokens", out var nt)) nTokens = nt.GetInt32();
                if (prop.Value.TryGetProperty("sizeBytes", out var sb)) sizeBytes = sb.GetInt32();
                if (!DateTime.TryParse(savedAt, out var dt)) dt = DateTime.Now.AddDays(-30);
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
            AppPaths.EnsureConfigDir();
            File.WriteAllText(IndexPath, obj.ToJsonString());
        }
        catch
        {
            // 索引持久化失败不影响运行
        }
    }

    /// <summary>key 转文件名安全字符（防路径注入）。</summary>
    private static string Sanitize(string key)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(key.Where(c => !invalid.Contains(c) && c != '/' && c != '\\').ToArray());
    }
}
