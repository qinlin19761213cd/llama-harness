using System.Text.Json;

namespace LlamaHarness;

/// <summary>
/// 应用配置模型。默认值为实测黄金底参：
/// ctx=65536 / ngl=999 / parallel=1 / kv-unified 开启（20G 显存内 KV 完整驻留，防 page-fault）。
/// 持久化为程序目录下的 config.json。
/// </summary>
public class AppConfig
{
    /// <summary>C-004：配置 schema 版本号；后续格式变更时递增并做迁移兼容（旧文件缺该字段时反序列化取默认值 1）。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    public string ExePath { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public int Port { get; set; } = 8080;
    public int CtxSize { get; set; } = 65536;   // -c 上下文长度（20G 显存红线内；阶段二验证后可上调至 131072）
    public int Ngl { get; set; } = 999;          // -ngl GPU 层数（黄金底参）
    public int Parallel { get; set; } = 1;       // --parallel 并发序列（黄金底参）
    public bool NoKvUnified { get; set; } = false;// --no-kv-unified：false = kv-unified 开启（K/V 连续存储，长上下文收益明确）
    public int Threads { get; set; } = Environment.ProcessorCount; // -t 线程数
    /// <summary>模型加载模式（--load-mode）：mlock = 全量加载 + 物理内存锁定，无页交换。</summary>
    public string LoadMode { get; set; } = "mlock";
    /// <summary>Prefill 微批大小（--ubatch-size）：提升 prefill 单步并行度；阶段二调优 2048→4096，不得超过 BatchSize。</summary>
    public int UbatchSize { get; set; } = 2048;
    /// <summary>Prompt 处理批量上限（--batch-size）：不得低于 ubatch 的 2 倍。</summary>
    public int BatchSize { get; set; } = 8192;
    /// <summary>KV 缓存量化（--cache-type-k/v）：q4_0 / q8_0 / f16；阶段二切 q8_0 前必须核算显存。</summary>
    public string CacheTypeKv { get; set; } = "q4_0";
    /// <summary>Flash Attention 开关（--flash-attn on）：prefill 速度核心开关，必开。</summary>
    public bool FlashAttn { get; set; } = true;
    /// <summary>投机解码类型（--spec-type）：draft-mtp = MTP draft 模型，decode 提速 2~3 倍。</summary>
    public string SpecType { get; set; } = "draft-mtp";
    /// <summary>每轮投机 draft token 数（--spec-draft-n-max）：0 = 不拼接该参数。</summary>
    public int SpecDraftNMax { get; set; } = 2;
    /// <summary>batch 阶段 CPU 线程数（--tb）：prefill 分词/调度辅助加速；0 = 不拼接。</summary>
    public int BatchThreads { get; set; } = 0;
    /// <summary>附加参数：原样拼入命令行（不做再解析）；含空格的路径需自行加引号，如 --mmproj "D:\a b\projector.gguf"。</summary>
    public string ExtraArgs { get; set; } = "";
    public bool AutoMode { get; set; } = true;       // 智能按需模式：代理监听 8080 + 按需唤醒 + 闲置休眠
    public int IdleMinutes { get; set; } = 15;       // 无请求自动休眠分钟数
    // P 核亲和性掩码（十六进制）：13900F 本机 P 核 = 逻辑 CPU 0–15；留空 = 禁用绑定
    public string PCoreMask { get; set; } = "0x0000FFFF";
    // 强制流式：把非流式推理请求改写为 stream=true（SSE 直通），防客户端读超时→断开→全量重填。
    // 仅适用于能解析 SSE 流的客户端；标准 OpenAI SDK 客户端勿开。
    public bool ForceStream { get; set; } = false;
    /// <summary>KV Cache 保存路径（--slot-save-path）：留空 = 禁用 KV 缓存持久化。驱逐时自动 save，重绑定时自动 restore。</summary>
    public string KvCachePath { get; set; } = "g:/temp";
    /// <summary>Token Guard 总开关：代理层预估算 + 裁剪，防上下文超长 400 错误。</summary>
    public bool TokenGuardEnabled { get; set; } = true;
    /// <summary>输出预留 token（为模型生成回复保留）：预算 = CtxSize ÷ Parallel − 此值 − Prompt头部开销预留。</summary>
    public int ReservedOutputTokens { get; set; } = 8192;
    /// <summary>Prompt 头部开销预留（tools 工具定义 + system 提示词 + Jinja 模板渲染的隐形 token，不计入对话消息统计）。默认 10240 覆盖多工具 Agent 场景；工具数量增多时可在 UI 上调大。</summary>
    public int ReservedPromptOverhead { get; set; } = 10240;
    /// <summary>llama.cpp 主机内存 Prompt-Cache 上限（MiB）：0 = 完全关闭内置 prompt-cache（RAMDisk 快照全权接管模式，消除 LRU 驱逐虚假 KV-MISS）；回滚旧双兜底模式设 8192。</summary>
    public int CacheRamMiB { get; set; } = 0;
    /// <summary>禁止任务 release 后自动把空闲 slot 状态存入 prompt cache（与 CacheRamMiB=0 配套；cache-ram&gt;0 时仍生效）。</summary>
    public bool NoCacheIdleSlots { get; set; } = true;
    /// <summary>输出续接总开关：输出被 max_tokens 截断（finish_reason=length）时自动续接。</summary>
    public bool ContinuationEnabled { get; set; } = true;
    /// <summary>最大续接迭代次数（防死循环）。</summary>
    public int MaxContinuations { get; set; } = 10;
    /// <summary>单轮推理超时（秒）：超时返回已生成内容。</summary>
    public int ContinuationTimeoutSeconds { get; set; } = 300;
    /// <summary>bad_alloc 崩溃自动恢复总开关：流中断/5xx 检测到 bad_alloc 时自动快照接续或全量重放。</summary>
    public bool CrashRecoveryEnabled { get; set; } = true;
    /// <summary>进程死亡分支的最大自动重启次数（防无限重启循环）。</summary>
    public int MaxAutoRestarts { get; set; } = 2;
    /// <summary>恢复期间 SSE keep-alive 注释行间隔（秒）：保活客户端连接，Trae 无感续接。</summary>
    public int RecoveryKeepAliveIntervalSeconds { get; set; } = 5;
    /// <summary>自动强占（冻结防驱逐）的应用类型前缀，逗号分隔。key 匹配任一前缀 → 槽位不可被 LRU 驱逐（§4.2）。默认持久 Agent 会话类。</summary>
    public string AutoPreemptiveApps { get; set; } = "dsh_agent_global,trae_global";
    /// <summary>自动快照 key（仅快照持久化，不锁槽）：逗号分隔前缀。key 匹配任一前缀 → 首请求存档 + Warming eager restore；不参与槽位强占/驱逐拒绝（与 AutoPreemptiveApps 解耦）。默认 trae_global。</summary>
    public string AutoSnapshotKeys { get; set; } = "trae_global";

