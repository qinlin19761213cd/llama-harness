using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LlamaHarness;

/// <summary>
/// Token Guard：代理层 token 预估算 + 裁剪，防 "request exceeds context size" 400 错误。
/// - 计数：POST /tokenize（优先 /v1/tokenize 兼容旧版，404 回退 /tokenize）到后端 llama-server（真实分词器，本地毫秒级）
/// - 预算：CtxSize ÷ Parallel − ReservedOutputTokens（多槽均分总容量）
/// - 裁剪：轮次制（整轮删除最旧对话，保证 tool_call/tool_result 配对完整）
///   + 内容兜底（单条超大消息如巨型 tool_result 做字符级截断）
/// - 降级：tokenize 失败 → 默认原样转发不阻断（fail-open）；400 自愈等兜底场景 fail-closed（字符级估算继续裁剪）
/// </summary>
public static class TokenGuard
{
    private const int MinTrimLen = 50;
    private const int MinTrimContentLen = 200;
    private const int GuardTimeoutSeconds = 30;
    /// <summary>经后端 tokenize 端点计数 token。优先 /v1/tokenize（OpenAI 兼容旧路径），失败回退 /tokenize（llama.cpp b10676+ 新路径）。
    /// 故障实证：b10676 已移除 /v1/tokenize（实测 404），tokenize 迁移到 /tokenize；双路径保证新旧版本兼容。
    /// 每次失败输出 [TOKEN-GUARD-WARN] 诊断日志（HTTP 码/异常），消除"只见 FAILED 不知为何"的盲区。全部失败返回 null（调用方降级）。</summary>
    /// <summary>经后端 tokenize 端点计数 token（双路径容错已下沉到 IBackendClient.TokenizeAsync）。失败返回 null（调用方降级）。</summary>
    public static async Task<int?> CountTokensAsync(IBackendClient backend, string text)
    {
        // 问题 16 修复：入参 null 早退。backend=null 抛 ArgumentNullException（调用方编程错误）；
        // text 空返回 0（无需 tokenize，避免无谓 HTTP 往返与超时）。
        ArgumentNullException.ThrowIfNull(backend);
        if (string.IsNullOrEmpty(text)) return 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(GuardTimeoutSeconds));
        return await backend.TokenizeAsync(text, cts.Token);
    }

    /// <summary>
    /// 计量入口（每次调用强制输出 [TOKEN-GUARD] 日志，消除排查盲区）：
    /// 先 tokenize 消息文本得到 msg_est，输出计量日志，再委托 GuardAsync 执行裁剪。
    /// 返回 (Ok, Modified, Note)。
    /// </summary>
    public static async Task<(bool Ok, bool Modified, string? Note)> MeasureAsync(
        JsonObject root, IBackendClient backend, int budget,
        int reservedOutput, int reservedOverhead,
        Func<string, Task<int?>>? countTokens = null)
    {
        // 问题 16 修复：MeasureAsync 参数 null/负数早退。root/backend 抛 ArgumentNullException，budget<=0 抛 ArgumentOutOfRangeException。
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(backend);
        if (budget <= 0) throw new ArgumentOutOfRangeException(nameof(budget), budget, "budget 必须为正数");
        var messages = root["messages"] as JsonArray;
        if (messages == null || messages.Count == 0) return (true, false, null);

        Func<string, Task<int?>> counter = countTokens ?? ((string t) => CountTokensAsync(backend, t));
        int msgEst = await counter(BuildMessagesText(messages)) ?? -1;

        // 强制计量日志（通过 note 回传给调用方统一输出，避免 Console.WriteLine 污染 stdout）
        string logLine = msgEst >= 0
            ? $"[TOKEN-GUARD] budget={budget}, msg_est={msgEst}, reserved_out={reservedOutput}, reserved_overhead={reservedOverhead}"
            : $"[TOKEN-GUARD] budget={budget}, msg_est=FAILED(tokenize), reserved_out={reservedOutput}, reserved_overhead={reservedOverhead}";

        var (ok, modified, note) = await GuardAsync(root, backend, budget, counter);
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
        JsonObject root, IBackendClient backend, int budget,
        Func<string, Task<int?>>? countTokens = null, bool failOpenOnTokenizeError = true)
    {
        // 问题 16 修复：GuardAsync 参数 null/负数早退（与 MeasureAsync 一致）。
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(backend);
        if (budget <= 0) throw new ArgumentOutOfRangeException(nameof(budget), budget, "budget 必须为正数");
        var messages = root["messages"] as JsonArray;
        if (messages == null || messages.Count == 0) return (true, false, null);

        // tokenize 失败时：failOpenOnTokenizeError=true（默认）→ 返回 null 触发降级放行（正常热路径保持 fail-open）；
        // =false（400 自愈兜底）→ 退化为字符级保守估算，保证裁剪决策仍有依据，禁止未裁剪穿透死循环
        Func<string, Task<int?>> rawCounter = countTokens ?? ((string t) => CountTokensAsync(backend, t));
        Func<string, Task<int?>> counter = async t =>
        {
            int? n = await rawCounter(t);
            if (n != null) return n;
            return failOpenOnTokenizeError ? null : EstimateTokensByChars(t);
        };

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
        IBackendClient backend, string body, int budget,
        Func<string, Task<int?>>? countTokens = null)
    {
        // 问题 16 修复：string 版 GuardAsync 参数校验。backend null 抛 ArgumentNullException；
        // body 空串仍走 Parse 走透传（原语义），但 body null 抛 ArgumentNullException（避免 JsonNode.Parse(null) 抛 NRE）。
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(body);
        if (budget <= 0) throw new ArgumentOutOfRangeException(nameof(budget), budget, "budget 必须为正数");
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

        var (ok, modified, note) = await GuardAsync(root, backend, budget, countTokens);
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

    /// <summary>字符级保守估算 token 数（tokenize 端点不可用时的兜底口径）：CJK 字符≈1 token、emoji/surrogate pair≈2 token、其余≈len/4，取偏保守（宁多裁不 400）。</summary>
    public static int EstimateTokensByChars(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int cjk = 0, emoji = 0, other = 0;
        foreach (char ch in text)
        {
            // CJK Unified Ideographs（基本区）
            if (ch >= 0x4E00 && ch <= 0x9FFF) { cjk++; continue; }
            // Emoji / Symbols / surrogate pairs（多 char 组成的 Unicode 字符）
            // 覆盖: 0x1F000-0x1FFFF (emoji), 0x2600-0x27FF (symbols), 0xFE00-0xFE0F (variation), 0x20000+ (surrogate high/low)
            if ((ch >= 0x1F000 && ch <= 0x1FFFF) || (ch >= 0x2600 && ch <= 0x27FF) ||
                (ch >= 0xFE00 && ch <= 0xFE0F) || (ch >= 0x20000 && ch <= 0x2FFFF) ||
                (ch >= 0xD800 && ch <= 0xDBFF)) // surrogate high → 标记为 emoji（low surrogate 后续跳过）
            {
                emoji++;
                continue;
            }
            other++;
        }
        // M-04 修复：emoji/surrogate pair 每个约 2 token，防止低估；CJK 1:1；其余 4:1
        return Math.Max(1, cjk + emoji * 2 + other / 4);
    }
}
