using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// AppConfig.Sanitize 数值兜底统一入口测试（v2.17.2）：
/// Load() 与配置导入（MainFormPresenter.OnImportConfigClicked）共用同一套规则，集中维护防漂移。
/// 覆盖 17 条兜底规则 + 合法值幂等 + 关键边界（SpecDraftNMax / BatchThreads / MaxAutoRestarts 的 0 保留语义、
/// Port 上界 65534、ContinuationTimeout/MaxContinuations 的保留下界）。
/// </summary>
public class AppConfigSanitizeTests
{
    [Fact]
    public void Port_OutOfRange_FallsBack()
    {
        var c = new AppConfig { Port = 0 };
        AppConfig.Sanitize(c);
        Assert.Equal(8080, c.Port);
        c.Port = 65535;
        AppConfig.Sanitize(c);
        Assert.Equal(8080, c.Port); // 65535 与智能模式后端端口(Port+1)冲突，必须兜底
        c.Port = 65534;
        AppConfig.Sanitize(c);
        Assert.Equal(65534, c.Port); // 合法上界保留
    }

    [Fact]
    public void CtxSize_NonPositive_FallsBack()
    {
        var c = new AppConfig { CtxSize = 0 };
        AppConfig.Sanitize(c);
        Assert.Equal(65536, c.CtxSize); // 与 Load 黄金默认一致（导入路径不再漂移为 262144）
    }

    [Fact]
    public void Ngl_Negative_FallsBack()
    {
        var c = new AppConfig { Ngl = -1 };
        AppConfig.Sanitize(c);
        Assert.Equal(999, c.Ngl);
    }

    [Fact]
    public void Parallel_NonPositive_FallsBack()
    {
        var c = new AppConfig { Parallel = 0 };
        AppConfig.Sanitize(c);
        Assert.Equal(1, c.Parallel);
    }

    [Fact]
    public void Threads_NonPositive_FallsBackToProcessorCount()
    {
        var c = new AppConfig { Threads = 0 };
        AppConfig.Sanitize(c);
        Assert.Equal(Environment.ProcessorCount, c.Threads);
    }

    [Fact]
    public void UbatchAndBatch_NonPositive_FallsBack()
    {
        var c = new AppConfig { UbatchSize = 0, BatchSize = 0 };
        AppConfig.Sanitize(c);
        Assert.Equal(2048, c.UbatchSize);
        Assert.Equal(8192, c.BatchSize);
    }

    [Fact]
    public void SpecDraftNMax_Negative_FallsBackButZeroKept()
    {
        var c = new AppConfig { SpecDraftNMax = -1 };
        AppConfig.Sanitize(c);
        Assert.Equal(2, c.SpecDraftNMax);
        c.SpecDraftNMax = 0;
        AppConfig.Sanitize(c);
        Assert.Equal(0, c.SpecDraftNMax); // 0 = 用户显式禁用，不兜底
    }

    [Fact]
    public void BatchThreads_Negative_FallsBackButZeroKept()
    {
        var c = new AppConfig { BatchThreads = -1 };
        AppConfig.Sanitize(c);
        Assert.Equal(0, c.BatchThreads);
        c.BatchThreads = 0;
        AppConfig.Sanitize(c);
        Assert.Equal(0, c.BatchThreads); // 0 = 不拼 --tb，保留
    }

    [Fact]
    public void IdleMinutes_NonPositive_FallsBack()
    {
        var c = new AppConfig { IdleMinutes = 0 };
        AppConfig.Sanitize(c);
        Assert.Equal(15, c.IdleMinutes);
    }

    [Fact]
    public void ReservedOutputTokens_NonPositive_FallsBack()
    {
        var c = new AppConfig { ReservedOutputTokens = 0 };
        AppConfig.Sanitize(c);
        Assert.Equal(8192, c.ReservedOutputTokens);
    }

