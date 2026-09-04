using System.Text.Json;
using System.Text.Json.Nodes;
using LlamaHarness;
using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// TokenGuard 裁剪逻辑单测（E-2 二分收敛）：
/// 用确定性假计数器（token = 文本长度/10）替代 HTTP tokenize，验证：
/// - 轮次裁剪二分收敛（tokenize 次数 ≤ log₂(轮数)+2，而非旧实现 K+1）
/// - 内容兜底减半收敛
/// - 失败降级语义不变
/// </summary>
public class TokenGuardTrimTests
{
    /// <summary>GuardAsync 参数校验（问题 16 修复）要求 backend 非空；本组测试注入假计数器，backend 不会被调用。</summary>
    private static readonly IBackendClient _backend = new NeverBackend();
    /// <summary>构造 root：system 前缀 + nTurns 个 user 轮（每轮 user+assistant，各 ~400 字符）。</summary>
    private static JsonObject BuildRoot(int nTurns)
    {
        var root = new JsonObject();
        var msgs = new JsonArray();
        root["messages"] = msgs;
        msgs.Add(new JsonObject { ["role"] = "system", ["content"] = new string('S', 500) });
        for (int t = 0; t < nTurns; t++)
        {
            msgs.Add(new JsonObject { ["role"] = "user", ["content"] = $"Q{t}" + new string('a', 400) });
            msgs.Add(new JsonObject { ["role"] = "assistant", ["content"] = new string('b', 400) });
        }
        return root;
    }

    [Fact]
    public async Task WithinBudget_NoTrim_OriginalBodyPreserved()
    {
        var body = @"{""messages"":[{""role"":""user"",""content"":""hi""}]}";
        int calls = 0;
        Func<string, Task<int?>> counter = async t => { calls++; return t.Length / 10; };

        var (ok, result, note) = await TokenGuard.GuardAsync(_backend, body, 10_000, counter);

        Assert.True(ok);
        Assert.Null(note);
        Assert.Equal(body, result); // 预算内：原样返回（不重新序列化）
        Assert.Equal(1, calls);     // 只 tokenize 一次
    }

