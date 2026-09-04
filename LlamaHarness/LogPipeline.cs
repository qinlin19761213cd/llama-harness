using System.Text;

namespace LlamaHarness;

/// <summary>日志流标识（4 个输出文件）：Main→harness.log + warn_error.log（派生）、Slot→slot.log、Dump→request_dump.log。</summary>
public enum LogStream { Main, Slot, Dump }

/// <summary>队列满丢弃策略：DropNewest = 丢新入队（保留历史，默认——排查看重最早异常源头）；DropOldest = 丢最旧。</summary>
public enum QueueFullPolicy { DropNewest, DropOldest }

/// <summary>日志消息实体：Enqueue 生产侧捕获 Utc 时间戳（消除写线程时序漂移），携带格式化行 + 原始行（Classify 用）。
/// 契约：单流内部严格 FIFO；跨流不做全局时间排序。</summary>
public readonly struct LogMessage
{
    public LogStream Stream { get; }
    /// <summary>业务发生时刻（Enqueue 时捕获 DateTime.UtcNow）。</summary>
    public DateTime CreateUtc { get; }
    /// <summary>已格式化的输出行（本地时间显示）。</summary>
    public string StampedLine { get; }
    /// <summary>原始行（未加时间戳，供 Classify——严重度正则锚定行首）。</summary>
    public string RawLine { get; }

    public LogMessage(LogStream stream, DateTime createUtc, string stampedLine, string rawLine)
    {
        Stream = stream;
        CreateUtc = createUtc;
        StampedLine = stampedLine;
        RawLine = rawLine;
    }
}

/// <summary>有界队列：四流共享全局单队列（50k 行 ≈ 10MB 上限）。满时按策略丢弃 + 计数。public 供测试。</summary>
public sealed class BoundedLineQueue
{
    private readonly Queue<LogMessage> _q = new();
    private readonly object _gate = new();
    private long _dropped;

    public int Capacity { get; }
    /// <summary>当前丢弃策略（可运行时切换，UI 配置）。</summary>
    public QueueFullPolicy Policy { get; set; } = QueueFullPolicy.DropNewest;

    public BoundedLineQueue(int capacity) => Capacity = capacity;

    /// <summary>入队：满时按策略丢弃。返回 false = 本条被丢弃（DropNewest 满）。</summary>
    public bool TryEnqueue(LogMessage msg)
    {
        lock (_gate)
        {
            if (_q.Count >= Capacity)
            {
                if (Policy == QueueFullPolicy.DropNewest)
                {
                    _dropped++;
                    return false;
                }
                _q.Dequeue(); // DropOldest：挤掉最旧，保留新消息
                _dropped++;
            }
            _q.Enqueue(msg);
            return true;
        }
    }

    /// <summary>批量出队（最多 maxCount），返回实际条数。</summary>
    public int Drain(List<LogMessage> batch, int maxCount)
    {
        lock (_gate)
        {
            int n = Math.Min(maxCount, _q.Count);
            for (int i = 0; i < n; i++) batch.Add(_q.Dequeue());
            return n;
        }
    }

    /// <summary>当前队列行数。</summary>
    public int Count
    {
        get { lock (_gate) return _q.Count; }
    }

    /// <summary>取自上次调用以来的丢弃增量（[LOG-PIPE] 埋点用）。</summary>
    public long TakeDroppedDelta()
    {
        lock (_gate)
        {
            var d = _dropped;
            _dropped = 0;
            return d;
        }
    }
}

/// <summary>Flush 双阈值：时间 ≥150ms 或缓冲字节 ≥64KB。public 供测试。</summary>
public static class FlushPolicy
{
    public const int IntervalMs = 150;
    public const long SizeBytes = 64_000;

    /// <summary>是否应 Flush（任一阈值达到即刷）。</summary>
    public static bool ShouldFlush(long elapsedMs, long bufferedBytes) =>
        elapsedMs >= IntervalMs || bufferedBytes >= SizeBytes;
}

/// <summary>常驻日志写入器：单个 StreamWriter 缓冲写 + 按大小轮切（close→rename→reopen）。
/// 管道内仅写线程触碰（Shutdown 超时路径除外，尽力而为）。public 供测试。</summary>
public sealed class LogStreamWriter : IDisposable
{
    private readonly string _path;
    private StreamWriter? _writer;
    private long _bytes;         // 文件总字节（轮切基准）
    private long _pendingBytes;  // 距上次 Flush 的写入字节（64KB 阈值基准）
    private bool _initialized;

