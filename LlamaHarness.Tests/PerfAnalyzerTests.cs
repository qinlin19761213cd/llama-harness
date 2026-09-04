using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// PerfAnalyzer 双源分析单测（v2.21）：连续窗口阈值检测（Above/Below/持续时长/空值打断）、
/// 单请求时延告警、趋势摘要、perf.log 离线解析。纯函数无状态，直接构造 PerfPoint 序列验证。
/// </summary>
public class PerfAnalyzerTests
{
    private static PerfPoint Pt(double? cpu = null, double? vram = null, double? tg = null, double? ctx = null, int? inflight = null, int seq = 0)
        => new()
        {
            Ts = new DateTime(2026, 9, 1, 10, 0, 0).AddSeconds(seq),
            CpuPercent = cpu,
            VramUsedMb = vram,
            TgTps = tg,
            CtxUsedPct = ctx,
            Inflight = inflight,
        };

    private static List<PerfThresholdRule> TgRule(int dur = 30) => new()
    {
        new PerfThresholdRule { Metric = "tg_tps", Direction = PerfThresholdDirection.Below, WarnValue = 10, CritValue = 3, MinDurationSeconds = dur },
    };
    private static List<PerfThresholdRule> CpuRule(int dur = 30) => new()
    {
        new PerfThresholdRule { Metric = "cpu", Direction = PerfThresholdDirection.Above, WarnValue = 90, CritValue = 97, MinDurationSeconds = dur },
    };

    [Fact]
    public void EvaluatePoints_ShortSpike_NoAlarm()
    {
        // 5 个点超 90 但持续时间 < 30：不应告警
        var pts = Enumerable.Range(0, 5).Select(i => Pt(cpu: 92, seq: i)).ToList();
        Assert.Empty(PerfAnalyzer.EvaluatePoints(pts, CpuRule()));
    }

    [Fact]
    public void EvaluatePoints_AboveWarnDuration_FiresWarn()
    {
        var pts = Enumerable.Range(0, 32).Select(i => Pt(cpu: 92, seq: i)).ToList(); // 持续 32s > 90 < 97
        var alarms = PerfAnalyzer.EvaluatePoints(pts, CpuRule());
        var a = Assert.Single(alarms);
        Assert.Equal(PerfAlarmLevel.Warn, a.Level);
        Assert.Equal("cpu", a.Metric);
        Assert.Equal(92, a.Value);
    }

    [Fact]
    public void EvaluatePoints_AboveCrit_FiresCrit()
    {
        var pts = Enumerable.Range(0, 32).Select(i => Pt(cpu: 98, seq: i)).ToList(); // 持续 32s > 97
        var alarms = PerfAnalyzer.EvaluatePoints(pts, CpuRule());
        var a = Assert.Single(alarms);
        Assert.Equal(PerfAlarmLevel.Crit, a.Level);
    }

    [Fact]
    public void EvaluatePoints_Boundary32s_ExactlyFires()
    {
        // 32 点 92 → 持续越过 32s > 30 → 触发 Warn（验证 32s 边界确实触发）
        var pts = Enumerable.Range(0, 32).Select(i => Pt(cpu: 92, seq: i)).ToList();
        var alarms = PerfAnalyzer.EvaluatePoints(pts, CpuRule());
        Assert.Single(alarms);
    }

    [Fact]
    public void EvaluatePoints_NullAfter29sDoesNotFire()
    {
        // 29 点 92（< 30s 阈值）+ 1 点 null 打断 = 30 点总时长，但连续越过仅 29s → 不告警
        var pts = Enumerable.Range(0, 29).Select(i => Pt(cpu: 92, seq: i))
            .Append(Pt(cpu: null, seq: 29))
            .ToList();
        Assert.Empty(PerfAnalyzer.EvaluatePoints(pts, CpuRule()));
    }

    [Fact]
    public void EvaluateTiming_59999_BoundaryNotTrigger()
    {
        // Warn 阈值 60000：59999ms 边界外不触发
        var rules = PerfThresholdRule.Defaults();
        var t = new RequestTiming { Ts = DateTime.Now, TotalMs = 59999, Success = true };
        Assert.Empty(PerfAnalyzer.EvaluateTiming(t, rules));
    }

