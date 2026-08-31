using System.Text;

namespace LlamaHarness;

/// <summary>
/// 性能日志（v2.21）：独立常驻直写 logs/perf.log，机器可解析的统一格式，每行一条记录。
/// 与主日志（LogPipeline）刻意分离——性能采样 1 行/秒且高频，不占用主有界队列（50k 四流共享单写线程）：
/// 统一的是格式协议（Kind,ts,key=value,...），不是写入通道（方案②边界）。
/// 记录类别：system（1s 系统指标）/ cpp（5s llama.cpp 指标）/ timing（请求级网关时延事件）/
/// kv（KV save/restore 事件）/ sched（槽选择/唤醒事件）/ count（调度+日志管道累积快照，5s）/ session（会话边界 sid+版本）。
/// 轮切：5MB × 3 份（perf.log → .1 → .2 依次后移，删除最旧）。线程安全（lock）、尽力而为（不抛出）。
/// 格式化数值用 F1 保留一位小数，布尔用 1/0，缺失字段省略（分析端按 Key 存在性判断）。
/// </summary>
public static class PerfLog
{
    /// <summary>单文件大小上限（5MB），超限轮切。</summary>
    private const long MaxFileBytes = 5 * 1024 * 1024;
    /// <summary>保留备份份数（perf.log + .1 + .2 共 3 份）。</summary>
    private const int MaxBackups = 2;

    private static readonly object _gate = new();
    private static StreamWriter? _writer;
    private static long _bytes;

    /// <summary>主 perf.log 路径（logs/perf.log）。</summary>
    public static string Path => AppPaths.PerfLog;

    /// <summary>启动：追加模式打开（不存在自动创建）；重启时已超限先轮切。幂等。</summary>
    public static void Start()
    {
        lock (_gate)
        {
            if (_writer != null) return;
            AppPaths.EnsureLogDir();
            var path = AppPaths.PerfLog;
            _bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (_bytes >= MaxFileBytes) RotateLocked();
            _writer = OpenWriter(path);
        }
    }

    /// <summary>记录一条系统层采样（1s 一次；含最近缓存延续的显存值）。</summary>
    public static void LogSystem(PerfPoint p)
    {
        var sb = new StringBuilder(96);
        if (p.CpuPercent is double cpu) sb.Append(",cpu=").Append(cpu.ToString("F1"));
        if (p.MemUsedGb is double mu) sb.Append(",mem=").Append(mu.ToString("F1"));
        if (p.MemTotalGb is double mt) sb.Append(",total=").Append(mt.ToString("F1"));
        if (p.VramUsedMb is double vu) sb.Append(",vram=").Append(vu.ToString("F0"));
        if (p.VramTotalMb is double vt) sb.Append(",vram_total=").Append(vt.ToString("F0"));
        if (p.Inflight is int inf) sb.Append(",inflight=").Append(inf);
        WriteLine("system", sb);
    }

    /// <summary>记录一条 llama.cpp 层采样（5s 一次，含 cpp 字段时才写）。</summary>
    public static void LogCpp(PerfPoint p)
    {
        var sb = new StringBuilder(96);
        if (p.PpTps is double pp) sb.Append(",pp_tps=").Append(pp.ToString("F1"));
        if (p.TgTps is double tg) sb.Append(",tg_tps=").Append(tg.ToString("F1"));
        if (p.TokensCached is long tok) sb.Append(",tok=").Append(tok);
        if (p.CtxUsedPct is double ctx) sb.Append(",ctx=").Append(ctx.ToString("F3"));
        if (p.SlotsProcessing is int sp) sb.Append(",slots=").Append(sp);
        WriteLine("cpp", sb);
    }

    /// <summary>记录一条请求级时延事件（timing，每次推理请求完成）。</summary>
    public static void LogTiming(RequestTiming t)
    {
        var sb = new StringBuilder(128);
        sb.Append(",app=").Append(Escape(t.App));
        sb.Append(",path=").Append(Escape(t.Path));
        sb.Append(",success=").Append(t.Success ? 1 : 0);
        sb.Append(",wake=").Append(t.WakeWaitMs.ToString("F1"));
        sb.Append(",gateway=").Append(t.GatewayMs.ToString("F1"));
        sb.Append(",backend=").Append(t.BackendMs.ToString("F1"));
        sb.Append(",total=").Append(t.TotalMs.ToString("F1"));
        WriteLine("timing", sb);
    }