    public LogStreamWriter(string path) => _path = path;

    /// <summary>距上次 Flush 的缓冲字节数（Flush 判定用，写线程内访问）。</summary>
    public long PendingBytes => _pendingBytes;

    /// <summary>写一段文本（调用方持管道写线程上下文）。</summary>
    public void Write(string text)
    {
        EnsureOpen();
        _writer!.Write(text);
        _bytes += Encoding.UTF8.GetByteCount(text);
        _pendingBytes += Encoding.UTF8.GetByteCount(text);
    }

    public void Flush()
    {
        try { _writer?.Flush(); } catch { /* 尽力而为 */ }
        _pendingBytes = 0;
    }

    /// <summary>按大小轮切：close → path→path.1（覆盖旧备份）→ 下次写自动重开。返回是否发生轮切。</summary>
    public bool RotateIfNeeded(long maxBytes)
    {
        if (_bytes <= maxBytes) return false;
        CloseQuiet();
        try
        {
            var backup = _path + ".1";
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(_path, backup);
        }
        catch
        {
            // 轮切失败不影响写入（下次 EnsureOpen 仍会打开原文件追加）
        }
        _bytes = 0;
        return true;
    }

    private void EnsureOpen()
    {
        if (_writer != null) return;
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        // FileShare.ReadWrite：允许测试/外部工具在写入期间读取文件（生产环境无影响）
        _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8, 4096);
        if (!_initialized)
        {
            // 首次打开：以既有文件大小为轮切基准（追加模式不重置计数）
            var fi = new FileInfo(_path);
            _bytes = fi.Exists ? fi.Length : 0;
            _initialized = true;
        }
    }

    private void CloseQuiet()
    {
        try { _writer?.Dispose(); } catch { /* 尽力而为 */ }
        _writer = null;
    }

    public void Dispose()
    {
        Flush();
        CloseQuiet();
    }
}

/// <summary>统一异步日志管道（方案 A）：生产侧 Enqueue 即返回（一次 lock，零编码/分类/磁盘），
/// 单后台写线程批量消费 + 双阈值 Flush + 轮切；IO 连续失败退避 200ms；Shutdown drain（默认 3s 超时）。
/// 契约：单流内部严格 FIFO；跨流不做全局时间排序；时间戳在 Enqueue 捕获。
/// 埋点（约束 C-4）：[LOG-PIPE] 启动 / dropped=N / io_fail=N / shutdown drained|timeout。</summary>
public sealed class LogPipeline : IDisposable
{
    public const int DefaultQueueCapacity = 50_000;

    // v2.22 可观测：日志管道累积指标（写线程更新 + _perfGate 保护，PerfSnapshot 供采样器读）
    private long _totalDropped;
    private long _flushCount;
    private double _flushSumMs;
    private readonly object _perfGate = new();

    /// <summary>四流大小上限（字节）：main/slot/dump 2MB、warn 5MB，各自独立轮切互不干扰。</summary>
    private const long MaxMainBytes = 2_000_000;
    private const long MaxWarnBytes = 5_000_000;
    private const long MaxSlotBytes = 2_000_000;
    private const long MaxDumpBytes = 2_000_000;

    /// <summary>warn_error 块附带的前置 main 日志条数（写线程侧环形缓冲，语义 = "该条之前 10 条"）。</summary>
    private const int WarnContextLines = 10;
    private const int MaxDrainPerTick = 8192;
    private const int BatchCapacity = 512;

    private readonly BoundedLineQueue _queue;
    private readonly Thread _writerThread;
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly LogStreamWriter _mainWriter;
    private readonly LogStreamWriter _warnWriter;
    private readonly LogStreamWriter _slotWriter;
    private readonly LogStreamWriter _dumpWriter;
    private readonly Queue<string> _warnContext = new(); // 写线程侧：最近 main 行（warn 块上下文）
    private readonly int _joinTimeoutMs;
    private volatile bool _accepting = true;
    private volatile bool _shutdownRequested;
    private long _ioFailCount;
    private int _consecutiveIoFailures;
    private DateTime _lastFlushUtc = DateTime.UtcNow;
    private bool _disposed;
    // M-06 修复：Enqueue 锁改为实例字段，避免静态锁在单例场景下与未来多实例扩展冲突
    private readonly object _enqueueGate = new();

