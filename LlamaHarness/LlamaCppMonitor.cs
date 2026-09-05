using System.Text.Json;

namespace LlamaHarness;

/// <summary>
/// llama.cpp 手动采集的完整监控快照，一次触发生成一份。
/// 保留 Raw 原始报文，方便后续调试、排查。
/// </summary>
public class LlamaCppMonitorSnapshot
{
    /// <summary>快照采集时间</summary>
    public DateTime CaptureAt { get; set; }

    /// <summary>/props 原始 json 字符串</summary>
    public string RawPropsJson { get; set; } = "";
    /// <summary>/slots 原始 json 字符串</summary>
    public string RawSlotsJson { get; set; } = "";
    /// <summary>/metrics 原始文本</summary>
    public string RawMetricsText { get; set; } = "";

    /// <summary>解析之后的槽位信息</summary>
    public List<LlamaSlotInfo> Slots { get; set; } = new();
    /// <summary>全局属性</summary>
    public LlamaGlobalProps GlobalProps { get; set; } = new();
}

/// <summary>单个槽位信息，映射 /slots 接口返回。</summary>
public class LlamaSlotInfo
{
    public int id { get; set; }
    public long id_task { get; set; }
    public string state_name { get; set; } = "";
    public int n_ctx { get; set; }
    public int tokens_cached { get; set; }
    public bool is_processing { get; set; }
    public bool speculative { get; set; }
    public double pp_tps { get; set; }
    public double tg_tps { get; set; }
}

/// <summary>/props 全局配置（llama-server 返回的全部字段）。</summary>
public class LlamaGlobalProps
{
    public int total_slots { get; set; }
    public int ctx_size { get; set; }
    public string model_path { get; set; } = "";
    public string model { get; set; } = "";
    public string model_string { get; set; } = "";
    public string seed { get; set; } = "";
    public string generation_seed { get; set; } = "";
    public int image_model_size { get; set; }
    public int n_gpu_layers { get; set; }
    public int main_gpu { get; set; }
    public int flash_attn { get; set; }
    public int rope_freq_base { get; set; }
    public int rope_freq_sliding { get; set; }
    public int n_ctx { get; set; }
    public int n_batch { get; set; }
    public int n_ubatch { get; set; }
    public int n_threads { get; set; }
    public int n_threads_batch { get; set; }
    public int n_regex { get; set; }
    public int max_batch_size { get; set; }
    public int cache_k_size { get; set; }
    public int no_perf_time_report { get; set; }
    public string name { get; set; } = "";
    public string description { get; set; } = "";
    public string author { get; set; } = "";
    public string license { get; set; } = "";
    public string architecture { get; set; } = "";
    public string parameters { get; set; } = "";
    public string embedding_size { get; set; } = "";
    public string vocabulary_size { get; set; } = "";
    public string modality { get; set; } = "";
    public string build_info { get; set; } = "";
    public string chat_template { get; set; } = "";
    /// <summary>原始 JSON 全部字段（key→value），用于展示未映射到强类型字段的属性。</summary>
    public Dictionary<string, string> RawFields { get; set; } = new();
}

/// <summary>
/// llama.cpp 手动采集服务：点击/调用才拉取一次 /slots、/props、/metrics，无后台轮询。
/// 三个接口独立容错——任一失败不影响其他接口的数据。
/// </summary>
public class LlamaCppMonitorCollector
{
    private const int ProbeTimeoutSeconds = 8;
    private readonly IBackendClient _backend; // v2.26：收敛到 LlamaServerClient（HttpClient 唯一化），探测超时由 cts 控制

    public LlamaCppMonitorCollector(string baseAddress)
    {
        _backend = new LlamaServerClient(baseAddress); // 端口可变场景：Monitor 独立持有后端客户端，调用方零改动

    }

    /// <summary>
    /// 【手动触发】采集一次完整快照。三个接口并行请求，各自独立容错：
    /// - 某接口失败时，对应 Raw 字段为空、解析结果为空/默认值，不抛异常；
    /// - 调用方可通过 <see cref="LlamaCppMonitorSnapshot"/> 的 Raw 字段是否为空判断各接口成功与否。
    /// </summary>
    // P0-H-05 修复：为每个 HTTP 请求创建独立的 CancellationTokenSource，避免共享 CTS 导致连带取消和资源泄漏
    public async Task<LlamaCppMonitorSnapshot> CaptureSnapshotAsync(CancellationToken ct = default)
    {
        var snapshot = new LlamaCppMonitorSnapshot { CaptureAt = DateTime.Now };

        // 探测超时：对齐原 HttpClient.Timeout=8s（后端不可用时不阻塞 UI/采样循环）
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(ProbeTimeoutSeconds));

