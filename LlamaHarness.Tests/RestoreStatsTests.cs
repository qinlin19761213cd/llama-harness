using LlamaHarness;
using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// Restore 命中率可观测单测（3.1）：
/// - 四象限判定（HitByDelta / FullPrefill / MidRange 保守 miss / savedN 未知退化）
/// - prompt eval 行解析
/// - FIFO 归属（最旧优先、空队列返回 null、TTL 防错位）
/// - 四象限计数（false_miss / false_hit）
/// - 告警状态迁移（&lt;50% 红、同级别不重复）
/// - 持久化往返（原子写 + Load 恢复）
/// </summary>
public class RestoreStatsTests
{
    /// <summary>测试用临时持久化路径（避免污染真实 config/）。</summary>
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"restore_stats_test_{Guid.NewGuid():N}.json");

    [Fact]
    public void Judge_HitByDelta_SmallEval()
    {
        var (hit, reason) = RestoreStats.Judge(656, 171200);
        Assert.True(hit);
        Assert.Equal("HitByDelta", reason);
    }

    [Fact]
    public void Judge_FullPrefill_LargeEval()
    {
        var (hit, reason) = RestoreStats.Judge(171856, 171200);
        Assert.False(hit);
        Assert.Equal("FullPrefill", reason);
    }

    [Fact]
    public void Judge_MidRange_ConservativeMiss()
    {
        // 50000 > 4096（非命中）且 < 171200*0.5=85600（非全量）→ 中间态保守 miss
        var (hit, reason) = RestoreStats.Judge(50000, 171200);
        Assert.False(hit);
        Assert.Equal("MidRange", reason);
    }

    [Fact]
    public void Judge_SavedN_Zero_DegratesToMiss()
    {
        // savedN 未知：全量估计退化为 eval 值本身 → 恒为 FullPrefill miss
        var (hit, reason) = RestoreStats.Judge(100000, 0);
        Assert.False(hit);
        Assert.Equal("FullPrefill", reason);
    }

    [Fact]
    public void TryParsePromptEvalTokens_ValidLine()
    {
        Assert.True(RestoreStats.TryParsePromptEvalTokens(
            "srv  prompt eval time = 123.4 ms / 656 tokens ( 5.324 ms/token)", out int n));
        Assert.Equal(656, n);
    }

    [Fact]
    public void TryParsePromptEvalTokens_NonMatchingLine()
    {
        Assert.False(RestoreStats.TryParsePromptEvalTokens("eval time = 10 ms / 5 tokens", out _));
        Assert.False(RestoreStats.TryParsePromptEvalTokens("total time = 1234 ms", out _));
    }

    [Fact]
    public void Fifo_PopsOldest_First()
    {
        var s = new RestoreStats(TempPath());
        s.RecordRequest("key_a", 0, false, 171200);
        s.RecordRequest("key_b", 1, true, 171200);
        var r1 = s.OnPromptEval(100);   // 弹 key_a
        var r2 = s.OnPromptEval(100);   // 弹 key_b
        Assert.NotNull(r1);
        Assert.NotNull(r2);
        Assert.Equal("key_a", r1!.Key);
        Assert.Equal("key_b", r2!.Key);
    }

    [Fact]
    public void Fifo_Empty_ReturnsNull()
    {
        var s = new RestoreStats(TempPath());
        Assert.Null(s.OnPromptEval(100));
    }

    [Fact]
    public void Fifo_Ttl_ExpiredEntry_Dropped()
    {
        var s = new RestoreStats(TempPath()) { PendingTtl = TimeSpan.FromMilliseconds(1) };
        s.RecordRequest("key_x", 0, false, 171200);
        Thread.Sleep(30); // 条目过期（TTL 防错位：非判定上下文任务的 print_timing 不应消费旧条目）
        Assert.Null(s.OnPromptEval(100));
    }

    [Fact]
    public void FourQuadrant_CountsFalseMissAndFalseHit()
    {
        var s = new RestoreStats(TempPath());
        // wrapper 报 MISS + 实际命中 → hits + false_miss
        s.RecordRequest("k1", 0, wrapperHit: false, savedN: 171200);
        var r1 = s.OnPromptEval(100);
        Assert.True(r1!.Hit);
        Assert.True(r1.FalseMiss);
        Assert.False(r1.FalseHit);
        // wrapper 报 HIT + 实际未命中 → misses + false_hit
        s.RecordRequest("k2", 1, wrapperHit: true, savedN: 171200);
        var r2 = s.OnPromptEval(171856);
        Assert.False(r2!.Hit);
        Assert.True(r2.FalseHit);
        Assert.False(r2.FalseMiss);

        var snap = s.Snapshot();
        Assert.Equal(2, snap.TotalAttempts);
        Assert.Equal(1, snap.TotalHits);
        Assert.Equal(1, snap.TotalFalseMiss);
        Assert.Equal(1, snap.TotalFalseHit);
    }

    [Fact]
    public void Alert_Red_Below50Percent_StateTransitionOnly()
    {
        var s = new RestoreStats(TempPath());
        // 前 4 次：2 hit + 2 miss（样本 < 5，不评估告警）
        for (int i = 0; i < 4; i++)
        {
            s.RecordRequest($"k{i}", 0, false, 171200);
            var r = s.OnPromptEval(i % 2 == 0 ? 100 : 171856);
            Assert.Equal(RestoreStats.AlertLevel.None, r!.Alert); // 样本不足
        }
        // 第 5 次 miss → 2/5 = 40% < 50% → 红色告警（状态迁移）
        s.RecordRequest("k5", 0, false, 171200);
        var r5 = s.OnPromptEval(171856);
        Assert.Equal(RestoreStats.AlertLevel.Red, r5!.Alert);
        // 第 6 次 miss → 2/6 = 33% 仍 Red → 同级别不重复告警
        s.RecordRequest("k6", 0, false, 171200);
        var r6 = s.OnPromptEval(171856);
        Assert.Equal(RestoreStats.AlertLevel.None, r6!.Alert);
    }

    [Fact]
    public void Persistence_RoundTrip()
    {
        var path = TempPath();
        var s = new RestoreStats(path);
        s.RecordRequest("trae_global", 0, false, 171200);
        s.OnPromptEval(100); // hit
        s.Save();

        Assert.True(File.Exists(path));
        // 新实例从文件恢复累计统计
        var s2 = new RestoreStats(path);
        var snap = s2.Snapshot();
        Assert.Equal(1, snap.TotalAttempts);
        Assert.Equal(1, snap.TotalHits);
        Assert.Single(snap.ByKey);
        Assert.Equal("trae_global", snap.ByKey[0].Key);
        File.Delete(path);
    }
    // ── v2.23.10 前缀漂移检测 ──
    [Fact]
    public void DriftAlert_FullPrefillChain_TriggersAfterThree()
    {
        // 存在快照（savedN>0）连续 3 次全量 prefill → 第 3 次触发前缀漂移告警
        var s = new RestoreStats(TempPath());
        for (int i = 0; i < 2; i++)
        {
            s.RecordRequest("trae_global", 0, wrapperHit: true, savedN: 1000);
            var r = s.OnPromptEval(5000); // 5000>4096 且 >= 全量估计 → FullPrefill
            Assert.False(r!.Hit);
            Assert.Equal("FullPrefill", r.Reason);
            Assert.False(r.DriftAlert); // 前两次不告警
        }
        s.RecordRequest("trae_global", 0, wrapperHit: true, savedN: 1000);
        var r3 = s.OnPromptEval(5000);
        Assert.False(r3!.Hit);
        Assert.True(r3.DriftAlert); // 第 3 次告警
        Assert.Equal(1, s.DriftAlertCount);
    }

    [Fact]
    public void DriftAlert_HitInterruptsChain_ResetsCounting()
    {
        // HitByDelta（增量命中）打断全量链 → 重新计数，需再连续 3 次全量才告警
        var s = new RestoreStats(TempPath());
        s.RecordRequest("k", 0, true, 1000); s.OnPromptEval(5000); // FP chain=1
        s.RecordRequest("k", 0, true, 1000); s.OnPromptEval(100);  // HIT 打断 chain=0
        s.RecordRequest("k", 0, true, 1000); s.OnPromptEval(5000); // FP chain=1
        s.RecordRequest("k", 0, true, 1000); s.OnPromptEval(5000); // FP chain=2
        s.RecordRequest("k", 0, true, 1000); var r = s.OnPromptEval(5000); // FP chain=3 → 告警
        Assert.True(r!.DriftAlert);
        // 打断后再触发一次告警（链归零后允许再次告警）
        s.RecordRequest("k", 0, true, 1000); s.OnPromptEval(100);  // HIT 重置
        s.RecordRequest("k", 0, true, 1000); s.OnPromptEval(5000);
        s.RecordRequest("k", 0, true, 1000); s.OnPromptEval(5000);
        s.RecordRequest("k", 0, true, 1000); var r2 = s.OnPromptEval(5000); // 再次 3 连 → 再次告警
        Assert.True(r2!.DriftAlert);
        Assert.Equal(2, s.DriftAlertCount);
    }

    [Fact]
    public void DriftAlert_NoSnapshotFullPrefill_NotDrift()
    {
        // savedN=0（无快照/首次存档）全量 prefill 是正常行为，不算前缀漂移
        var s = new RestoreStats(TempPath());
        for (int i = 0; i < 3; i++)
        {
            s.RecordRequest("k", 0, true, savedN: 0);
            var r = s.OnPromptEval(5000);
            Assert.False(r!.Hit);
            Assert.False(r.DriftAlert); // 永不告警
        }
        Assert.Equal(0, s.DriftAlertCount);
    }

    [Fact]
    public void PerfSnapshot_IncludesFullPrefillCount()
    {
        var s = new RestoreStats(TempPath());
        Assert.Equal(0, s.PerfSnapshot().TotalFullPrefill);
        s.RecordRequest("k", 0, true, 1000);
        s.OnPromptEval(5000); // 1 次全量
        s.RecordRequest("k", 0, true, 100);
        s.OnPromptEval(500); // 1 次命中
        var snap = s.PerfSnapshot();
        Assert.Equal(1, snap.TotalFullPrefill);
        Assert.Equal(1, snap.TotalHits);
    }
    [Fact]
    public void TryParsePromptEvalLine_Tps_TokensPerSecond_And_MsPerToken()
    {
        // v2.23.11：llama.cpp 新格式 "( 1.04 ms per token, 961.60 tokens per second)"
        Assert.True(RestoreStats.TryParsePromptEvalLine(
            "0.10.291.480 I slot print_timing: id  0 | task 4 | prompt eval time =  8571.15 ms / 8242 tokens ( 1.04 ms per token, 961.60 tokens per second)",
            out var tok, out var ms, out var tps));
        Assert.Equal(8242, tok);
        Assert.Equal(8571.15, ms, 2);
        Assert.Equal(961.60, tps, 2);
        // 旧格式 "( 5.324 ms/token)" → tps = 1000/5.324
        Assert.True(RestoreStats.TryParsePromptEvalLine(
            "srv  prompt eval time = 123.4 ms / 656 tokens ( 5.324 ms/token)",
            out var tok2, out _, out var tps2));
        Assert.Equal(656, tok2);
        Assert.Equal(1000.0 / 5.324, tps2, 2);
        // 无吞吐段：tokens/ms 仍解析，tps=0
        Assert.True(RestoreStats.TryParsePromptEvalLine(
            "prompt eval time = 10.0 ms / 5 tokens", out var tok3, out var ms3, out var tps3));
        Assert.Equal(5, tok3);
        Assert.Equal(0, tps3);
    }

    [Fact]
    public void Roi_HitByDelta_AccumulatesReuseTokensAndSavedMs()
    {
        // v2.23.11：hit 时 saved_n 即复用 token；节省时间用「全量 prefill 参考吞吐」折算
        var s = new RestoreStats(TempPath());
        // 先触发 FullPrefill，建立真实全量吞吐参考（8242 token / 961.6 tps）
        s.RecordRequest("k_roi", 0, wrapperHit: true, savedN: 8267);
        s.OnPromptEval(8242, tps: 961.6);
        // 命中：增量仅 prefill 32 token（增量小批 tps=12.64 应被忽略，用参考 961.6 折算）
        s.RecordRequest("k_roi", 0, wrapperHit: true, savedN: 13971);
        var r = s.OnPromptEval(32, tps: 12.64);
        Assert.True(r!.Hit);
        Assert.Equal("HitByDelta", r.Reason);
        var snap = s.PerfSnapshot();
        Assert.Equal(13971L, snap.ReuseTokens);
        Assert.Equal(13971 / 961.6 * 1000.0, snap.ReuseSavedMs, 1);
        // 再次命中累计（参考 tps 不变，仍用 961.6）
        s.RecordRequest("k_roi", 0, wrapperHit: true, savedN: 13971);
        s.OnPromptEval(32, tps: 13.0);
        var snap2 = s.PerfSnapshot();
        Assert.Equal(27942L, snap2.ReuseTokens);
        Assert.Equal(13971 / 961.6 * 1000.0 * 2, snap2.ReuseSavedMs, 1);
    }

    [Fact]
    public void Roi_HitWithoutRefTps_AccumulatesTokensOnly()
    {
        // v2.23.11：尚无全量 prefill 参考吞吐时，命中只累计复用 token，节省时间暂为 0（不猜默认值）
        var s = new RestoreStats(TempPath());
        s.RecordRequest("k", 0, wrapperHit: true, savedN: 13971);
        var r = s.OnPromptEval(32, tps: 12.64);
        Assert.True(r!.Hit);
        var snap = s.PerfSnapshot();
        Assert.Equal(13971L, snap.ReuseTokens);
        Assert.Equal(0.0, snap.ReuseSavedMs, 1);
    }

    [Fact]
    public void Roi_Miss_DoesNotAccumulate()
    {
        // v2.23.11：FullPrefill miss 不累计复用（saved_n 未真正复用）
        var s = new RestoreStats(TempPath());
        s.RecordRequest("k_miss", 0, wrapperHit: true, savedN: 8267);
        var r = s.OnPromptEval(8242, tps: 961.6);
        Assert.False(r!.Hit);
        var snap = s.PerfSnapshot();
        Assert.Equal(0L, snap.ReuseTokens);
        Assert.Equal(0.0, snap.ReuseSavedMs, 1);
    }
}
