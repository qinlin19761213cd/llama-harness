using LlamaHarness;
using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// 统一异步日志管道纯逻辑单测（批次 1）：
/// - BoundedLineQueue：DropNewest/DropOldest 满时丢弃 + 计数、FIFO 保序
/// - FlushPolicy：时间/大小双阈值边界
/// - LogStreamWriter：轮切触发/不触发
/// - 高并发多线程 Enqueue：单流内部 FIFO 保序
/// 注：写线程集成行为（IO 退避 / Shutdown drain / e2e 落盘）见批次 3 集成测试。
/// </summary>
public class LogPipelineTests
{
    private static LogMessage Msg(int seq, LogStream stream = LogStream.Main) =>
        new(stream, DateTime.UtcNow, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] line-{seq}", $"line-{seq}");

    // ==================== BoundedLineQueue ====================

    [Fact]
    public void Queue_DropNewest_WhenFull_KeepsOldestAndCounts()
    {
        var q = new BoundedLineQueue(3) { Policy = QueueFullPolicy.DropNewest };
        Assert.True(q.TryEnqueue(Msg(1)));
        Assert.True(q.TryEnqueue(Msg(2)));
        Assert.True(q.TryEnqueue(Msg(3)));
        // 满：新入队被丢弃，历史保留
        Assert.False(q.TryEnqueue(Msg(4)));
        Assert.Equal(3, q.Count);
        Assert.Equal(1, q.TakeDroppedDelta());

        var batch = new List<LogMessage>(8);
        q.Drain(batch, 8);
        Assert.Equal(3, batch.Count);
        Assert.Equal("line-1", batch[0].RawLine); // 最旧仍在队首
    }

    [Fact]
    public void Queue_DropOldest_WhenFull_ReplacesOldest()
    {
        var q = new BoundedLineQueue(3) { Policy = QueueFullPolicy.DropOldest };
        q.TryEnqueue(Msg(1));
        q.TryEnqueue(Msg(2));
        q.TryEnqueue(Msg(3));
        Assert.True(q.TryEnqueue(Msg(4))); // 挤掉 line-1
        Assert.Equal(3, q.Count);
        Assert.Equal(1, q.TakeDroppedDelta());

        var batch = new List<LogMessage>(8);
        q.Drain(batch, 8);
        Assert.Equal("line-2", batch[0].RawLine); // line-1 被挤掉
    }

    [Fact]
    public void Queue_FifoOrdering()
    {
        var q = new BoundedLineQueue(100);
        for (int i = 0; i < 50; i++) q.TryEnqueue(Msg(i));
        var batch = new List<LogMessage>(64);
        q.Drain(batch, 10);
        Assert.Equal(10, batch.Count);
        for (int i = 0; i < 10; i++)
            Assert.Equal($"line-{i}", batch[i].RawLine); // 严格 FIFO
    }

    [Fact]
    public void Queue_DroppedDelta_ResetsAfterTake()
    {
        var q = new BoundedLineQueue(1) { Policy = QueueFullPolicy.DropNewest };
        q.TryEnqueue(Msg(1));
        q.TryEnqueue(Msg(2)); // dropped
        Assert.Equal(1, q.TakeDroppedDelta());
        Assert.Equal(0, q.TakeDroppedDelta()); // 增量语义：取后归零
    }

    // ==================== FlushPolicy 双阈值边界 ====================

    [Theory]
    [InlineData(149, 0, false)]   // 时间未到、大小未到 → 不刷
    [InlineData(150, 0, true)]    // 时间阈值（≥150ms）→ 刷
    [InlineData(0, 63_999, false)]// 大小未到 → 不刷
    [InlineData(0, 64_000, true)] // 大小阈值（≥64KB）→ 刷
    public void FlushPolicy_ShouldFlush_Boundaries(long elapsedMs, long bytes, bool expected)
    {
        Assert.Equal(expected, FlushPolicy.ShouldFlush(elapsedMs, bytes));
    }