    /// <summary>指纹识别规则（v2.16）：有序按 Priority 升序匹配，第一条命中即返回。新增业务 = 配置追加一条规则，零代码改动。默认 4 条与重构前 GetAffinityKey 逐字等价。</summary>
    public List<AffinityRule> AffinityRules { get; set; } = DefaultAffinityRules();
    /// <summary>请求体 dump 开关（应用识别分析用）：每个 POST 的原始 body + headers 落盘 request_dump.log。默认关闭——防 prompt 隐私落盘与无谓 IO（审计 O-18）。</summary>
    public bool RequestDumpEnabled { get; set; } = false;
    /// <summary>日志管道队列满丢弃策略：DropNewest = 保留历史、丢新入队（默认——排查更看重最早异常源头）；DropOldest = 丢最旧、保留新消息。</summary>
    public QueueFullPolicy LogQueueFullPolicy { get; set; } = QueueFullPolicy.DropNewest;

    /// <summary>配置文件路径：项目目录下 config/config.json。</summary>
    private static string ConfigPath => AppPaths.ConfigJson;

    /// <summary>审计：config.json 字段命名统一 snake_case_lower（此前仅 schema_version 为 snake，其余 PascalCase）。</summary>
    private sealed class SnakeCaseNamingPolicy : System.Text.Json.JsonNamingPolicy
    {
        public override string ConvertName(string name)
        {
            var sb = new System.Text.StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsUpper(c))
                {
                    if (i > 0) sb.Append('_');
                    sb.Append(char.ToLowerInvariant(c));
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = new SnakeCaseNamingPolicy(),
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>旧版 config.json 为 PascalCase 字段名：仅用于兼容读取；保存一律写新 snake_case 格式。</summary>
    private static readonly JsonSerializerOptions LegacyJsonOpts = new() { WriteIndented = true };

    /// <summary>确保 config/ 目录存在（幂等）。</summary>
    private static void EnsureConfigDir() => AppPaths.EnsureConfigDir();

    /// <summary>默认 4 条指纹规则（与重构前 GetAffinityKey 的硬编码逐字等价）：DSH 规则引擎 / WebUI / Trae Work / DSH 主 Agent。</summary>
    public static List<AffinityRule> DefaultAffinityRules() => new()
    {
        new() { Id = "dsh_rule", Name = "DSH 规则引擎", UiPrefix = "dsh_rule", Match = AffinityMatchType.Header, Header = "x-deepseek-harness-user-id", KeyTemplate = "dsh_rule_{value}", Priority = 1, TooltipAutoPre = "勾选后 DSH 规则引擎会话（dsh_rule_*）槽位自动强占：空闲不被 LRU 驱逐，再次提问零 Prefill 开销。", TooltipSnap = "勾选后 DSH 规则引擎会话（dsh_rule_*）启用自动快照恢复：首请求存档 + 唤醒 eager restore；不锁槽，可被其他应用正常驱逐。" },
        new() { Id = "webui", Name = "WebUI", UiPrefix = "webui", Match = AffinityMatchType.Header, Header = "X-Conversation-Id", KeyTemplate = "webui_{value}", Priority = 2, TooltipAutoPre = "勾选后 WebUI 会话（webui_*）槽位自动强占：空闲不被 LRU 驱逐。", TooltipSnap = "勾选后 WebUI 会话（webui_*）启用自动快照恢复：首请求存档 + 唤醒 eager restore；不锁槽，可被其他应用正常驱逐。" },
        new() { Id = "trae_global", Name = "Trae Work", UiPrefix = "trae_global", Match = AffinityMatchType.HeaderValue, Header = "x-model-provider", Value = "custom_openai_compatible", Key = "trae_global", Priority = 3, TooltipAutoPre = "勾选后 Trae Work（trae_global）槽位自动强占：空闲不被 LRU 驱逐。", TooltipSnap = "勾选后 Trae Work（trae_global）启用自动快照恢复：首请求存档 + 唤醒 eager restore；不锁槽，可被其他应用正常驱逐。" },
        new() { Id = "dsh_agent", Name = "DSH 主 Agent", UiPrefix = "dsh_agent_global", Match = AffinityMatchType.UaAndHeaderPrefix, UaContains = "deepseek-harness", HeaderPrefix = "X-Stainless-", Key = "dsh_agent_global", Priority = 4, TooltipAutoPre = "勾选后 DSH 主 Agent（dsh_agent_global）槽位自动强占：空闲不被 LRU 驱逐。注意 parallel=2 时若两槽都被强占，新会话将排队等待（上限 30s）。", TooltipSnap = "勾选后 DSH 主 Agent（dsh_agent_global）启用自动快照恢复：首请求存档 + 唤醒 eager restore；不锁槽，可被其他应用正常驱逐。" },
    };

    /// <summary>数值兜底统一入口：越界时回退黄金默认值。Load() 与配置导入共用同一套规则，避免规则漂移（修复导入路径 CtxSize 与 Load 不一致）。</summary>
    public static void Sanitize(AppConfig cfg)
    {
        if (cfg.Port is < 1 or > 65534) cfg.Port = 8080; // 上限 65534：智能模式后端端口 = Port+1，65535 会与前端端口冲突
        if (cfg.CtxSize <= 0) cfg.CtxSize = 65536;
        if (cfg.Ngl < 0) cfg.Ngl = 999;
        if (cfg.Parallel <= 0) cfg.Parallel = 1;
        if (cfg.Threads <= 0) cfg.Threads = Environment.ProcessorCount;
        if (cfg.UbatchSize <= 0) cfg.UbatchSize = 2048;
        if (cfg.BatchSize <= 0) cfg.BatchSize = 8192;
        if (cfg.SpecDraftNMax < 0) cfg.SpecDraftNMax = 2; // 0 = 用户显式禁用，不兜底
        if (cfg.BatchThreads < 0) cfg.BatchThreads = 0;   // 0 = 不拼 --tb
        if (cfg.IdleMinutes <= 0) cfg.IdleMinutes = 15;
        if (cfg.ReservedOutputTokens <= 0) cfg.ReservedOutputTokens = 8192;
        if (cfg.ReservedPromptOverhead < 0) cfg.ReservedPromptOverhead = 10240;
        if (cfg.CacheRamMiB < 0) cfg.CacheRamMiB = 0;
        if (cfg.MaxContinuations < 1) cfg.MaxContinuations = 10;
        if (cfg.ContinuationTimeoutSeconds < 30) cfg.ContinuationTimeoutSeconds = 300;
        if (cfg.MaxAutoRestarts < 0) cfg.MaxAutoRestarts = 2; // 0 = 禁用进程死亡分支的自动重启
        if (cfg.RecoveryKeepAliveIntervalSeconds < 1) cfg.RecoveryKeepAliveIntervalSeconds = 5;
    }

    /// <summary>加载配置；文件不存在返回默认值，损坏则回退默认值并通过 out 报告错误。</summary>

    public static AppConfig Load(out string? loadError)
    {
        loadError = null;
        try
        {
            if (!File.Exists(ConfigPath))
                return new AppConfig();

            var json = File.ReadAllText(ConfigPath);
            // 兼容：旧版 config.json 为 PascalCase 字段名（新版统一 snake_case_lower）；按字段名探测选择反序列化选项
            var opts = json.Contains("\"ExePath\"") ? LegacyJsonOpts : JsonOpts;
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, opts);
            if (cfg == null)
                throw new InvalidOperationException("反序列化结果为空");

            Sanitize(cfg); // 数值兜底统一入口（Load/导入共用，规则集中在 Sanitize）
            return cfg;
        }
        catch (Exception ex)
        {
            loadError = $"config.json 读取失败，已回退默认值：{ex.Message}";
            return new AppConfig();
        }
    }

    /// <summary>单槽输入 token 预算：上下文均摊到每槽，扣除输出预留 + Prompt 头部开销预留（tools/system/Jinja 模板隐形 token）。审计 O-9：收敛此前散落 5 处的重复公式。</summary>
    public int GetInputBudget() => Math.Max(MinInputBudgetTokens, CtxSize / Math.Max(1, Parallel) - ReservedOutputTokens - ReservedPromptOverhead);

    /// <summary>输入预算下限（防止极端配置下预算 ≤0 导致全部请求被裁剪）。</summary>
    public const int MinInputBudgetTokens = 1024;

    /// <summary>保存配置到 config/ 目录（临时文件 + 重命名原子写入，防半截文件损坏），返回是否成功。</summary>
    public bool Save(out string? error)
    {
        error = null;
        try
        {
            EnsureConfigDir();
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(tmp, ConfigPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
