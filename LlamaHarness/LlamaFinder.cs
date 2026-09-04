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
        // B1/B2 修复：先按参数 token 组装 List<string>，再对每个 token 独立 EscapeArg 输出。
        // 这样 llama.cpp 侧的 arg 解析器看到的每个 flag / value 都是独立 token，
        // 值里即使含 `"` 或空格也不会被误当作新的 flag（避免命令行参数注入）。
        var args = new List<string>();
        args.Add("-m");
        args.Add(EscapeArg(cfg.ModelPath));
        args.Add("--port");
        args.Add((portOverride ?? cfg.Port).ToString());
        args.Add("-c");
        args.Add(cfg.CtxSize.ToString());
        args.Add("-ngl");
        args.Add(cfg.Ngl.ToString());
        args.Add("--parallel");
        args.Add(cfg.Parallel.ToString());
        // Prompt-Cache 管控（RAMDisk 快照全权接管模式）：--cache-ram 0 关闭内置主机内存 prompt-cache（消除 LRU 驱逐虚假 KV-MISS），
        // --no-cache-idle-slots 禁止 release 后空闲 slot 自动存入 prompt cache。回滚旧双兜底模式：CacheRamMiB=8192 + NoCacheIdleSlots=false。
        args.Add("--cache-ram");
        args.Add(cfg.CacheRamMiB.ToString());
        if (cfg.NoCacheIdleSlots)
            args.Add("--no-cache-idle-slots");
        // KV Cache 持久化：配置了缓存路径时，启用 /slots 端点 + 指定保存目录（单槽/多槽均需要）
        if (!string.IsNullOrWhiteSpace(cfg.KvCachePath))
        {
            args.Add("--slots");
            // 路径归一化 + 父目录校验：拒绝含 `"`/特殊字符的原始字符串，父目录必须存在且为绝对路径。
            var normalizedKv = cfg.KvCachePath.Trim();
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(normalizedKv);
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"KV Cache 路径非法：{normalizedKv}", nameof(cfg.KvCachePath), ex);
            }
            var parentDir = Path.GetDirectoryName(fullPath) ?? "";
            if (!Path.IsPathRooted(parentDir))
                throw new ArgumentException($"KV Cache 路径必须为绝对路径：{fullPath}", nameof(cfg.KvCachePath));
            if (!Directory.Exists(parentDir))
                throw new ArgumentException($"KV Cache 父目录不存在：{parentDir}", nameof(cfg.KvCachePath));
            args.Add("--slot-save-path");
            args.Add(EscapeArg(fullPath));
        }
        if (cfg.NoKvUnified)
            args.Add("--no-kv-unified");
        int threads = threadsOverride ?? cfg.Threads;
        if (threads > 0)
        {
            args.Add("-t");
            args.Add(threads.ToString());
        }
        // Prefill 吞吐参数（结构化：阶段二调参只改 config 值，代码零改动）
        if (!string.IsNullOrWhiteSpace(cfg.LoadMode))
        {
            args.Add("--load-mode");
            args.Add(cfg.LoadMode.Trim());
        }
        if (cfg.UbatchSize > 0)
        {
            args.Add("--ubatch-size");
            args.Add(cfg.UbatchSize.ToString());
        }
        if (cfg.BatchSize > 0)
        {
            args.Add("--batch-size");
            args.Add(cfg.BatchSize.ToString());
        }
        if (!string.IsNullOrWhiteSpace(cfg.CacheTypeKv))
        {
            var ct = cfg.CacheTypeKv.Trim();
            args.Add("--cache-type-k");
            args.Add(ct);
            args.Add("--cache-type-v");
            args.Add(ct);
        }
        if (cfg.FlashAttn)
        {
            args.Add("--flash-attn");
            args.Add("on");
        }
        if (!string.IsNullOrWhiteSpace(cfg.SpecType))
        {
            args.Add("--spec-type");
            args.Add(cfg.SpecType.Trim());
            if (cfg.SpecDraftNMax > 0)
            {
                args.Add("--spec-draft-n-max");
                args.Add(cfg.SpecDraftNMax.ToString());
            }
        }
        if (cfg.BatchThreads > 0)
        {
            args.Add("--tb");
            args.Add(cfg.BatchThreads.ToString());
        }
        // M-P1 修复：ExtraArgs 白名单过滤——防止命令行参数注入。
        // B1/B2 收紧：剔除 `"`、`[`、`]`——引号允许时值边界可被绕过（注入额外 flag），中括号允许时值可含 Windows glob。
        // 允许：字母/数字、下划线、点、连字符、冒号、空格、等号、斜杠、反斜杠。
        // 拒绝：`& | ; ^ < > ( ) % $ * ? " [ ]` 等 shell / 引号 / glob 元字符。
        if (!string.IsNullOrWhiteSpace(cfg.ExtraArgs))
        {
            char[] trimmed = cfg.ExtraArgs.Trim()
                .Where(c => char.IsLetterOrDigit(c)
                    || c == '_' || c == '.' || c == '-' || c == ':'
                    || c == ' ' || c == '\t'
                    || c == '=' || c == '/' || c == '\\').ToArray();
            string extra = new string(trimmed).Trim();
            if (extra.Length > 0)
                args.Add(extra);
        }
        return string.Join(" ", args);
    }

    /// <summary>
    /// Windows CreateProcess / llama.cpp arg 解析双兼容转义：
    /// 若参数含空格、制表符或双引号，用双引号包裹并把内部 `"` 转义为 `\"`；否则原样返回。
    /// </summary>
    private static string EscapeArg(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return arg;
        bool needQuote = arg.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0;
        if (!needQuote) return arg;
        return "\"" + arg.Replace("\"", "\\\"") + "\"";
    }
}
