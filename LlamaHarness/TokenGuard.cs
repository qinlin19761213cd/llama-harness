using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LlamaHarness;

/// <summary>
/// Token Guard：代理层 token 预估算 + 裁剪，防 "request exceeds context size" 400 错误。
/// - 计数：POST /v1/tokenize 到后端 llama-server（真实分词器，本地毫秒级）
/// - 预算：CtxSize ÷ Parallel − ReservedOutputTokens（多槽均分总容量）
/// - 裁剪：轮次制（整轮删除最旧对话，保证 tool_call/tool_result 配对完整）
///   + 内容兜底（单条超大消息如巨型 tool_result 做字符级截断）
/// - 降级：tokenize 失败 → 原样转发不阻断；无 user 消息 → 透传
/// </summary>
public static class TokenGuard
{
    private const int MinTrimLen = 50;
    private const int MinTrimContentLen = 200;
    private const int GuardTimeoutSeconds = 30;
    /// <summary>经后端 /v1/tokenize 端点计数 token。失败返回 null（调用方降级原样转发）。</summary>
    public static async Task<int?> CountTokensAsync(HttpClient hc, int port, string text)
    {
        try
        {
            var payload = new JsonObject { ["content"] = text };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{port}/v1/tokenize")
            {
                Content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(GuardTimeoutSeconds));
            using var resp = await hc.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            // 兼容两种响应格式：{"tokens":[...]}（数数组长度）/ {"n_tokens":N}
            if (root.TryGetProperty("tokens", out var toks) && toks.ValueKind == JsonValueKind.Array)
                return toks.GetArrayLength();
            if (root.TryGetProperty("n_tokens", out var n) && n.TryGetInt32(out var v))
                return v;
            return null;
        }
        catch
        {
            return null; // 后端忙 / 超时：降级
        }
    }

    /// <summary>
    /// 计量入口（每次调用强制输出 [TOKEN-GUARD] 日志，消除排查盲区）：
    /// 先 tokenize 消息文本得到 msg_est，输出计量日志，再委托 GuardAsync 执行裁剪。
    /// 返回 (Ok, Modified, Note)。
    /// </summary>
    public static async Task<(bool Ok, bool Modified, string? Note)> MeasureAsync(
        JsonObject root, HttpClient hc, int backendPort, int budget,
        int reservedOutput, int reservedOverhead,
        Func<string, Task<int?>>? countTokens = null)
    {
        var messages = root["messages"] as JsonArray;
        if (messages == null || messages.Count == 0) return (true, false, null);

        Func<string, Task<int?>> counter = countTokens ?? ((string t) => CountTokensAsync(hc, backendPort, t));
        int msgEst = await counter(BuildMessagesText(messages)) ?? -1;

        // 强制计量日志（不管是否裁剪都输出，供 Streamlit+DuckDB 统计）
        string logLine = msgEst >= 0
            ? $"[TOKEN-GUARD] budget={budget}, msg_est={msgEst}, reserved_out={reservedOutput}, reserved_overhead={reservedOverhead}"
            : $"[TOKEN-GUARD] budget={budget}, msg_est=FAILED(tokenize), reserved_out={reservedOutput}, reserved_overhead={reservedOverhead}";
        Console.WriteLine(logLine);

        var (ok, modified, note) = await GuardAsync(root, hc, backendPort, budget, counter);
        // 合并计量信息到 note（调用方统一输出）
        if (note != null) return (ok, modified, $"{logLine}\n{note}");
        return (ok, modified, logLine);
    }