    [Fact]
    public void EvaluateTiming_60000_EqualityNotTrigger()
    {
        // 边界相等不触发（实现为严格大于 v > WarnValue）
        var rules = PerfThresholdRule.Defaults();
        var t = new RequestTiming { Ts = DateTime.Now, TotalMs = 60000, Success = true };
        Assert.Empty(PerfAnalyzer.EvaluateTiming(t, rules));
    }

    [Fact]
    public void EvaluateTiming_60001_TriggerWarn()
    {
        // 越界 1ms 即触发 Warn（严格大于语义）
        var rules = PerfThresholdRule.Defaults();
        var t = new RequestTiming { Ts = DateTime.Now, TotalMs = 60001, Success = true };
        var alarms = PerfAnalyzer.EvaluateTiming(t, rules);
        var a = Assert.Single(alarms);
        Assert.Equal(PerfAlarmLevel.Warn, a.Level);
        Assert.Equal(60000, a.Threshold);
    }

    [Fact]
    public void EvaluateTiming_180000_EqualityNotTriggerCrit()
    {
        // Crit 阈值 180000 边界相等：实现为严格大于 → 落入 Warn 而非 Crit
        var rules = PerfThresholdRule.Defaults();
        var t = new RequestTiming { Ts = DateTime.Now, TotalMs = 180000, Success = true };
        var alarms = PerfAnalyzer.EvaluateTiming(t, rules);
        Assert.Single(alarms);
        Assert.Equal(PerfAlarmLevel.Warn, alarms[0].Level);
        Assert.Equal(60000, alarms[0].Threshold);
    }

    [Fact]
    public void EvaluateTiming_180001_TriggerCrit()
    {
        // 越界 1ms 即触发 Crit
        var rules = PerfThresholdRule.Defaults();
        var t = new RequestTiming { Ts = DateTime.Now, TotalMs = 180001, Success = true };
        var a = Assert.Single(PerfAnalyzer.EvaluateTiming(t, rules));
        Assert.Equal(PerfAlarmLevel.Crit, a.Level);
        Assert.Equal(180000, a.Threshold);
    }

    [Fact]
    public void EvaluatePoints_Boundary60s_ExactlyFiresVramWarn()
    {
        // vram_mb Warn=15000 Crit=18500 MinDuration=60：持续 60s > 15000 < 18500 → 触发 Warn
        var rules = new List<PerfThresholdRule>
        {
            new() { Metric = "vram_mb", Direction = PerfThresholdDirection.Above, WarnValue = 15000, CritValue = 18500, MinDurationSeconds = 60 },
        };
        var pts = Enumerable.Range(0, 60).Select(i => Pt(vram: 16000, seq: i)).ToList();
        var a = Assert.Single(PerfAnalyzer.EvaluatePoints(pts, rules));
        Assert.Equal(PerfAlarmLevel.Warn, a.Level);
    }