        // P0-H-05 修复：每个请求独立 CTS + 链接父 token，避免共享 CTS 导致连带取消
        // [P0-H3 修复] 三个 CTS 用 using，保证 CaptureSnapshotAsync 完成后 CancellationTokenSource 及时 Dispose，
        // 释放内核 ManualResetEvent 句柄和对父 timeoutCts.Token 的回调注册，防止高频调用时句柄缓慢堆积。
        using var slotsCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
        using var propsCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
        using var metricsCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);

        // 三个接口并行请求（各自独立容错，GetRawText 保留原始 JSON 文本）
        var slotsTask = _backend.GetSlotsAsync(slotsCts.Token);
        var propsTask = _backend.GetPropsAsync(propsCts.Token);
        var metricsTask = _backend.GetMetricsAsync(metricsCts.Token);
        
        try
        {
            await Task.WhenAll(slotsTask, propsTask, metricsTask);
        }
        catch
        {
            // 任一请求失败：不影响其他请求的后续处理
        }

        // /slots：解析为槽位列表
        JsonDocument? slotsDoc = null;
        try
        {
            slotsDoc = await slotsTask.ConfigureAwait(false);
        }
        catch { /* 解析失败保留 Raw，Slots 保持空列表 */ }
        
        if (slotsDoc != null)
        {
            using (slotsDoc) // P0-H-05 修复：using 包裹确保 JsonDocument 释放
            {
                snapshot.RawSlotsJson = slotsDoc.RootElement.GetRawText();
                try
                {
                    snapshot.Slots = JsonSerializer.Deserialize<List<LlamaSlotInfo>>(snapshot.RawSlotsJson) ?? new();
                }
                catch
                {
                    // 解析失败保留 Raw，Slots 保持空列表
                }
            }
        }

        // /props：解析为全局配置 + 遍历全部字段
        JsonDocument? propsDoc = null;
        try
        {
            propsDoc = await propsTask.ConfigureAwait(false);
        }
        catch { /* 解析失败保留 Raw，GlobalProps 保持默认值 */ }
        
        if (propsDoc != null)
        {
            using (propsDoc) // P0-H-05 修复：using 包裹确保 JsonDocument 释放
            {
                snapshot.RawPropsJson = propsDoc.RootElement.GetRawText();
                try
                {
                    snapshot.GlobalProps = JsonSerializer.Deserialize<LlamaGlobalProps>(snapshot.RawPropsJson) ?? new();
                    // 递归展开原始 JSON 全部字段（含嵌套），存入 RawFields（key.path → value）
                    FlattenJson(propsDoc.RootElement, "", snapshot.GlobalProps.RawFields);
                }
                catch
                {
                    // 解析失败保留 Raw，GlobalProps 保持默认值
                }
            }
        }

        // /metrics：Prometheus 文本格式，直接保留原文
        try
        {
            snapshot.RawMetricsText = await metricsTask.ConfigureAwait(false) ?? "";
        }
        catch { /* 失败保留空字符串 */ }

        return snapshot;
    }


    /// <summary>递归展开 JSON 对象为扁平 key.path → value 字典（嵌套对象/数组用 . 连接）。</summary>
    private static void FlattenJson(JsonElement element, string prefix, Dictionary<string, string> result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                var key = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
                if (prop.Value.ValueKind == JsonValueKind.Object || prop.Value.ValueKind == JsonValueKind.Array)
                {
                    // 嵌套对象/数组：递归展开
                    FlattenJson(prop.Value, key, result);
                }
                else
                {
                    // 叶子节点：直接存入
                    result[key] = prop.Value.ToString();
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            for (int i = 0; i < element.GetArrayLength(); i++)
            {
                var key = $"{prefix}[{i}]";
                if (element[i].ValueKind == JsonValueKind.Object || element[i].ValueKind == JsonValueKind.Array)
                {
                    FlattenJson(element[i], key, result);
                }
                else
                {
                    result[key] = element[i].ToString();
                }
            }
        }
    }

    /// <summary>释放 HttpClient 资源。</summary>
    public void Dispose() => _backend.Dispose();
}
