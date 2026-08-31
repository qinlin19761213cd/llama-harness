using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace LlamaHarness;

/// <summary>
/// 网关请求处理的纯静态工具集（零实例依赖，可独立单测）：
/// 请求体读取 / 后端请求构造 / 响应写出 / 推理请求判定 / 前缀指纹 / 工具循环检测 / 强制流式改写。
/// 原属 SmartScheduler（v2.15 重构迁出），方法体逐字迁移，行为等价。
/// </summary>
public static class RequestProcessor
{
    /// <summary>读取请求体字节（仅 POST；GET 返回 null）。AH-5：超 maxBytes 抛 InvalidDataException（调用方回 413，防本机恶意大 body 内存 DoS）。</summary>
    public static async Task<byte[]?> ReadRequestBodyAsync(HttpListenerRequest req, int maxBytes)
    {
        if (!string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            return null;
        if (maxBytes <= 0) maxBytes = 64 * 1024 * 1024; // 安全兜底
        if (req.ContentLength64 > maxBytes) // 预检 Content-Length（有声明时）
            throw new InvalidDataException($"请求体过大（Content-Length {req.ContentLength64} 超过上限 {maxBytes} 字节）。");
        using var ms = new MemoryStream();
        byte[] buf = new byte[81920];
        long total = 0;
        while (true)
        {
            int n = await req.InputStream.ReadAsync(buf, 0, buf.Length);
            if (n <= 0) break;
            total += n;
            if (total > maxBytes)
                throw new InvalidDataException($"请求体过大（超过上限 {maxBytes} 字节）。");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    /// <summary>构造后端 HttpRequestMessage：body 走内容头，Host/长度/编码等逐跳头由 HttpClient 处理，其余原样复制（个别特殊头复制失败忽略）。</summary>
    public static HttpRequestMessage BuildBackendRequest(HttpListenerRequest req, Uri uri, byte[]? bodyBytes)
    {
        var msg = new HttpRequestMessage(new HttpMethod(req.HttpMethod), uri);
        if (bodyBytes != null)
        {
            msg.Content = new ByteArrayContent(bodyBytes);
            // Content-Type 走内容头，避免与消息级头重复
            if (!string.IsNullOrEmpty(req.ContentType))
                msg.Content.Headers.ContentType = new MediaTypeHeaderValue(req.ContentType);
        }
        foreach (string key in req.Headers)
        {
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue; // 已在内容头上显式设置
            try
            {
                msg.Headers.TryAddWithoutValidation(key, req.Headers[key]);
            }
            catch
            {
                // 个别特殊头无法原样复制，忽略
            }
        }
        return msg;
    }

    /// <summary>前缀指纹（E-4 轻量版）：消息条数 + 各条 role|content长度 序列，零全量序列化、零 SHA256。
    /// 旧实现对除末条外全部 messages 做 ToJsonString + SHA256（大上下文每请求数 MB 开销），仅用于 [KV-HIT]/[KV-MISS] 日志判定；
    /// 轻量指纹的碰撞概率对该场景可接受（误 HIT 只影响日志，不影响实际 KV 行为）。null = 无状态单轮请求（无比对基线）。</summary>
    public static string? PrefixHash(JsonObject obj)
    {
        try
        {
            var msgs = obj["messages"] as System.Text.Json.Nodes.JsonArray;
            if (msgs == null || msgs.Count < 2) return null;
            // 指纹形如 "12:user|1834,assistant|92,..."（条数 + 除末条外各条 role|content长度）
            var sb = new StringBuilder(msgs.Count * 24);
            sb.Append(msgs.Count);
            for (int i = 0; i < msgs.Count - 1; i++)
            {
                var m = msgs[i]?.AsObject();
                var role = m?["role"]?.GetValue<string>() ?? "?";
                sb.Append(',').Append(role).Append('|').Append(ContentLen(m));
            }
            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>消息 content 长度（string = 字符数；数组型 = 序列化长度；无 = 0）。仅用于轻量指纹。</summary>
    private static int ContentLen(JsonObject? m)
    {
        var c = m?["content"];
        if (c == null) return 0;
        try
        {
            return c.GetValue<string>()?.Length ?? 0;
        }
        catch
        {
            return c.ToJsonString().Length; // 数组型 content：序列化长度作口径
        }
    }

    /// <summary>工具循环检测：末条消息 role=tool 即判定（与 InjectThinkingMode 的 role 比较口径一致）。</summary>
    public static bool DetectToolLoop(JsonObject obj)
    {
        try
        {
            var msgs = obj["messages"] as System.Text.Json.Nodes.JsonArray;
            if (msgs == null || msgs.Count == 0) return false;
            return string.Equals(msgs[^1]?["role"]?.GetValue<string>(), "tool", StringComparison.OrdinalIgnoreCase); // 与 InjectThinkingMode 的 role 比较口径一致
        }
        catch
        {
            return false;
        }
    }

    /// <summary>判断是否为 chat/completions 推理请求（思考模式注入仅对此类请求生效）。</summary>
    public static bool IsChatCompletions(string path)
    {
        return path.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>推理请求判定（POST + completion/embedding 路径）。</summary>
    public static bool IsInferenceRequest(HttpListenerRequest req)
    {
        if (!string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)) return false;
        var p = req.Url?.AbsolutePath ?? "";
        return p.Contains("completion", StringComparison.OrdinalIgnoreCase)
               || p.Contains("embedding", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>E-1 DOM 版：把非流式请求体改写为 stream=true——直接在树上置位（热路径复用同一棵树，无 parse/serialize）。</summary>
    public static void EnsureStreamTrue(JsonObject obj) => obj["stream"] = true;

    /// <summary>字符串降级版：仅当入口解析失败（root=null）时用于 C-005 兜底改写。
    /// C-005：优先 System.Text.Json DOM 解析修改（正确处理字符串内含 '}'、注释、格式化 JSON）；
    /// DOM 失败回退字符串 hack；两者都失败返回 null，调用方透传原始请求。</summary>
    public static string? EnsureStreamTrue(string json)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            if (node is System.Text.Json.Nodes.JsonObject obj)
            {
                obj["stream"] = true;
                return obj.ToJsonString();
            }
        }
        catch
        {
            // DOM 解析失败（非法 JSON），走字符串降级
        }
        // 降级：字符串级修改（"stream":false 直接替换；无 stream 字段注入到最后一个 '}' 前）
        if (System.Text.RegularExpressions.Regex.IsMatch(json, @"""stream""\s*:\s*false"))
            return System.Text.RegularExpressions.Regex.Replace(json, @"""stream""\s*:\s*false", @"""stream"":true");
        int idx = json.LastIndexOf('}');
        if (idx <= 0) return null;
        var prefix = json.Substring(0, idx).TrimEnd();
        bool hasComma = prefix.EndsWith(',');
        string field = "\"stream\":true";
        return $"{json.Substring(0, idx)}{(hasComma ? "" : ",")}{field}{json.Substring(idx)}";
    }

    /// <summary>JSON 响应写出（写入状态码 + body + 关闭连接）。</summary>
    public static async Task WriteJsonAsync(HttpListenerContext ctx, int code, string json)
    {
        var resp = ctx.Response;
        resp.StatusCode = code;
        resp.ContentType = "application/json";
        resp.ContentEncoding = System.Text.Encoding.UTF8;
        var buf = System.Text.Encoding.UTF8.GetBytes(json);
        resp.ContentLength64 = buf.Length;
        await resp.OutputStream.WriteAsync(buf);
        resp.Close();
    }

    /// <summary>错误响应写出（{"error":"..."}，JSON 序列化转义安全——含控制字符/换行/代理对；客户端已断开时静默忽略）。</summary>
    public static void WriteError(HttpListenerContext ctx, int code, string msg)
    {
        try
        {
            // AH-6：JsonSerializer 序列化转义 \n/\r/\u2028 等控制字符，避免异常消息含换行时响应体非法 JSON
            var body = System.Text.Json.JsonSerializer.Serialize(new { error = msg });
            var resp = ctx.Response;
            resp.StatusCode = code;
            resp.ContentType = "application/json";
            resp.ContentEncoding = System.Text.Encoding.UTF8;

            var buf = System.Text.Encoding.UTF8.GetBytes(body);
            resp.ContentLength64 = buf.Length;
            resp.OutputStream.Write(buf);
            resp.Close();
        }
        catch
        {
            // 客户端可能已断开，忽略
        }
    }
}