    [Fact]
    public void ParsePerfLog_ZeroTimeSpan_RoundTrip()
    {
        // 零时长按 3 种时延段分别写入，解析后 AvgTotalMs=0、MaxTotalMs=0（roundtrip 语义）
        string path = Path.Combine(Path.GetTempPath(), $"perf_{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllLines(path, new[]
            {
                "timing,2026-09-01 10:00:00.000,app=a,path=/v1/chat/completions,success=1,wake=0,gateway=0,backend=0,total=0",
                "timing,2026-09-01 10:00:01.000,app=a,path=/v1/chat/completions,success=1,wake=0.000,gateway=0.000,backend=0.000,total=0.000",
                "timing,2026-09-01 10:00:02.000,app=a,path=/v1/chat/completions,success=1,wake=0.0,gateway=0.0,backend=0.0,total=0.0",
            });
            var s = PerfAnalyzer.ParsePerfLog(path);
            Assert.Equal(3, s.Requests);
            Assert.Equal(0, s.AvgTotalMs);
            Assert.Equal(0, s.MaxTotalMs);
            Assert.Equal(0, s.FailureRate);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void ComputeSummary_EmptyPoints_ReturnsEmptySummary()
    {
        // 边界：空点集（0 点）不应抛、不应除零，PointCount=0、各指标为默认空/0
        var s = PerfAnalyzer.ComputeSummary(Array.Empty<PerfPoint>());
        Assert.Equal(0, s.PointCount);
        Assert.Null(s.AvgCpu);
        Assert.Null(s.MaxCpu);
        Assert.Null(s.AvgVramMb);
        Assert.Null(s.MaxVramMb);
        Assert.Null(s.AvgTgTps);
        Assert.Null(s.MinTgTps);
        Assert.Null(s.LastVramMb);
    }

    [Fact]
    public void ComputeSummary_AllNullPoints_ReturnsEmptyStats()
    {
        // 全 null 指标点：分母按非空点数计，各指标为 null/0 而非 NaN
        var pts = new List<PerfPoint> { Pt(), Pt() }; // 全字段 null
        var s = PerfAnalyzer.ComputeSummary(pts);
        Assert.Equal(2, s.PointCount);
        Assert.Null(s.AvgVramMb);
        Assert.Null(s.MaxVramMb);
        Assert.Null(s.AvgTgTps);
        Assert.Null(s.MinTgTps);
        Assert.Null(s.LastVramMb);
    }

    [Fact]
    public void EvaluateTiming_MultipleRules_AllTrigger()
    {
        // 多 total_ms 规则并存（不同阈值）→ 全部触发，非短路
        var rules = new List<PerfThresholdRule>
        {
            new() { Metric = "total_ms", Direction = PerfThresholdDirection.Above, WarnValue = 10000, CritValue = 20000, MinDurationSeconds = 1 },
            new() { Metric = "total_ms", Direction = PerfThresholdDirection.Above, WarnValue = 30000, CritValue = 50000, MinDurationSeconds = 1 },
        };
        var t = new RequestTiming { Ts = DateTime.Now, TotalMs = 60000, Success = true };
        var alarms = PerfAnalyzer.EvaluateTiming(t, rules);
        Assert.Equal(2, alarms.Count); // 两条规则均越 Crit
        Assert.All(alarms, a => Assert.Equal(PerfAlarmLevel.Crit, a.Level));
    }

    [Fact]
    public void EvaluateTiming_MixedRulesOnlyTotalMsApplied()
    {
        // 混合规则列表（cpu/tg_tps/total_ms）→ 仅 total_ms 参与 EvaluateTiming
        var rules = new List<PerfThresholdRule>
        {
            new() { Metric = "cpu", Direction = PerfThresholdDirection.Above, WarnValue = 90, CritValue = 97, MinDurationSeconds = 1 },
            new() { Metric = "tg_tps", Direction = PerfThresholdDirection.Below, WarnValue = 10, CritValue = 3, MinDurationSeconds = 1 },
            new() { Metric = "total_ms", Direction = PerfThresholdDirection.Above, WarnValue = 60000, CritValue = 180000, MinDurationSeconds = 1 },
        };
        var t = new RequestTiming { Ts = DateTime.Now, TotalMs = 70000, Success = true };
        var a = Assert.Single(PerfAnalyzer.EvaluateTiming(t, rules));
        Assert.Equal("total_ms", a.Metric);
    }

    [Fact]
    public void EvaluatePoints_NullValue_BreaksContinuity()
    {
        // 15 点 92 → 1 点 null（打断） → 15 点 92：各段 < 30，不告警
        var pts = Enumerable.Range(0, 15).Select(i => Pt(cpu: 92, seq: i))
            .Append(Pt(cpu: null, seq: 15))
            .Concat(Enumerable.Range(16, 15).Select(i => Pt(cpu: 92, seq: i)))
            .ToList();
        Assert.Empty(PerfAnalyzer.EvaluatePoints(pts, CpuRule()));
    }

    [Fact]
    public void EvaluatePoints_BelowDirection_FiresWarn()
    {
        var rules = new List<PerfThresholdRule>
        {
            new() { Metric = "tg_tps", Direction = PerfThresholdDirection.Below, WarnValue = 10, CritValue = 3, MinDurationSeconds = 5 },
        };
        var pts = Enumerable.Range(0, 8).Select(i => Pt(tg: 5, inflight: 1, seq: i)).ToList(); // 负载下持续 8s < 10（负载门控：空闲点不评估吞吐）
        var alarms = PerfAnalyzer.EvaluatePoints(pts, rules);
        var a = Assert.Single(alarms);
        Assert.Equal(PerfAlarmLevel.Warn, a.Level);
        Assert.Equal(5, a.Value);
    }

    [Fact]
    public void EvaluateTiming_OverThreshold_Fires()
    {
        var rules = PerfThresholdRule.Defaults(); // total_ms: warn 60000 crit 180000
        var t = new RequestTiming { Ts = DateTime.Now, TotalMs = 70000, Success = true };
        var alarms = PerfAnalyzer.EvaluateTiming(t, rules);
        var a = Assert.Single(alarms);
        Assert.Equal(PerfAlarmLevel.Warn, a.Level);
        Assert.Equal("total_ms", a.Metric);

        var t2 = new RequestTiming { Ts = DateTime.Now, TotalMs = 190000, Success = true };
        Assert.Equal(PerfAlarmLevel.Crit, Assert.Single(PerfAnalyzer.EvaluateTiming(t2, rules)).Level);
    }

    [Fact]
    public void ComputeSummary_StatsAreCorrect()
    {
        var pts = new[]
        {
            Pt(cpu: 10, vram: 1000, tg: 60, ctx: 0.1, inflight: 1, seq: 0),
            Pt(cpu: 20, vram: 2000, tg: 40, ctx: 0.3, inflight: 3, seq: 1),
            Pt(cpu: 15, vram: 1500, tg: 50, ctx: 0.2, inflight: 2, seq: 2),
        };
        var s = PerfAnalyzer.ComputeSummary(pts);
        Assert.Equal(3, s.PointCount);
        Assert.Equal(15.0, s.AvgCpu);
        Assert.Equal(20.0, s.MaxCpu);
        Assert.Equal(1500.0, s.AvgVramMb);
        Assert.Equal(2000.0, s.MaxVramMb);
        Assert.Equal(50.0, s.AvgTgTps);
        Assert.Equal(40.0, s.MinTgTps);
        Assert.Equal(0.3, s.MaxCtxPct);
        Assert.Equal(1500.0, s.LastVramMb);
        Assert.Equal(3, s.MaxInflight);
    }

    [Fact]
    public void ParsePerfLog_ReadsThreeKinds()
    {
        string path = Path.Combine(Path.GetTempPath(), $"perf_{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllLines(path, new[]
            {
                "system,2026-09-01 10:00:00.100,cpu=12.0,mem=28.5,total=64.0,vram=1234,vram_total=8192,inflight=1",
                "cpp,2026-09-01 10:00:05.100,pp_tps=0.0,tg_tps=65.2,tok=12345,ctx=0.180,slots=1",
                "timing,2026-09-01 10:00:07.200,app=trae_global,path=/v1/chat/completions,success=1,wake=0.5,gateway=8.2,backend=3200.1,total=3208.8",
                "timing,2026-09-01 10:00:09.000,app=dsh_agent,path=/v1/chat/completions,success=0,wake=0.0,gateway=1.0,backend=500.0,total=501.0",
                "broken-line-no-comma", // 容错：非法行跳过
            });
            var s = PerfAnalyzer.ParsePerfLog(path);
            Assert.Equal(5, s.TotalLines);
            Assert.Equal(1, s.SystemCount);
            Assert.Equal(1, s.CppCount);
            Assert.Equal(2, s.TimingCount);
            Assert.Equal(2, s.Requests);
            Assert.Equal(1, s.FailedRequests);
            Assert.Equal(0.5, s.FailureRate);
            Assert.Equal(1234, s.MaxVramMb);
            Assert.Equal(65.2, s.MinTgTps);
            Assert.Equal(1854.9, s.AvgTotalMs); // (3208.8+501)/2
            Assert.Equal(3208.8, s.MaxTotalMs);
            Assert.Equal(new DateTime(2026, 9, 1, 10, 0, 0).AddMilliseconds(100), s.FirstTs); // 含毫秒
            Assert.Equal(new DateTime(2026, 9, 1, 10, 0, 9), s.LastTs);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void ParsePerfLog_MissingFile_IsEmpty()
    {
        var s = PerfAnalyzer.ParsePerfLog(Path.Combine(Path.GetTempPath(), $"nope_{Guid.NewGuid():N}.log"));
        Assert.True(s.IsEmpty);
        Assert.Equal(0, s.TotalLines);
    }

    [Fact]
    public void ParsePerfLog_WhenFileLockedByWriter_StillReads()
    {
        // v2.23.1 故障回归：perf.log 存在且非空，但运行中 PerfLog 写线程持有 FileAccess.Write 句柄时，
        // File.ReadLines（FileShare.Read）与写句柄共享冲突抛 IOException → 被吞 → TotalLines=0 → 误报"为空或不存在"。
        // 修复：ReadLinesShared 用 FileShare.ReadWrite，锁写共存下仍可读取。
        string path = Path.Combine(Path.GetTempPath(), $"perf_{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllLines(path, new[]
            {
                "system,2026-09-01 10:00:00.100,cpu=12.0,mem=28.5,total=64.0,vram=1234,vram_total=8192,inflight=1",
                "cpp,2026-09-01 10:00:05.100,pp_tps=0.0,tg_tps=65.2,tok=12345,ctx=0.180,slots=1",
                "timing,2026-09-01 10:00:07.200,app=trae_global,path=/v1/chat/completions,success=1,wake=0.5,gateway=8.2,backend=3200.1,total=3208.8",
            });
            // 模拟运行中 PerfLog 写线程：持有 FileAccess.Write 句柄（FileShare.Read 只允许他人读）
            using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var sw = new StreamWriter(fs) { AutoFlush = true })
            {
                sw.WriteLine("session,2026-09-01 10:00:08.000,type=start,sid=abc123,ver=v2.22");
                sw.WriteLine("system,2026-09-01 10:00:09.000,cpu=13.0,mem=28.5,total=64.0,vram=2048,vram_total=8192,inflight=1");

                // 修复前：File.ReadLines(FileShare.Read) 与写句柄冲突 → IOException → TotalLines=0 → IsEmpty
                var s = PerfAnalyzer.ParsePerfLog(path);
                Assert.False(s.IsEmpty);
                Assert.True(s.TotalLines >= 4);
                Assert.Equal(2, s.SystemCount);
                Assert.Equal(2048, s.MaxVramMb);

                // ParseSessions 同样应能读取（写句柄共存）
                var sessions = PerfAnalyzer.ParseSessions(path);
                Assert.Single(sessions);
                Assert.Equal("abc123", sessions[0].Sid);
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
    [Fact]
    public void EvaluatePoints_IdleSkipsThroughputRule()
    {
        // 空闲（inflight=0，无处理槽）时 tg_tps=0 持续 32s：负载门控跳过 → 不误报
        var idle = Enumerable.Range(0, 32).Select(i => Pt(tg: 0, inflight: 0, seq: i)).ToList();
        Assert.Empty(PerfAnalyzer.EvaluatePoints(idle, TgRule()));

        // 有负载（inflight=1）时 tg_tps=0 持续 32s：仍触发 Crit（真异常不被吞）
        var busy = Enumerable.Range(0, 32).Select(i => Pt(tg: 0, inflight: 1, seq: i)).ToList();
        var alarms = PerfAnalyzer.EvaluatePoints(busy, TgRule());
        Assert.NotEmpty(alarms);
        Assert.All(alarms, a => Assert.Equal("tg_tps", a.Metric));
    }

    [Fact]
    public void EvaluatePoints_IdleDoesNotBreakBusyRun()
    {
        // 空闲点跳过（不重置）：16s 负载 + 16s 空闲 + 16s 负载 → 负载累计 32s 仍触发
        var pts = Enumerable.Range(0, 16).Select(i => Pt(tg: 0, inflight: 1, seq: i))
            .Concat(Enumerable.Range(16, 16).Select(i => Pt(tg: 0, inflight: 0, seq: i)))
            .Concat(Enumerable.Range(32, 16).Select(i => Pt(tg: 0, inflight: 1, seq: i)))
            .ToList();
        Assert.NotEmpty(PerfAnalyzer.EvaluatePoints(pts, TgRule()));
    }
}