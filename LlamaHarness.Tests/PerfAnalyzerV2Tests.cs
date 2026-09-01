using LlamaHarness;
using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// PerfAnalyzer v2 会话分析单测（v2.22 可观测）：perf.log 按 sid 会话分组聚合摘要 + 跨会话退化归因。
/// 测试用临时文件直接构造 perf.log 行格式，不经过 PerfLog 写入器（隔离真实日志）。
/// </summary>
public class PerfAnalyzerV2Tests
{
    private static string WriteTempLog(string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), "pa_v2_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void ParseSessions_SplitsByBoundary_AndAggregatesAllKinds()
    {
        var path = WriteTempLog(new[]
        {
            "session,2026-09-01 10:00:00.000,type=start,sid=sess1,ver=2.22",
            "kv,2026-09-01 10:00:01.000,op=save,ms=12.3,key=k1",
            "kv,2026-09-01 10:00:02.000,op=save,ms=14.5,key=k1",
            "kv,2026-09-01 10:00:03.000,op=restore,ms=50.0,key=k1",
            "sched,2026-09-01 10:00:04.000,op=slot_select,ms=0.7,key=app_x",
            "sched,2026-09-01 10:00:05.000,op=slot_select,ms=1.3,key=app_y",
            "sched,2026-09-01 10:00:06.000,op=wakeup,ms=3000.0",
            "count,2026-09-01 10:00:07.000,evict=2,preempt=1,log_dropped=7,log_flush=2.50,kv_hit=95,kv_false=5,saved_n=4096,kv_full=8", // v2.23.10
            "timing,2026-09-01 10:00:08.000,app=a,path=/v1,success=1,total=3200.0",
            "session,2026-09-01 10:00:09.000,type=end,sid=sess1",
            "session,2026-09-01 10:10:00.000,type=start,sid=sess2,ver=2.22",
            "session,2026-09-01 10:10:05.000,type=end,sid=sess2",
        });
        try
        {
            var sessions = PerfAnalyzer.ParseSessions(path);
            Assert.Equal(2, sessions.Count);
            var s1 = sessions[0];
            Assert.Equal("sess1", s1.Sid);
            Assert.Equal("2.22", s1.Version);
            Assert.Equal(2, s1.KvSaveCount);
            Assert.Equal(13.4, s1.AvgKvSaveMs); // (12.3+14.5)/2
            Assert.Equal(1, s1.KvRestoreCount);
            Assert.Equal(50.0, s1.AvgKvRestoreMs);
            Assert.Equal(2, s1.SlotSelectCount);
            Assert.Equal(1.0, s1.AvgSlotSelectMs);
            Assert.Equal(1.3, s1.MaxSlotSelectMs);
            Assert.Equal(1, s1.WakeupCount);
            Assert.Equal(3000.0, s1.AvgWakeupMs);
            Assert.Equal(95, s1.KvHit);
            Assert.Equal(5, s1.KvFalseMiss);
            Assert.Equal(4096, s1.SavedN);
            Assert.Equal(2, s1.Evict);
            Assert.Equal(1, s1.Preempt);
            Assert.Equal(7, s1.LogDropped);
            Assert.Equal(8, s1.KvFullPrefill); // v2.23.10
            Assert.Equal(2.5, s1.LogFlushAvgMs);
            Assert.Equal(1, s1.Requests);
            Assert.Equal(3200.0, s1.AvgTotalMs);
            Assert.Equal(0.95, s1.KvHitRate, 2);
            // 会话 2 无事件 → 空聚合
            Assert.Equal("sess2", sessions[1].Sid);
            Assert.Equal(0, sessions[1].KvSaveCount);
            Assert.Equal(0, sessions[1].Requests);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void ParseSessions_UnclosedSession_IsFlushed()
    {
        var path = WriteTempLog(new[] { "session,2026-09-01 10:00:00.000,type=start,sid=sessX,ver=2.22" });
        try
        {
            var sessions = PerfAnalyzer.ParseSessions(path);
            Assert.Single(sessions);
            Assert.Equal("sessX", sessions[0].Sid);
            Assert.Null(sessions[0].End);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void ParseSessions_CountLastRowWins()
    {
        var path = WriteTempLog(new[]
        {
            "session,2026-09-01 10:00:00.000,type=start,sid=sessC,ver=2.22",
            "count,2026-09-01 10:00:05.000,evict=1,log_dropped=3",
            "count,2026-09-01 10:00:10.000,evict=5,log_dropped=9",
            "session,2026-09-01 10:00:15.000,type=end,sid=sessC",
        });
        try
        {
            var s = PerfAnalyzer.ParseSessions(path)[0];
            Assert.Equal(5, s.Evict);   // 末行覆盖
            Assert.Equal(9, s.LogDropped);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void CompareSessions_DetectsRegressionItems()
    {
        var baseS = new PerfSessionSummary { Sid = "s1", AvgTotalMs = 1000, MinTgTps = 50, AvgSlotSelectMs = 0.5, KvHit = 90, KvFalseMiss = 10, LogDropped = 5 };
        var curS = new PerfSessionSummary { Sid = "s2", AvgTotalMs = 1500, MinTgTps = 30, AvgSlotSelectMs = 2.0, KvHit = 70, KvFalseMiss = 30, LogDropped = 100 };
        var reg = PerfAnalyzer.CompareSessions(baseS, curS);
        var metrics = reg.Items.Select(i => i.Metric).ToHashSet();
        Assert.Contains("avg_total_ms", metrics);
        Assert.Contains("min_tg_tps", metrics);
        Assert.Contains("avg_slot_select_ms", metrics);
        Assert.Contains("kv_hit_rate", metrics);
        Assert.Contains("log_dropped", metrics);
        var total = reg.Items.First(i => i.Metric == "avg_total_ms");
        Assert.NotNull(total.Cause); // 总时延归因有分解提示
    }

    [Fact]
    public void CompareSessions_NoBaseline_Or_NoChange_Empty()
    {
        var curS = new PerfSessionSummary { Sid = "s2", AvgTotalMs = 1500 };
        Assert.Empty(PerfAnalyzer.CompareSessions(null, curS).Items);
        // 无显著变化（+5% < 10%）→ 无项
        var baseS = new PerfSessionSummary { Sid = "s1", AvgTotalMs = 1000 };
        var sameS = new PerfSessionSummary { Sid = "s2", AvgTotalMs = 1050 };
        Assert.Empty(PerfAnalyzer.CompareSessions(baseS, sameS).Items);
    }
}
