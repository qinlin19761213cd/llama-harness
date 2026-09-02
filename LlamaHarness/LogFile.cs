namespace LlamaHarness;

/// <summary>
/// UI 日志文件持久化薄门面（统一异步日志管道）：
/// - harness.log：全部日志；warn_error.log：警告/错误独立成块（附该条之前 10 条上下文）；slot.log / request_dump.log 独立流
/// - 生产侧 Enqueue 即返回（一次 lock，零编码/分类/磁盘）；单后台写线程批量消费 + 双阈值 Flush + 轮切（LogPipeline）
/// - _recent 环形缓冲在生产侧更新（SnapshotRecent UI 行为与旧实现一致）
/// 线程安全、尽力而为（永不抛出）。
/// </summary>
public static class LogFile
{
    private static readonly object _recentGate = new();

    /// <summary>日志目录：项目目录下 logs/（写入器首次打开时自动创建）。</summary>
    internal static string LogDir => AppPaths.LogDir;

    /// <summary>最近 N 条带时间戳日志（生产侧更新），供警告/错误块与 /__status__ recent_logs。</summary>
    private static readonly Queue<string> _recent = new();

    /// <summary>warn_error 块附带的前置日志条数。</summary>
    private const int ContextLines = 10;

    private static readonly Lazy<LogPipeline> _pipeline =
        new(() => new LogPipeline(LogDir, QueueFullPolicy.DropNewest), true);

    /// <summary>设置队列满丢弃策略（运行时生效，UI 配置页）。</summary>
    /// <summary>v2.22 可观测：日志管道性能快照（丢弃行数 / flush 平均耗时）。</summary>
    public static (long Dropped, double FlushAvgMs) PerfSnapshot() => _pipeline.Value.PerfSnapshot();

    public static void Configure(QueueFullPolicy policy)
    {
        _pipeline.Value.Queue.Policy = policy;
    }

    public enum Level { Info, Warn, Error }

    /// <summary>llama-server 输出严重度标记：时间戳前缀后跟 I/W/E（如 "0.38.265.840 E srv ..."）。</summary>
    private static readonly System.Text.RegularExpressions.Regex SeverityRe =
        new(@"^\d[\d.]*\s+([IWE])\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>英文错误关键字（不带 I/W/E 前缀的输出兜底，词边界 + 不区分大小写）。</summary>
    private static readonly System.Text.RegularExpressions.Regex ErrorKeywordRe =
        new(@"\b(error|fatal|critical|exception|failed|failure)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>英文警告关键字。</summary>
    private static readonly System.Text.RegularExpressions.Regex WarnKeywordRe =
        new(@"\b(warning|warn)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>llama.cpp 已知良性噪声（3.3 日志标准化）：剪枝/合并模型残留的 unused tensor 警告——不进告警流，仅写主日志。</summary>
    private static readonly System.Text.RegularExpressions.Regex UnusedTensorRe =
        new(@"model has unused tensor blk\.\d+", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>日志级别分类（中英双语）：
    /// 0. 已知良性噪声（unused tensor）→ Info（不写 warn_error.log）；
    /// 1. 中文关键字（错误/失败/异常 → Error，警告 → Warn）；
    /// 2. llama-server I/W/E 严重度标记；
    /// 3. 英文关键字兜底（error/fatal/critical/exception/failed → Error，warning/warn → Warn）。</summary>
    public static Level Classify(string line)
    {
        if (UnusedTensorRe.IsMatch(line)) return Level.Info; // 3.3：良性警告降级，防误告警
        if (line.StartsWith("[TOKEN-GUARD] ")) return Level.Info; // 可观测计量数据（msg_est=FAILED(tokenize) 是正常降级路径），非错误
        if (line.Contains("错误") || line.Contains("失败") || line.Contains("异常")) return Level.Error;
        if (line.Contains("警告")) return Level.Warn;
        var m = SeverityRe.Match(line);
        if (m.Success)
            return m.Groups[1].Value switch // 正则字符类 [IWE] 只匹配大写，无需 ToUpper
            {
                "E" => Level.Error,
                "W" => Level.Warn,
                _ => Level.Info,
            };
        if (ErrorKeywordRe.IsMatch(line)) return Level.Error;
        if (WarnKeywordRe.IsMatch(line)) return Level.Warn;
        return Level.Info;
    }

    /// <summary>最近日志快照（/__status__ 的 recent_logs 数据源；生产侧更新，含全部 harness 侧日志）。</summary>
    public static string[] SnapshotRecent()
    {
        lock (_recentGate)
        {
            return _recent.ToArray();
        }
    }

    /// <summary>追加一行日志（可来自任意线程）：捕获 Utc 时间戳 + 更新 _recent + Enqueue（即返回，零磁盘）。
    /// warn_error 派生块由写线程按 Classify 结果生成（上下文 = 该条之前 10 条）。</summary>
    public static void Append(string line)
    {
        try
        {
            var utc = DateTime.UtcNow;
            var stamped = LogPipeline.FormatLine(utc, line);
            lock (_recentGate)
            {
                _recent.Enqueue(stamped);
                while (_recent.Count > ContextLines) _recent.Dequeue();
            }
            _pipeline.Value.Enqueue(LogStream.Main, utc, line);
        }
        catch
        {
            // 尽力而为：不影响主流程
        }
    }

    /// <summary>追加一行槽位日志（可来自任意线程）：独立流 slot.log，超 2MB 轮切。用于绑定/驱逐/KV Cache 事件追溯。</summary>
    public static void SlotAppend(string line)
    {
        try
        {
            _pipeline.Value.Enqueue(LogStream.Slot, DateTime.UtcNow, line);
        }
        catch
        {
            // 尽力而为
        }
    }

    /// <summary>追加请求 dump 块（可来自任意线程）：独立流 request_dump.log（多行块作为单条消息，保留块内结构）。</summary>
    public static void DumpAppend(string block)
    {
        try
        {
            _pipeline.Value.Enqueue(LogStream.Dump, DateTime.UtcNow, block);
        }
        catch
        {
            // 尽力而为
        }
    }

    /// <summary>进程退出时调用：drain 队列 + 最终 Flush（3s 超时；超时返回剩余行数）。</summary>
    public static void Shutdown()
    {
        try
        {
            var (completed, remaining) = _pipeline.Value.Shutdown();
            if (!completed)
                System.Diagnostics.Debug.WriteLine($"[LOG-PIPE] shutdown drain timeout, remain {remaining} lines lost");
        }
        catch
        {
            // 尽力而为
        }
    }
}