    /// <summary>记录一条 kv/sched 事件行：op + 单次耗时 + 关联 key。</summary>
    public static void LogEvent(string kind, PerfEvent e)
    {
        var sb = new StringBuilder(96);
        sb.Append(",op=").Append(Escape(e.Op));
        sb.Append(",ms=").Append(e.DurationMs.ToString("F1"));
        if (!string.IsNullOrEmpty(e.Key)) sb.Append(",key=").Append(Escape(e.Key));
        WriteLine(kind, sb);
    }

    /// <summary>记录一条累积计数行（count，5s 节奏）：调度驱逐/强占 + 日志管道丢弃/flush 的绝对累积快照（分析端相邻差分为增量）。</summary>
    public static void LogCounts(PerfPoint p)
    {
        var sb = new StringBuilder(96);
        if (p.EvictCount is int ev) sb.Append(",evict=").Append(ev);
        if (p.PreemptTrigger is int pre) sb.Append(",preempt=").Append(pre);
        if (p.LogDroppedLines is long ld) sb.Append(",log_dropped=").Append(ld);
        if (p.LogFlushCostMs is double lf) sb.Append(",log_flush=").Append(lf.ToString("F2"));
        WriteLine("count", sb);
    }

    /// <summary>新会话边界：写 session 起始行，返回 sid（进程级会话 UUID，跨会话/跨版本对比锚点）。</summary>
    public static string StartSession(string version)
    {
        var sid = Guid.NewGuid().ToString("N");
        var sb = new StringBuilder(64);
        sb.Append(",type=start").Append(",sid=").Append(sid).Append(",ver=").Append(Escape(version));
        WriteLine("session", sb);
        return sid;
    }

    /// <summary>会话结束边界：写 session 结束行（summary 为可选摘要负载）。</summary>
    public static void EndSession(string sid, string? summary = null)
    {
        var sb = new StringBuilder(64);
        sb.Append(",type=end").Append(",sid=").Append(Escape(sid));
        if (!string.IsNullOrEmpty(summary)) sb.Append(",summary=").Append(Escape(summary));
        WriteLine("session", sb);
    }

    /// <summary>停止：Flush + 关闭写入器（进程退出时调用）。幂等。</summary>
    public static void Stop()
    {
        lock (_gate)
        {
            if (_writer == null) return;
            try { _writer.Flush(); } catch { }
            try { _writer.Dispose(); } catch { }
            _writer = null;
            _bytes = 0;
        }
    }

    /// <summary>统一写出：Kind,ts,payload；超限轮切。线程安全。</summary>
    private static void WriteLine(string kind, StringBuilder payload)
    {
        lock (_gate)
        {
            if (_writer == null) return; // 未 Start：丢弃（尽力而为，不自动启动避免隐式副作用）
            var line = string.Concat(kind, ",", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), payload);
            try
            {
                _writer.WriteLine(line);
                _bytes += Encoding.UTF8.GetByteCount(line) + 2; // +CRLF
                if (_bytes >= MaxFileBytes) RotateLocked();
            }
            catch
            {
                // 磁盘满/IO 异常：尽力而为，不抛出
            }
        }
    }

    /// <summary>轮切（_gate 内调用）：关旧流 → 删除最旧 .2 → 依次后移 → 开新流。</summary>
    private static void RotateLocked()
    {
        try
        {
            if (_writer != null) { _writer.Flush(); _writer.Dispose(); _writer = null; }
            var basePath = AppPaths.PerfLog;
            for (int i = MaxBackups; i >= 1; i--)
            {
                var dst = $"{basePath}.{i}";
                var src = $"{basePath}.{i - 1}";
                if (i == MaxBackups)
                {
                    if (File.Exists(dst)) File.Delete(dst);
                    if (File.Exists(src)) File.Move(src, dst, overwrite: true);
                }
                else if (File.Exists(src)) File.Move(src, dst, overwrite: true);
            }
            if (File.Exists(basePath)) File.Move(basePath, $"{basePath}.1", overwrite: true);
            _writer = OpenWriter(basePath);
            _bytes = 0;
        }
        catch
        {
            // 轮切失败：尽力恢复（下次写入再试）
            if (_writer == null) _writer = OpenWriter(AppPaths.PerfLog);
        }
    }

    /// <summary>以追加模式打开写入器（AutoFlush：每条立即可读，崩溃不丢已记行）。</summary>
    private static StreamWriter OpenWriter(string path)
    {
        return new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
    }

    /// <summary>值转义：应用名/路径中若含逗号则替换（保持每行 Key=Value 可解析）。</summary>
    private static string Escape(string v) => v.Replace(',', ' ').Replace('\n', ' ').Replace('\r', ' ');
}