    /// <summary>
    /// 核心实现（DOM 版，E-1）：原地裁剪 root["messages"]，无中间 parse/serialize。
    /// 热路径（PrepareGatewayAsync）复用同一棵 DOM，管道末端统一序列化一次。
    /// 返回 (Ok, Modified, Note)：Modified=false → 调用方可直接用原 body；true → 需序列化 root。
    /// countTokens 可注入（单测用假计数器）；默认走后端 /v1/tokenize。
    /// </summary>
    public static async Task<(bool Ok, bool Modified, string? Note)> GuardAsync(
        JsonObject root, HttpClient hc, int backendPort, int budget,
        Func<string, Task<int?>>? countTokens = null)
    {
        var messages = root["messages"] as JsonArray;
        if (messages == null || messages.Count == 0) return (true, false, null);

        Func<string, Task<int?>> counter = countTokens ?? ((string t) => CountTokensAsync(hc, backendPort, t));

        // O-14：计数口径对齐——送 messages 文本（role+content）tokenize，而非原始 body（含 model/temperature 等非上下文字段，计数偏高）
        int count = await counter(BuildMessagesText(messages)) ?? -1;
        if (count < 0) return (true, false, null); // tokenize 失败：降级原样转发
        int origCount = count;
        if (count <= budget) return (true, false, null);

        // ── 轮次制裁剪 ──
        // 一轮 = user 消息 + 其后到下一个 user 之前的 assistant/tool 消息（整体删除，保 tool_call 配对）。
        // 最小保留集：首个 user 之前的全部消息（system 等）+ 最后一轮（最后 user → 末尾）。
        int firstUser = FirstIndexOfRole(messages, "user");
        int lastUser = LastIndexOfRole(messages, "user");
        if (firstUser < 0) return (true, false, null); // 无 user 消息：无可裁

        var turnStarts = new List<int>();
        for (int i = firstUser; i <= lastUser; i++)
            if (RoleOf(messages[i]!) == "user") turnStarts.Add(i);

        // ── 轮次制裁剪（E-2 二分收敛）──
        // 旧实现每删一轮全量重 tokenize（最坏 K+1 次 HTTP 往返，串行阻塞关键路径）。
        // 现：按轮预切分，试评估只在索引区间上拼计数文本（零节点搬移），
        // 二分 ≤ log₂(轮数)+1 次 tokenize 收敛；收敛后批量破坏性删除。
        int maxDelete = turnStarts.Count - 1; // 必须保留最后一轮
        int deletedTurns = 0;
        if (maxDelete > 0)
        {
            // 试评估 f(k) = "前缀(首个 user 前) + turns[k..]" 的 token 数：跳过前 k 轮的索引区间 [turnStarts[0], turnStarts[k])
            async Task<int?> EvalK(int k)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < turnStarts[0]; i++) AppendMsgText(sb, messages[i]);
                for (int i = turnStarts[k]; i < messages.Count; i++) AppendMsgText(sb, messages[i]);
                return await counter(sb.ToString());
            }

