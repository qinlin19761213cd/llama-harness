using System.Collections.Specialized;

namespace LlamaHarness;

/// <summary>
/// 指纹规则匹配引擎（v2.16）：按优先级顺序对请求头执行规则匹配，产出亲和 Key。
/// 取代原 SlotAffinity.GetAffinityKey() 硬编码的 4 组 if-else；规则来自 config.json 的 affinity_rules。
/// 纯静态、可单测；新增业务 = 配置追加规则，零代码改动。
/// </summary>
public static class AffinityRuleMatcher
{
    /// <summary>AH-16：头值参与亲和 key 时的长度上限（防客户端超长头导致 key 膨胀——KV 文件名/JSON/内存）。</summary>
    private const int MaxHeaderValueLength = 256;

    /// <summary>按 Priority 升序遍历规则，第一条命中返回 key；全不命中返回 null（调用方走随机槽，不建绑定）。</summary>
    public static string? Match(NameValueCollection headers, IEnumerable<AffinityRule> rules)
    {
        foreach (var r in rules.OrderBy(x => x.Priority))
        {
            var key = TryMatch(headers, r);
            if (key != null) return key;
        }
        return null;
    }

    /// <summary>单条规则匹配；命中返回 key，未命中返回 null。</summary>
    private static string? TryMatch(NameValueCollection headers, AffinityRule r)
    {
        switch (r.Match)
        {
            case AffinityMatchType.Header:
            {
                var hv = headers[r.Header];
                if (string.IsNullOrEmpty(hv)) return null;
                if (hv.Length > MaxHeaderValueLength) return null; // AH-16：超长头值不参与绑定（防 key 膨胀）
                return r.KeyTemplate.Replace("{value}", hv);
            }
            case AffinityMatchType.HeaderValue:
            {
                var mv = headers[r.Header];
                if (string.IsNullOrEmpty(r.Value) || !string.Equals(mv, r.Value, StringComparison.OrdinalIgnoreCase))
                    return null;
                return r.Key;
            }
            case AffinityMatchType.UaAndHeaderPrefix:
            {
                var ua = headers["User-Agent"] ?? "";
                if (!ua.Contains(r.UaContains, StringComparison.OrdinalIgnoreCase)) return null;
                foreach (var k in headers.AllKeys)
                {
                    if (k != null && k.StartsWith(r.HeaderPrefix, StringComparison.OrdinalIgnoreCase))
                        return r.Key;
                }
                return null;
            }
            default:
                return null;
        }
    }

    /// <summary>未知应用自动兜底（v2.23.8）：对 UA 稳定哈希生成独立亲和 key（unknown_{hash12}），
    /// 走正常槽位亲和 + KV 快照。正式规则全不命中时调用；同 UA → 同 key（KV 可跨请求复用）。
    /// 无 UA / UA 超长（AH-16 同类防护）/ 已达上限 → 返回 null（不建绑定，防 key 膨胀与滥用）。</summary>
    public static string? TryAutoBindUnknown(NameValueCollection headers, int maxUnknownKeys, int existingUnknownCount)
    {
        if (existingUnknownCount >= maxUnknownKeys) return null; // 达上限：拒绝新建 unknown key
        var ua = headers["User-Agent"];
        if (string.IsNullOrEmpty(ua)) return null;
        if (ua.Length > MaxHeaderValueLength * 2) return null;   // UA > 512 不绑定（超长特征不可信）
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ua)))
            .Substring(0, 12).ToLowerInvariant();
        return $"unknown_{hash}";
    }

    /// <summary>按规则派生应用显示名：固定 Key 精确匹配优先，KeyTemplate 前缀匹配其次；未知返回 "未知应用"。</summary>
    public static string AppNameOf(string key, IEnumerable<AffinityRule> rules)
    {
        foreach (var r in rules)
        {
            // 固定 key 规则：精确匹配（忽略大小写）
            if (!string.IsNullOrEmpty(r.Key) && string.Equals(key, r.Key, StringComparison.OrdinalIgnoreCase))
                return r.Name;
            // 模板规则：key 以 {value} 之前的前缀开头
            if (!string.IsNullOrEmpty(r.KeyTemplate))
            {
                int ph = r.KeyTemplate.IndexOf("{value}", StringComparison.Ordinal);
                if (ph > 0)
                {
                    var prefix = r.KeyTemplate.Substring(0, ph);
                    if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return r.Name;
                }
            }
        }
        return "未知应用";
    }
}