    // ==================== LogStreamWriter 轮切边界 ====================

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "llama_harness_logtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Rotate_TriggersAtThreshold()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "t.log");
        var w = new LogStreamWriter(path);
        w.Write(new string('a', 100));
        Assert.False(w.RotateIfNeeded(100)); // 恰好 100，未超限 → 不轮切
        w.Write("x");                        // 超 1 字节
        Assert.True(w.RotateIfNeeded(100));  // 触发轮切
        // 备份名 = 时间戳+序号（t.log.yyyyMMdd-HHmmss[.log]），非固定 .1（C4 修复）
        Assert.Single(Directory.GetFiles(dir, "t.*.log"));
        w.Write("after-rotate");             // 自动重开新文件
        Assert.True(File.Exists(path));
        w.Dispose();
    }

    [Fact]
    public void Rotate_DoesNothingBelowThreshold()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "t.log");
        var w = new LogStreamWriter(path);
        w.Write(new string('a', 50));
        Assert.False(w.RotateIfNeeded(100));
        Assert.False(File.Exists(path + ".1"));
        w.Dispose();
    }

    // ==================== 集成：写线程 / IO 退避 / Shutdown drain / e2e 落盘 ====================

    private static bool WaitForFileContains(string path, string text, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                if (File.Exists(path))
                {
                    // FileShare.ReadWrite：与写线程的 FileStream 共存（File.ReadAllText 默认 Share=None 会失败）
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs);
                    var content = reader.ReadToEnd();
                    if (content.Contains(text)) return true;
                }
            }
            catch
            {
                // 瞬态 IO（写入中/句柄竞争）→ 重试
            }
            Thread.Sleep(50);
        }
        return false;
    }

    [Fact]
    public void Pipeline_E2E_WritesToFile_AndWarnDerived()
    {
        var dir = TempDir();
        using var p = new LogPipeline(dir, QueueFullPolicy.DropNewest);
        for (int i = 0; i < 5; i++)
            p.Enqueue(LogStream.Main, DateTime.UtcNow, $"e2e-line-{i}");
        p.Enqueue(LogStream.Main, DateTime.UtcNow, "e2e 错误行（应派生 warn 块）");
        p.Enqueue(LogStream.Slot, DateTime.UtcNow, "e2e-slot-line");

        Assert.True(WaitForFileContains(Path.Combine(dir, "harness.log"), "e2e-line-4"));
        Assert.True(WaitForFileContains(Path.Combine(dir, "warn_error.log"), "===== Error ====="));
        Assert.True(WaitForFileContains(Path.Combine(dir, "slot.log"), "e2e-slot-line"));

        var (completed, remaining) = p.Shutdown();
        Assert.True(completed);
        Assert.Equal(0, remaining);
    }

    [Fact]
    public void Pipeline_IoFailure_BackoffAndCount_ThreadSurvives()
    {
        // harness.log 路径预置为目录 → FileStream 打开必失败（模拟磁盘/权限异常）
        var dir = TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "harness.log"));
        using var p = new LogPipeline(dir, QueueFullPolicy.DropNewest, joinTimeoutMs: 500);
        for (int i = 0; i < 10; i++)
            p.Enqueue(LogStream.Main, DateTime.UtcNow, $"io-fail-{i}");

        // 轮询：连续失败每条间隔 200ms 退避，10 次失败 ≈ 1.8s
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (p.IoFailCount < 10 && sw.ElapsedMilliseconds < 5000) Thread.Sleep(50);
        Assert.True(p.IoFailCount >= 10, $"IoFailCount={p.IoFailCount}，应 ≥10（每条消息失败一次）");

        // 写线程存活：Shutdown 仍能 drain 完成（队列已消费空）
        var (completed, _) = p.Shutdown();
        Assert.True(completed);
    }

    [Fact]
    public void Pipeline_Shutdown_DrainsAll()
    {
        var dir = TempDir();
        using var p = new LogPipeline(dir, QueueFullPolicy.DropNewest);
        for (int i = 0; i < 100; i++)
            p.Enqueue(LogStream.Main, DateTime.UtcNow, $"drain-{i}");

        var (completed, remaining) = p.Shutdown();
        Assert.True(completed);
        Assert.Equal(0, remaining);
        // drain 完整：最后一条也在文件里
        Assert.True(WaitForFileContains(Path.Combine(dir, "harness.log"), "drain-99", timeoutMs: 500));
    }

    [Fact]
    public void Pipeline_Shutdown_Timeout_ReportsIncomplete()
    {
        // joinTimeout=1ms + 大量消息：Join 几乎必然超时（写线程仍在 drain）
        var dir = TempDir();
        using var p = new LogPipeline(dir, QueueFullPolicy.DropNewest, joinTimeoutMs: 1);
        for (int i = 0; i < 20_000; i++)
            p.Enqueue(LogStream.Main, DateTime.UtcNow, $"flood-{i}");

        var (completed, remaining) = p.Shutdown();
        Assert.False(completed); // Join 超时 → 未完成 drain
        Assert.True(remaining >= 0); // 剩余行数已上报（具体值取决于写线程进度，不固定断言）
    }

    [Fact]
    public void LogFile_Append_SnapshotRecent_ProducerSide()
    {
        // _recent 生产侧更新：Append 后立即 SnapshotRecent，不等写线程落盘
        for (int i = 0; i < 15; i++)
            LogFile.Append($"snapshot-test-{i}");
        var snap = LogFile.SnapshotRecent();
        Assert.Equal(LogFile.Level.Info, LogFile.Classify("snapshot-test-14")); // Classify 门面保留
        // 最近 10 条窗口：最后一条必在快照中
        Assert.Contains(snap, s => s.Contains("snapshot-test-14"));
        Assert.DoesNotContain(snap, s => s.Contains("snapshot-test-3")); // 超出窗口被挤出
    }

    // ==================== 高并发：单流内部 FIFO 保序 ====================

    [Fact]
    public void Concurrent_Enqueue_SingleStreamFifoPreserved()
    {
        // 并发语义：每个生产者自己的子序列严格递增（单流 FIFO）；跨线程交错顺序不保证。
        const int threads = 8;
        const int perThread = 500;
        var q = new BoundedLineQueue(threads * perThread);
        var ts = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            int threadId = t;
            ts[t] = new Thread(() =>
            {
                for (int i = 0; i < perThread; i++)
                    q.TryEnqueue(Msg(threadId * perThread + i)); // seq 编码 (threadId, i)
            });
            ts[t].Start();
        }
        foreach (var t in ts) t.Join();

        var batch = new List<LogMessage>(1024);
        var lastIdx = new int[threads]; // 每生产者最后见到的 i
        Array.Fill(lastIdx, -1);
        var count = new int[threads];
        int total = 0;
        while (q.Drain(batch, 1024) > 0)
        {
            foreach (var m in batch)
            {
                int seq = int.Parse(m.RawLine.Substring("line-".Length));
                int tid = seq / perThread;
                int idx = seq % perThread;
                Assert.True(idx > lastIdx[tid], $"生产者 {tid} 子序列乱序：{idx} <= {lastIdx[tid]}");
                lastIdx[tid] = idx;
                count[tid]++;
                total++;
            }
            batch.Clear();
        }
        Assert.Equal(threads * perThread, total);
        for (int t = 0; t < threads; t++)
            Assert.Equal(perThread, count[t]); // 无丢失
    }
}