    [Fact]
    public void ReservedPromptOverhead_Negative_FallsBack()
    {
        var c = new AppConfig { ReservedPromptOverhead = -1 };
        AppConfig.Sanitize(c);
        Assert.Equal(10240, c.ReservedPromptOverhead);
    }

    [Fact]
    public void CacheRamMiB_Negative_FallsBack()
    {
        var c = new AppConfig { CacheRamMiB = -100 };
        AppConfig.Sanitize(c);
        Assert.Equal(0, c.CacheRamMiB);
    }

    [Fact]
    public void MaxContinuations_TooLow_FallsBack()
    {
        var c = new AppConfig { MaxContinuations = 0 };
        AppConfig.Sanitize(c);
        Assert.Equal(10, c.MaxContinuations);
        c.MaxContinuations = 1;
        AppConfig.Sanitize(c);
        Assert.Equal(1, c.MaxContinuations); // 下界保留
    }

    [Fact]
    public void ContinuationTimeout_TooLow_FallsBack()
    {
        var c = new AppConfig { ContinuationTimeoutSeconds = 29 };
        AppConfig.Sanitize(c);
        Assert.Equal(300, c.ContinuationTimeoutSeconds);
        c.ContinuationTimeoutSeconds = 30;
        AppConfig.Sanitize(c);
        Assert.Equal(30, c.ContinuationTimeoutSeconds); // 下界保留
    }

    [Fact]
    public void MaxAutoRestarts_Negative_FallsBackButZeroKept()
    {
        var c = new AppConfig { MaxAutoRestarts = -1 };
        AppConfig.Sanitize(c);
        Assert.Equal(2, c.MaxAutoRestarts);
        c.MaxAutoRestarts = 0;
        AppConfig.Sanitize(c);
        Assert.Equal(0, c.MaxAutoRestarts); // 0 = 禁用进程死亡分支的自动重启，保留
    }

    [Fact]
    public void RecoveryKeepAlive_TooLow_FallsBack()
    {
        var c = new AppConfig { RecoveryKeepAliveIntervalSeconds = 0 };
        AppConfig.Sanitize(c);
        Assert.Equal(5, c.RecoveryKeepAliveIntervalSeconds);
        c.RecoveryKeepAliveIntervalSeconds = 1;
        AppConfig.Sanitize(c);
        Assert.Equal(1, c.RecoveryKeepAliveIntervalSeconds); // 下界保留
    }

    [Fact]
    public void ValidValues_Unchanged_Idempotent()
    {
        var c = new AppConfig
        {
            Port = 8080, CtxSize = 131072, Ngl = 100, Parallel = 2, Threads = 8,
            UbatchSize = 1024, BatchSize = 4096, SpecDraftNMax = 1, BatchThreads = 2,
            IdleMinutes = 30, ReservedOutputTokens = 4096, ReservedPromptOverhead = 2048,
            CacheRamMiB = 4096, MaxContinuations = 5, ContinuationTimeoutSeconds = 120,
            MaxAutoRestarts = 3, RecoveryKeepAliveIntervalSeconds = 10,
        };
        AppConfig.Sanitize(c);
        Assert.Equal(8080, c.Port);
        Assert.Equal(131072, c.CtxSize);
        Assert.Equal(100, c.Ngl);
        Assert.Equal(2, c.Parallel);
        Assert.Equal(8, c.Threads);
        Assert.Equal(1024, c.UbatchSize);
        Assert.Equal(4096, c.BatchSize);
        Assert.Equal(1, c.SpecDraftNMax);
        Assert.Equal(2, c.BatchThreads);
        Assert.Equal(30, c.IdleMinutes);
        Assert.Equal(4096, c.ReservedOutputTokens);
        Assert.Equal(2048, c.ReservedPromptOverhead);
        Assert.Equal(4096, c.CacheRamMiB);
        Assert.Equal(5, c.MaxContinuations);
        Assert.Equal(120, c.ContinuationTimeoutSeconds);
        Assert.Equal(3, c.MaxAutoRestarts);
        Assert.Equal(10, c.RecoveryKeepAliveIntervalSeconds);
    }
}