            // 先验极端：删光全部可删轮仍超预算 → 无 K 可行，删到最小集进内容兜底
            int countMax = await EvalK(maxDelete) ?? -1;
            if (countMax < 0) return (true, false, null); // tokenize 失败：降级为未修改状态
            if (countMax <= budget)
            {
                // 二分求 [1, maxDelete] 中满足预算的最小 K（token 数随 K 单调不增）
                int lo = 1, hi = maxDelete;
                int finalCount = countMax; // f(maxDelete) 已知可行
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    int c = await EvalK(mid) ?? -1;
                    if (c < 0) return (true, false, null); // tokenize 失败：降级为未修改状态
                    if (c <= budget) { hi = mid; finalCount = c; }
                    else lo = mid + 1;
                }
                deletedTurns = hi;
                count = finalCount;
            }
            else
            {
                deletedTurns = maxDelete;
                count = countMax;
            }

            // 批量破坏性删除前 deletedTurns 轮（与 EvalK 计数口径一致，无需再 tokenize）
            for (int t = deletedTurns - 1; t >= 0; t--)
            {
                int start = turnStarts[t];
                int end = turnStarts[t + 1]; // 下一轮起点（不含）
                for (int i = end - 1; i >= start; i--) messages.RemoveAt(i);
            }
        }

        // ── 内容兜底（E-2 二分收敛）── 最小集仍超 → 截断最大消息内容（巨型 tool_result 等）
        // 保留比例取线性估算 budget/count；一轮后仍超则比例减半（二分步），≤5 轮收敛（旧实现固定 ratio 迭代最多 10 轮）
        bool contentTruncated = false;
        double retain = Math.Max(0.1, (double)budget / count);
        for (int iter = 0; iter < 5 && count > budget; iter++)
        {
            int maxIdx = IndexOfLargestContent(messages);
            string? content = maxIdx >= 0 ? GetContent(messages[maxIdx]!) : null;
            if (content == null || content.Length < MinTrimContentLen) break; // 无可再裁的内容
            int newLen = Math.Max(MinTrimLen, (int)(content.Length * retain));
            // O-14：头尾双保留（头部留上下文、尾部留最新信息），替代纯头部截断
            int half = newLen / 2;
            int tail = newLen - half;
            string kept = content[..half] + "\n[…]\n" + content[^tail..];
            SetContent(messages[maxIdx]!, kept + "\n[已截断 - Token Guard]");
            contentTruncated = true;
            count = await counter(BuildMessagesText(messages)) ?? -1;
            if (count < 0) return (true, deletedTurns > 0 || contentTruncated, null);
            retain = Math.Max(0.1, retain / 2); // 仍超 → 保留比例减半（二分步）
        }

        bool modified = deletedTurns > 0 || contentTruncated;
        if (count > budget)
        {
            var err = $"Token Guard：裁剪后仍 {count} tokens，超预算 {budget}。请缩短输入。";
            return (false, modified, err);
        }

        var note = $"Token Guard：估算 {origCount} tokens > 预算 {budget}，删除 {deletedTurns} 轮对话，最终 {count} tokens";
        return (true, modified, note);
    }

    /// <summary>
    /// 主入口（string 版，供崩溃恢复/续接等非热路径）：parse 一次 → 核心实现 → 按需序列化一次。
    /// 预算内 → (true, 原body, null)；裁剪成功 → (true, 新body, 日志说明)；
    /// 最小集仍超预算 → (false, null, 错误信息)，调用方返回 400。
    /// </summary>
    public static async Task<(bool Ok, string? Body, string? Note)> GuardAsync(
        HttpClient hc, int backendPort, string body, int budget,
        Func<string, Task<int?>>? countTokens = null)
    {
        // 解析 body 提取 messages（非 JSON / 无 messages → 透传）
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(body)?.AsObject();
        }
        catch
        {
            return (true, body, null);
        }
        if (root == null) return (true, body, null);

        var (ok, modified, note) = await GuardAsync(root, hc, backendPort, budget, countTokens);
        return (ok, ok ? (modified ? root.ToJsonString() : body) : null, note);
    }

    // ── 辅助 ──

    /// <summary>O-14：构造送 tokenize 的计数文本——逐条拼接 role + content（与上下文消耗口径对齐）。</summary>
    private static string BuildMessagesText(JsonArray messages)
    {
        var sb = new StringBuilder();
        foreach (var m in messages) AppendMsgText(sb, m);
        return sb.ToString();
    }

    /// <summary>把单条消息的 role+content 追加到计数文本（与 BuildMessagesText 同口径；二分试评估复用）。</summary>
    private static void AppendMsgText(StringBuilder sb, JsonNode? msg)
    {
        var o = msg?.AsObject();
        if (o == null) return;
        var role = o["role"]?.GetValue<string>() ?? "";
        var c = o["content"];
        string? content = null;
        try { content = c?.GetValue<string>(); } catch { /* 数组型 content */ }
        sb.Append(role).Append(": ").Append(content ?? c?.ToJsonString() ?? "").Append("\n");
    }

    /// <summary>取消息 role 字段；null = 非对象。</summary>
    private static string? RoleOf(JsonNode msg) => msg?.AsObject()?["role"]?.GetValue<string>();

    private static int FirstIndexOfRole(JsonArray arr, string role)
    {
        for (int i = 0; i < arr.Count; i++)
            if (RoleOf(arr[i]!) == role) return i;
        return -1;
    }

    private static int LastIndexOfRole(JsonArray arr, string role)
    {
        for (int i = arr.Count - 1; i >= 0; i--)
            if (RoleOf(arr[i]!) == role) return i;
        return -1;
    }

    /// <summary>取消息的文本 content（string 类型）；null = 无可裁剪内容（数组型多模态等不裁）。</summary>
    private static string? GetContent(JsonNode msg)
    {
        var c = msg?.AsObject()?["content"];
        if (c == null) return null;
        try
        {
            return c.GetValue<string>();
        }
        catch
        {
            return null; // 数组型 content：不裁
        }
    }

    private static void SetContent(JsonNode msg, string value) => msg.AsObject()["content"] = value;

    /// <summary>找可裁剪内容最长的消息下标；-1 = 无。</summary>
    private static int IndexOfLargestContent(JsonArray arr)
    {
        int best = -1, bestLen = 0;
        for (int i = 0; i < arr.Count; i++)
        {
            var c = GetContent(arr[i]!);
            if (c != null && c.Length > bestLen) { bestLen = c.Length; best = i; }
        }
        return best;
    }
}
