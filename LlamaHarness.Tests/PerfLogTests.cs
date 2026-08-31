using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// PerfLog 单测（v2.21）：三类记录（system/cpp/timing）的落盘行格式验证。
/// 轮切（5MB×3）与并发写入不做单测——5MB 真实写太重且依赖 IO 时序，由真实运行验证（代码审查保证）。
/// 注意：写入目标为测试 bin 下 logs/perf.log（AppPaths.BaseDir），测试开头清理旧文件保证干净基线。
/// </summary>
public class PerfLogTests
{
    [Fact]
    public void Log_ThreeKinds_WritesParsableLines()
    {
        string path = AppPaths.PerfLog;
        try { if (File.Exists(path)) File.Delete(path); } catch { }

        PerfLog.Start();
        try
        {
            // system 行（1s 系统指标）
            PerfLog.LogSystem(new PerfPoint
            {
                Ts = DateTime.Now,
                CpuPercent = 12.3,
                MemUsedGb = 28.5,
                MemTotalGb = 64.0,
                VramUsedMb = 1234,
                VramTotalMb = 8192,
                Inflight = 2,
            });
            // cpp 行（5s llama.cpp 指标）
            PerfLog.LogCpp(new PerfPoint
            {
                Ts = DateTime.Now,
                PpTps = 0,
                TgTps = 65.2,
                TokensCached = 12345,
                CtxUsedPct = 0.18,
                SlotsProcessing = 1,
            });
            // timing 行（请求级时延）
            PerfLog.LogTiming(new RequestTiming
            {
                Ts = DateTime.Now,
                App = "trae_global",
                Path = "/v1/chat/completions",
                Success = true,
                WakeWaitMs = 0.5,
                GatewayMs = 8.2,
                BackendMs = 3200.1,
                TotalMs = 3208.8,
            });

            PerfLog.Stop(); // 先停写释放文件句柄，再读（写入器持有期不读）
            var lines = File.ReadAllLines(path);
            Assert.True(lines.Length >= 3, $"期望 ≥3 行，实际 {lines.Length}");
            // system 行
            Assert.StartsWith("system,", lines[^3]);
            Assert.Contains("cpu=12.3", lines[^3]);
            Assert.Contains("mem=28.5", lines[^3]);
            Assert.Contains("vram=1234", lines[^3]);
            Assert.Contains("inflight=2", lines[^3]);
            // cpp 行
            Assert.StartsWith("cpp,", lines[^2]);
            Assert.Contains("tg_tps=65.2", lines[^2]);
            Assert.Contains("tok=12345", lines[^2]);
            Assert.Contains("ctx=0.180", lines[^2]);
            // timing 行
            Assert.StartsWith("timing,", lines[^1]);
            Assert.Contains("app=trae_global", lines[^1]);
            Assert.Contains("path=/v1/chat/completions", lines[^1]);
            Assert.Contains("success=1", lines[^1]);
            Assert.Contains("backend=3200.1", lines[^1]);
        }
        finally
        {
            PerfLog.Stop();
        }
    }

    [Fact]
    public void Log_WhenNotStarted_IsNoOp()
    {
        // 未 Start 时写入被静默丢弃（不抛异常、不创建文件）
        PerfLog.Stop(); // 确保停止状态
        try { if (File.Exists(AppPaths.PerfLog)) File.Delete(AppPaths.PerfLog); } catch { } // 清理残留（可能来自其他测试）
        PerfLog.LogSystem(new PerfPoint { CpuPercent = 1.0 });
        Assert.False(File.Exists(AppPaths.PerfLog), "未 Start 不应创建 perf.log");
    }

    [Fact]
    public void Start_IsIdempotent()
    {
        PerfLog.Start();
        try
        {
            PerfLog.Start(); // 二次 Start 不重建、不抛
            PerfLog.LogSystem(new PerfPoint { CpuPercent = 2.0 });
        }
        finally
        {
            PerfLog.Stop();
        }
    }
}
