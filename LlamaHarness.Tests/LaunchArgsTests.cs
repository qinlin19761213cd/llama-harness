using LlamaHarness;
using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// llama-server 启动参数拼接单测（1.2 参数结构化）：
/// - 默认配置包含全部 Prefill 吞吐参数（load-mode/ubatch/batch/cache-type-k/v/flash-attn/spec-type/spec-draft-n-max/cache-req）
/// - 条件分支：BatchThreads=0 不拼 --tb；CacheReq=false 不拼 --cache-req；SpecType 留空不拼投机解码参数
/// - kv-unified：NoKvUnified=false 时不拼 --no-kv-unified
/// </summary>
public class LaunchArgsTests
{
    private static AppConfig DefaultCfg() => new()
    {
        ModelPath = @"G:\models\test.gguf",
        Port = 8081,
        KvCachePath = "g:/temp",
    };

    [Fact]
    public void DefaultConfig_ContainsAllPrefillParams()
    {
        var args = LlamaFinder.BuildArgs(DefaultCfg());
        Assert.Contains("--load-mode mlock", args);
        Assert.Contains("--ubatch-size 2048", args);
        Assert.Contains("--batch-size 8192", args);
        Assert.Contains("--cache-type-k q4_0 --cache-type-v q4_0", args);
        Assert.Contains("--flash-attn on", args);
        Assert.Contains("--spec-type draft-mtp", args);
        Assert.Contains("--spec-draft-n-max 2", args);
        // 默认 BatchThreads=0：不拼 --tb
        Assert.DoesNotContain("--tb", args);
        // 默认 NoKvUnified=false：kv-unified 开启，不拼 --no-kv-unified
        Assert.DoesNotContain("--no-kv-unified", args);
    }

    [Fact]
    public void BatchThreads_Positive_AppendsTb()
    {
        var cfg = DefaultCfg();
        cfg.BatchThreads = 12;
        var args = LlamaFinder.BuildArgs(cfg);
        Assert.Contains("--tb 12", args);
    }

    [Fact]
    public void SpecType_Empty_OmitsSpecParams()
    {
        var cfg = DefaultCfg();
        cfg.SpecType = "";
        var args = LlamaFinder.BuildArgs(cfg);
        Assert.DoesNotContain("--spec-type", args);
        Assert.DoesNotContain("--spec-draft-n-max", args);
    }

    [Fact]
    public void NoKvUnified_True_AppendsNoKvUnified()
    {
        var cfg = DefaultCfg();
        cfg.NoKvUnified = true;
        var args = LlamaFinder.BuildArgs(cfg);
        Assert.Contains("--no-kv-unified", args);
    }

    [Fact]
    public void Q8_Kv_CacheType_SwitchesBothKAndV()
    {
        var cfg = DefaultCfg();
        cfg.CacheTypeKv = "q8_0";
        var args = LlamaFinder.BuildArgs(cfg);
        Assert.Contains("--cache-type-k q8_0 --cache-type-v q8_0", args);
    }

    [Fact]
    public void ExtraArgs_AppendedLast()
    {
        var cfg = DefaultCfg();
        // B1/B2 安全收紧：ExtraArgs 白名单剔除 `"`（防引号边界注入）——已知权衡：含空格路径无法再用引号包裹
        cfg.ExtraArgs = "--mmproj \"D:\\a b\\projector.gguf\"";
        var args = LlamaFinder.BuildArgs(cfg);
        // 白名单过滤后拼在尾部（引号被剔除）
        Assert.EndsWith("--mmproj D:\\a b\\projector.gguf", args);
        Assert.DoesNotContain("\"", args);
    }
}