    [Fact]
    public async Task TurnTrim_BinarySearchConverges_FewerTokenizes()
    {
        var root = BuildRoot(6); // 6 轮 → maxDelete=5；旧实现最坏 1+K 次，新实现 ≤ 1+1+log₂(5)+1
        int budget = 300;
        int calls = 0;
        Func<string, Task<int?>> counter = async t => { calls++; return t.Length / 10; };

        var (ok, modified, note) = await TokenGuard.GuardAsync(root, _backend, budget, counter);

        Assert.True(ok);
        Assert.True(modified);
        Assert.NotNull(note);
        // tokenize 次数：初始(1) + 极端验证(1) + 二分(≤3) ≤ 6（旧实现删 K 轮 = 1+K，K≥4 时更差）
        Assert.True(calls <= 6, $"tokenize 调用 {calls} 次，超出二分预期");
        // 最后一轮必须保留（末条消息仍是最后一个 assistant）
        var msgs = root["messages"]!.AsArray();
        Assert.Equal("assistant", msgs[^1]!.AsObject()["role"]!.GetValue<string>());
        Assert.Equal(new string('b', 400), msgs[^1]!.AsObject()["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task TurnTrim_FinalCountWithinBudget()
    {
        var root = BuildRoot(8); // 8 轮，大上下文
        int budget = 250;
        Func<string, Task<int?>> counter = async t => t.Length / 10;

        var (ok, _, _) = await TokenGuard.GuardAsync(root, _backend, budget, counter);

        Assert.True(ok);
        var msgs = root["messages"]!.AsArray();
        // 最终状态计数 ≤ 预算（用同一口径重算）
        int finalCount = 0;
        foreach (var m in msgs)
        {
            var o = m!.AsObject();
            finalCount += ($"{o["role"]}: {o["content"]}\n").Length;
        }
        Assert.True(finalCount / 10 <= budget, $"最终 {finalCount / 10} tokens 超预算 {budget}");
    }

    [Fact]
    public async Task TokenizeFailureMidTrim_DegradesToUnmodified()
    {
        var root = BuildRoot(4);
        int budget = 300;
        int calls = 0;
        // 第 2 次 tokenize 起失败（模拟后端忙）
        Func<string, Task<int?>> counter = async t =>
        {
            calls++;
            return calls == 1 ? t.Length / 10 : null;
        };

        var (ok, modified, note) = await TokenGuard.GuardAsync(root, _backend, budget, counter);

        Assert.True(ok);          // 降级不阻断
        Assert.False(modified);   // 未修改状态透传
        Assert.Null(note);
    }

    [Fact]
    public async Task ContentFallback_HalvingConverges()
    {
        // 单轮 + 巨型 content（10 万字符 ≈ 1 万 tokens）：轮次无可删 → 内容兜底
        var root = new JsonObject();
        var msgs = new JsonArray();
        root["messages"] = msgs;
        msgs.Add(new JsonObject { ["role"] = "user", ["content"] = new string('x', 100_000) });

        int budget = 6_500;
        int calls = 0;
        Func<string, Task<int?>> counter = async t => { calls++; return t.Length / 10; };

        var (ok, modified, _) = await TokenGuard.GuardAsync(root, _backend, budget, counter);

        Assert.True(ok);
        Assert.True(modified);
        // 收敛：≤ 初始(1) + 兜底(≤5) 次
        Assert.True(calls <= 6, $"tokenize 调用 {calls} 次，超出减半收敛预期");
        var content = msgs[0]!.AsObject()["content"]!.GetValue<string>();
        Assert.Contains("[已截断 - Token Guard]", content);
    }

    [Fact]
    public async Task NoUserMessages_PassThrough()
    {
        var root = new JsonObject();
        root["messages"] = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = new string('s', 10_000) },
        };
        int calls = 0;
        Func<string, Task<int?>> counter = async t => { calls++; return t.Length / 10; };

        var (ok, modified, note) = await TokenGuard.GuardAsync(root, _backend, 100, counter);

        Assert.True(ok);
        Assert.False(modified);
        Assert.Null(note);
        Assert.Equal(1, calls); // 只计数一次，无可裁
    }

    // ── 故障修复回归（v2.23）：tokenize 双路径回退 + fail-closed 兜底 ──
    [Fact]
    public async Task FailOpenFalse_TokenizeFailure_UsesCharEstimateToTrim()
    {
        // 400 自愈兜底场景：tokenize 持续失败（返回 null）+ failOpenOnTokenizeError=false
        // → 不得原样穿透死循环，退化为字符级保守估算继续裁剪
        var root = BuildRoot(6);
        int budget = 300;
        Func<string, Task<int?>> counter = async t => null; // 模拟 tokenize 端点 404/持续失败

        var (ok, modified, note) = await TokenGuard.GuardAsync(root, _backend, budget, counter, failOpenOnTokenizeError: false);

        Assert.True(ok);
        Assert.True(modified);          // 必须裁剪，禁止未裁剪穿透
        Assert.NotNull(note);
        var msgs = root["messages"]!.AsArray();
        Assert.Equal("assistant", msgs[^1]!.AsObject()["role"]!.GetValue<string>()); // 最后一轮仍保留
    }

    [Fact]
    public void EstimateTokensByChars_CjkAndAscii()
    {
        Assert.Equal(4, TokenGuard.EstimateTokensByChars("你好世界"));      // 4 CJK ≈ 4
        Assert.Equal(2, TokenGuard.EstimateTokensByChars("hello world"));  // 11 非CJK /4 = 2
        Assert.Equal(3, TokenGuard.EstimateTokensByChars("hello你好"));     // 5/4=1 + 2CJK = 3
        Assert.Equal(1, TokenGuard.EstimateTokensByChars("a"));             // 至少 1
        Assert.Equal(0, TokenGuard.EstimateTokensByChars(""));
    }
}
/// <summary>永不触发的 IBackendClient 替身：参数校验通过即可；任何成员被调用即失败（测试不应走到）。</summary>
internal sealed class NeverBackend : IBackendClient
{
    public void Dispose() { }
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct)
        => throw new NotImplementedException();
    public Task<HttpResponseMessage> ChatCompletionsAsync(string body, CancellationToken ct)
        => throw new NotImplementedException();
    public Task<int?> TokenizeAsync(string text, CancellationToken ct)
        => throw new NotImplementedException();
    public Task<HttpResponseMessage> SlotSaveAsync(int slot, string filename, CancellationToken ct)
        => throw new NotImplementedException();
    public Task<HttpResponseMessage> SlotRestoreAsync(int slot, string filename, CancellationToken ct)
        => throw new NotImplementedException();
    public Task<HttpResponseMessage> SlotEraseAsync(int slot, CancellationToken ct)
        => throw new NotImplementedException();
    public Task<JsonDocument?> GetSlotsAsync(CancellationToken ct)
        => throw new NotImplementedException();
    public Task<JsonDocument?> GetPropsAsync(CancellationToken ct)
        => throw new NotImplementedException();
    public Task<string?> GetMetricsAsync(CancellationToken ct)
        => throw new NotImplementedException();
    public Task<HttpResponseMessage> ProbeAsync(string path, CancellationToken ct)
        => throw new NotImplementedException();
}
