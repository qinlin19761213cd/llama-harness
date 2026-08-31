using System.Text;

namespace LlamaHarness;

/// <summary>
/// llama-server.exe 定位（优先级：手动指定 → PATH → 常见安装位置）
/// 以及启动命令行拼接。纯逻辑，无 UI 依赖。
/// </summary>
public static class LlamaFinder
{
    /// <summary>按优先级查找 llama-server.exe，找不到返回 null。</summary>
    public static string? Find(string configuredPath)
    {
        // 1. 配置中手动指定的路径
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            try
            {
                if (File.Exists(configuredPath.Trim()))
                    return Path.GetFullPath(configuredPath.Trim());
            }
            catch
            {
                // 非法路径字符串，忽略继续搜索
            }
        }

        // 2. PATH 环境变量（条目去引号：形如 "C:\my tools";D:\bin 的 PATH 写法合法）
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), "llama-server.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // PATH 中含非法目录时跳过
            }
        }

        // 3. 常见安装位置
        var candidates = AppPaths.BackendExeCandidates();
        foreach (var c in candidates)
        {
            try
            {
                if (File.Exists(c)) return c;
            }
            catch
            {
                // 忽略非法路径
            }
        }
        return null;
    }

    /// <summary>
    /// 拼接 llama-server 完整命令行参数。
    /// 模板：-m &lt;model&gt; --port &lt;p&gt; -c &lt;c&gt; -ngl &lt;n&gt; --parallel &lt;np&gt; [--no-kv-unified] -t &lt;t&gt;
    ///       [--load-mode] [--ubatch-size] [--batch-size] [--cache-type-k/v] [--flash-attn on]
    ///       [--spec-type ... --spec-draft-n-max N] [--tb N] [附加参数]
    /// portOverride 用于智能模式下后端端口（前端端口 + 1）；
    /// threadsOverride 用于 P 核掩码生效时钳制线程数（防超订）。
    /// 附加参数原样拼入（不做再解析），含空格的值需用户自行加引号，见 AppConfig.ExtraArgs。
    /// </summary>
    public static string BuildArgs(AppConfig cfg, int? portOverride = null, int? threadsOverride = null)
    {
        var sb = new StringBuilder();
        sb.Append($"-m \"{cfg.ModelPath}\"");
        sb.Append($" --port {(portOverride ?? cfg.Port)}");
        sb.Append($" -c {cfg.CtxSize}");
        sb.Append($" -ngl {cfg.Ngl}");
        sb.Append($" --parallel {cfg.Parallel}");
        // Prompt-Cache 管控（RAMDisk 快照全权接管模式）：--cache-ram 0 关闭内置主机内存 prompt-cache（消除 LRU 驱逐虚假 KV-MISS），
        // --no-cache-idle-slots 禁止 release 后空闲 slot 自动存入 prompt cache。回滚旧双兜底模式：CacheRamMiB=8192 + NoCacheIdleSlots=false。
        sb.Append($" --cache-ram {cfg.CacheRamMiB}");
        if (cfg.NoCacheIdleSlots)
            sb.Append(" --no-cache-idle-slots");
        // KV Cache 持久化：配置了缓存路径时，启用 /slots 端点 + 指定保存目录（单槽/多槽均需要）
        if (!string.IsNullOrWhiteSpace(cfg.KvCachePath))
        {
            sb.Append(" --slots");
            sb.Append($" --slot-save-path \"{cfg.KvCachePath.Trim()}\""); // 引号包裹：路径含空格（如 "C:\temp cache"）不致断裂
        }
        if (cfg.NoKvUnified)
            sb.Append(" --no-kv-unified");
        int threads = threadsOverride ?? cfg.Threads;
        if (threads > 0)
            sb.Append($" -t {threads}");
        // Prefill 吞吐参数（结构化：阶段二调参只改 config 值，代码零改动）
        if (!string.IsNullOrWhiteSpace(cfg.LoadMode))
            sb.Append($" --load-mode {cfg.LoadMode.Trim()}");
        if (cfg.UbatchSize > 0)
            sb.Append($" --ubatch-size {cfg.UbatchSize}");
        if (cfg.BatchSize > 0)
            sb.Append($" --batch-size {cfg.BatchSize}");
        if (!string.IsNullOrWhiteSpace(cfg.CacheTypeKv))
            sb.Append($" --cache-type-k {cfg.CacheTypeKv.Trim()} --cache-type-v {cfg.CacheTypeKv.Trim()}");
        if (cfg.FlashAttn)
            sb.Append(" --flash-attn on");
        if (!string.IsNullOrWhiteSpace(cfg.SpecType))
        {
            sb.Append($" --spec-type {cfg.SpecType.Trim()}");
            if (cfg.SpecDraftNMax > 0)
                sb.Append($" --spec-draft-n-max {cfg.SpecDraftNMax}");
        }
        if (cfg.BatchThreads > 0)
            sb.Append($" --tb {cfg.BatchThreads}");
        if (!string.IsNullOrWhiteSpace(cfg.ExtraArgs))
            sb.Append(' ').Append(cfg.ExtraArgs.Trim());
        return sb.ToString();
    }
}
