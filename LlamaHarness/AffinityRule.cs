namespace LlamaHarness;

/// <summary>指纹匹配方式。</summary>
public enum AffinityMatchType
{
    /// <summary>取 Header 值替换 KeyTemplate 的 {value} 占位（如 dsh_rule_{value}）。</summary>
    Header,

    /// <summary>Header 值等于指定 Value（忽略大小写）→ 返回固定 Key（如 trae_global）。</summary>
    HeaderValue,

    /// <summary>User-Agent 含 UaContains 且存在 HeaderPrefix 前缀头 → 返回固定 Key（如 dsh_agent_global）。</summary>
    UaAndHeaderPrefix,
}

/// <summary>
/// 指纹识别规则（v2.16）：config.json 的 affinity_rules 数组元素。
/// 有序按 Priority 升序匹配，第一条命中即返回；新增业务 = 在配置追加一条规则，零代码改动。
/// 默认 4 条（DSH 规则引擎 / WebUI / Trae Work / DSH 主 Agent）与重构前 GetAffinityKey 逐字等价。
/// </summary>
public sealed class AffinityRule
{
    /// <summary>业务标识（如 "dsh_rule"），供配置引用与日志。</summary>
    public string Id { get; set; } = "";

    /// <summary>应用显示名（如 "DSH 规则引擎"），AppNameOf 由此派生。</summary>
    public string Name { get; set; } = "";

    /// <summary>匹配方式。</summary>
    public AffinityMatchType Match { get; set; } = AffinityMatchType.Header;

    /// <summary>头名（Header / HeaderValue 用）。</summary>
    public string Header { get; set; } = "";

    /// <summary>期望头值（HeaderValue 用）。</summary>
    public string Value { get; set; } = "";

    /// <summary>UA 须含的子串（UaAndHeaderPrefix 用）。</summary>
    public string UaContains { get; set; } = "";

    /// <summary>须存在的头名前缀（UaAndHeaderPrefix 用，如 "X-Stainless-"）。</summary>
    public string HeaderPrefix { get; set; } = "";

    /// <summary>含 {value} 占位的 key 模板（Header 用，如 "dsh_rule_{value}"）。</summary>
    public string KeyTemplate { get; set; } = "";

    /// <summary>固定 key（HeaderValue / UaAndHeaderPrefix 用）。</summary>
    public string Key { get; set; } = "";

    /// <summary>匹配优先级，越小越先（1 = 最高）。</summary>
    public int Priority { get; set; }

    /// <summary>UI 自动强占/自动快照 checkbox 写入 AutoPreemptiveApps/AutoSnapshotKeys 的前缀；未填时由 UiPrefixOf() 推导。</summary>
    public string? UiPrefix { get; set; }

    /// <summary>自动强占 checkbox 悬浮提示；null → UI 用模板生成。</summary>
    public string? TooltipAutoPre { get; set; }

    /// <summary>自动快照 checkbox 悬浮提示；null → UI 用模板生成。</summary>
    public string? TooltipSnap { get; set; }

    /// <summary>UI checkbox 勾选后写入 AutoPreemptiveApps/AutoSnapshotKeys 的前缀（与 SlotAffinity 前缀匹配语义一致）。
    /// 显式 UiPrefix 优先；Header → KeyTemplate 的 {value} 前段（如 dsh_rule_{value} → dsh_rule）；固定 Key 规则 → Key。</summary>
    public string UiPrefixOf()
    {
        if (!string.IsNullOrEmpty(UiPrefix)) return UiPrefix;
        if (Match == AffinityMatchType.Header && KeyTemplate.Contains("{value}", StringComparison.Ordinal))
        {
            int idx = KeyTemplate.IndexOf("{value}", StringComparison.Ordinal);
            if (idx > 0) return KeyTemplate.Substring(0, idx).TrimEnd('_', '-'); // 去掉 {value} 前段的尾部分隔符（dsh_rule_ → dsh_rule）
        }
        if (!string.IsNullOrEmpty(Key)) return Key;
        return Id;
    }
}