    /// <summary>累计 IO 失败计数（[LOG-PIPE] io_fail 埋点源）。</summary>
    public long IoFailCount => Interlocked.Read(ref _ioFailCount);

    /// <summary>当前队列积压行数。</summary>
    /// <summary>v2.22 可观测：日志管道累积指标快照（丢弃总行数 / flush 平均耗时 ms）。</summary>
    public (long Dropped, double FlushAvgMs) PerfSnapshot()
    {
        lock (_perfGate)
            return (_totalDropped, _flushCount > 0 ? _flushSumMs / _flushCount : 0);
    }

    public int QueueCount => _queue.Count;

    /// <summary>有界队列（public 供测试与运行时切换 policy）。</summary>
    public BoundedLineQueue Queue => _queue;

    private const string MainLogFile = "harness.log";
    private const string WarnLogFile = "warn_error.log";
    private const string SlotLogFile = "slot.log";
    private const string DumpLogFile = "request_dump.log";

    public LogPipeline(string logDir, QueueFullPolicy policy, int joinTimeoutMs = 3000)
    {
        _joinTimeoutMs = joinTimeoutMs;
        _queue = new BoundedLineQueue(DefaultQueueCapacity) { Policy = policy };
        _mainWriter = new LogStreamWriter(Path.Combine(logDir, MainLogFile));
        _warnWriter = new LogStreamWriter(Path.Combine(logDir, WarnLogFile));
        _slotWriter = new LogStreamWriter(Path.Combine(logDir, SlotLogFile));
        _dumpWriter = new LogStreamWriter(Path.Combine(logDir, DumpLogFile));
        _writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "LogPipeline-Writer" };
        _writerThread.Start();
        // [LOG-PIPE] 启动埋点（约束 C-4：组件启用状态）
        Enqueue(LogStream.Main, DateTime.UtcNow, $"[LOG-PIPE] 启动：4 流，队列 {DefaultQueueCapacity}，policy={policy}");
    }

    /// <summary>统一时间戳格式（本地时间显示，Utc 值转换）——生产侧与管道共用，保证一致。</summary>
    public static string FormatLine(DateTime utc, string raw) =>
        $"[{utc.ToLocalTime():yyyy-MM-dd HH:mm:ss}] {raw}";

    /// <summary>入队一条日志（任意线程）：lock 内仅入队 + 满时丢弃计数，零编码/分类/磁盘。返回 false = 被丢弃或已停止接收。</summary>
    public bool Enqueue(LogStream stream, DateTime createUtc, string rawLine)
    {
        // M-06 修复：使用实例锁 _enqueueGate 替代静态锁，避免四流竞争（单例场景下语义等价但符合规范）
        lock (_enqueueGate)
        {
            if (!_accepting) return false;
            var msg = new LogMessage(stream, createUtc, FormatLine(createUtc, rawLine), rawLine);
            return _queue.TryEnqueue(msg);
        }
    }

    /// <summary>写线程主循环：等数据信号或 150ms tick → 批量出队处理 → Flush 判定 → shutdown drain 退出。</summary>
    private void WriterLoop()
    {
        var batch = new List<LogMessage>(BatchCapacity);
        while (true)
        {
            _wake.Wait(FlushPolicy.IntervalMs); // 数据信号（auto-reset）或超时 tick
            int total = 0;
            while (_queue.Drain(batch, BatchCapacity) > 0 && total < MaxDrainPerTick)
            {
                ProcessBatch(batch);
                total += batch.Count;
                batch.Clear();
            }
            if (_shutdownRequested && _queue.Count == 0) break;
        }
    }

    private void ProcessBatch(List<LogMessage> batch)
    {
        foreach (var msg in batch)
            ProcessOne(msg);

        // [LOG-PIPE] 丢弃埋点（直接写 main，不经队列防递归）
        var dropped = _queue.TakeDroppedDelta();
        if (dropped > 0)
        {
            lock (_perfGate) _totalDropped += dropped; // v2.22 可观测
            WriteDirectSafe($"[LOG-PIPE] dropped={dropped} policy={_queue.Policy}{Environment.NewLine}");
        }

        // Flush 双阈值判定（四流一起刷，与旧 150ms 定时器同节奏）
        var elapsedMs = (long)(DateTime.UtcNow - _lastFlushUtc).TotalMilliseconds;
        var maxPending = Math.Max(
            Math.Max(_mainWriter.PendingBytes, _warnWriter.PendingBytes),
            Math.Max(_slotWriter.PendingBytes, _dumpWriter.PendingBytes));
        if (FlushPolicy.ShouldFlush(elapsedMs, maxPending))
        {
            var fsw = System.Diagnostics.Stopwatch.StartNew(); // v2.22 可观测：flush 单次耗时
            _mainWriter.Flush();
            _warnWriter.Flush();
            _slotWriter.Flush();
            _dumpWriter.Flush();
            lock (_perfGate) { _flushCount++; _flushSumMs += fsw.Elapsed.TotalMilliseconds; }
            _lastFlushUtc = DateTime.UtcNow;
        }
    }

    private void ProcessOne(LogMessage msg)
    {
        try
        {
            switch (msg.Stream)
            {
                case LogStream.Main: WriteMain(msg); break;
                case LogStream.Slot: WritePlain(_slotWriter, MaxSlotBytes, msg.StampedLine + Environment.NewLine); break;
                case LogStream.Dump: WritePlain(_dumpWriter, MaxDumpBytes, msg.StampedLine + Environment.NewLine); break;
            }
            _consecutiveIoFailures = 0;
        }
        catch
        {
            // IO 异常（磁盘满/占用/权限）：计数 + 连续失败退避，防死循环打满 CPU；尽力而为不重试单条
            Interlocked.Increment(ref _ioFailCount);
            if (++_consecutiveIoFailures >= 3) Thread.Sleep(200);
        }
    }

    /// <summary>main 流：Classify（原始行）→ 写 harness.log → 非 Info 派生 warn 块（上下文 = 该条之前 10 条）。</summary>
    private void WriteMain(LogMessage msg)
    {
        var lvl = LogFile.Classify(msg.RawLine);
        WritePlain(_mainWriter, MaxMainBytes, msg.StampedLine + Environment.NewLine);
        if (lvl != LogFile.Level.Info)
            WritePlain(_warnWriter, MaxWarnBytes, BuildWarnBlock(lvl, msg.StampedLine));
        _warnContext.Enqueue(msg.StampedLine);
        while (_warnContext.Count > WarnContextLines) _warnContext.Dequeue();
    }

    private string BuildWarnBlock(LogFile.Level lvl, string stampedLine)
    {
        var sb = new StringBuilder();
        foreach (var l in _warnContext)
            sb.Append(l).Append(Environment.NewLine);
        sb.Append($"===== {lvl} =====").Append(Environment.NewLine);
        sb.Append(stampedLine).Append(Environment.NewLine);
        return sb.ToString();
    }

    private static void WritePlain(LogStreamWriter writer, long maxBytes, string text)
    {
        if (writer.RotateIfNeeded(maxBytes)) { /* 已轮切，下条自动重开 */ }
        writer.Write(text);
    }

    /// <summary>直接写盘（绕过队列）：埋点行专用，尽力而为。</summary>
    private void WriteDirectSafe(string text)
    {
        try
        {
            WritePlain(_mainWriter, MaxMainBytes, text);
            _mainWriter.Flush();
        }
        catch
        {
            // 埋点失败不影响主流程
        }
    }

    /// <summary>Shutdown：停止接收 → drain 全部队列 → 最终 Flush。返回 (是否完整 drain, 剩余行数)。
    /// 正常：[LOG-PIPE] shutdown drained；超时：[LOG-PIPE] shutdown drain timeout, remain N lines lost。</summary>
    public (bool Completed, int Remaining) Shutdown()
    {
        if (_disposed) return (true, 0);
        _accepting = false;
        _shutdownRequested = true;
        _wake.Set();
        bool joined = _writerThread.Join(_joinTimeoutMs);
        if (joined)
        {
            WriteDirectSafe($"[LOG-PIPE] shutdown drained，队列清空{Environment.NewLine}");
            DisposeWriters();
            _disposed = true;
            return (true, 0);
        }
        // 超时：写线程可能卡在 IO；daemon 线程随进程退出，不强制 Dispose（避免与写线程竞争句柄）
        var remaining = _queue.Count;
        WriteDirectSafe($"[LOG-PIPE] shutdown drain timeout, remain {remaining} lines lost{Environment.NewLine}");
        return (false, remaining);
    }

    private void DisposeWriters()
    {
        _mainWriter.Dispose();
        _warnWriter.Dispose();
        _slotWriter.Dispose();
        _dumpWriter.Dispose();
    }

    public void Dispose() => Shutdown();
}
