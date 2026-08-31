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
        var pts = Enumerable.Range(0, 8).Select(i => Pt(tg: 5, seq: i)).ToList(); // 持续 8s < 10
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
}
