using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LlamaHarness;
using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// IBackendClient / LlamaServerClient 契约测试（v2.26 Step 1）：MockHttpMessageHandler 注入，无需真实 llama-server。
/// 覆盖：推理透明代理透传（SendAsync）、非流式 chat/completions（计量/预热）、KV 槽位（save/restore/erase）、
/// 状态探测（/slots /props /metrics 判空降级）、通用探测（ProbeAsync）。
/// </summary>
public class BackendClientTests
{
    private sealed class MockHandler : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Url, string? Body, string? Accept)> Requests = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Responder =
            _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string? body = null;
            if (request.Content != null)
                body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Requests.Add((request.Method, request.RequestUri!.ToString(), body,
                request.Headers.Accept?.ToString()));
            return Task.FromResult(Responder(request));
        }
    }

    private static (LlamaServerClient client, MockHandler handler) Make(string baseUrl = "http://localhost:8081")
    {
        var h = new MockHandler();
        return (new LlamaServerClient(baseUrl, h), h);
    }

    // ── ① 推理透明代理（SendAsync）──────────────────────────
    [Fact]
    public async Task SendAsync_透传URL方法Body头_流式直通()
    {
        var (client, h) = Make();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            new Uri("http://localhost:8081/v1/chat/completions?stream=true"))
        {
            Content = new StringContent("{\"messages\":[]}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await client.SendAsync(req, CancellationToken.None);

        var r = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal("http://localhost:8081/v1/chat/completions?stream=true", r.Url);
        Assert.Equal("{\"messages\":[]}", r.Body);
        Assert.Contains("text/event-stream", r.Accept);
    }

    [Fact]
    public async Task SendAsync_非流式_无eventStreamAccept()
    {
        var (client, h) = Make();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            new Uri("http://localhost:8081/v1/chat/completions"))
        { Content = new StringContent("{}", Encoding.UTF8, "application/json") };

        await client.SendAsync(req, CancellationToken.None);

        var r = Assert.Single(h.Requests);
        Assert.DoesNotContain("text/event-stream", r.Accept);
    }

    // ── ② 非流式 chat/completions（计量/预热）───────────────
    [Fact]
    public async Task ChatCompletionsAsync_POST正确端点与ContentType()
    {
        var (client, h) = Make();
        using var resp = await client.ChatCompletionsAsync("{\"max_tokens\":0}", CancellationToken.None);

        var r = Assert.Single(h.Requests);
        Assert.Equal("http://localhost:8081/v1/chat/completions", r.Url);
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal("{\"max_tokens\":0}", r.Body);
    }

    // ── ③ KV 槽位 ──────────────────────────────────────────
    [Theory]
    [InlineData("save", "dsh_rule_abc", "http://localhost:8081/slots/0?action=save")]
    [InlineData("restore", "webui_xyz", "http://localhost:8081/slots/3?action=restore")]
    [InlineData("erase", null, "http://localhost:8081/slots/5?action=erase")]
    public async Task Slot动作_URL与body正确(string action, string? key, string expectUrl)
    {
        var (client, h) = Make();
        bool ok = action switch
        {
            "save" => await client.SlotSaveAsync(0, key!, CancellationToken.None),
            "restore" => await client.SlotRestoreAsync(3, key!, CancellationToken.None),
            _ => await client.SlotEraseAsync(5, CancellationToken.None),
        };

        Assert.True(ok);
        var r = Assert.Single(h.Requests);
        Assert.Equal(expectUrl, r.Url);
        if (key != null)
        {
            using var doc = JsonDocument.Parse(r.Body!);
            Assert.Equal(key, doc.RootElement.GetProperty("key").GetString());
        }
        else
        {
            Assert.Null(r.Body); // erase 无 body
        }
    }

    [Fact]
    public async Task SlotSave_后端500_返回false()
    {
        var (client, h) = Make();
        h.Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var ok = await client.SlotSaveAsync(0, "k", CancellationToken.None);
        Assert.False(ok);
    }

    // ── ④ 状态探测（判空降级）───────────────────────────────
    [Fact]
    public async Task GetSlots_200_解析Json()
    {
        var (client, h) = Make();
        h.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"slots\":[{\"id\":0}]}", Encoding.UTF8, "application/json") };

        using var doc = await client.GetSlotsAsync(CancellationToken.None);
        Assert.NotNull(doc);
        Assert.Equal("http://localhost:8081/slots", Assert.Single(h.Requests).Url);
    }

    [Fact]
    public async Task GetSlots_404_返回null()
    {
        var (client, h) = Make();
        h.Responder = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        using var doc = await client.GetSlotsAsync(CancellationToken.None);
        Assert.Null(doc);
    }

    [Fact]
    public async Task GetProps_200_解析Json()
    {
        var (client, h) = Make();
        h.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"total_slots\":2}", Encoding.UTF8, "application/json") };

        using var doc = await client.GetPropsAsync(CancellationToken.None);
        Assert.NotNull(doc);
        Assert.Equal("http://localhost:8081/props", Assert.Single(h.Requests).Url);
    }

    [Fact]
    public async Task GetMetrics_200_返回文本()
    {
        var (client, h) = Make();
        h.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("llm_prompt_tokens 100\n", Encoding.UTF8, "text/plain") };

        var text = await client.GetMetricsAsync(CancellationToken.None);
        Assert.Equal("llm_prompt_tokens 100\n", text);
        Assert.Equal("http://localhost:8081/metrics", Assert.Single(h.Requests).Url);
    }

    [Fact]
    public async Task GetMetrics_500_返回null()
    {
        var (client, h) = Make();
        h.Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        Assert.Null(await client.GetMetricsAsync(CancellationToken.None));
    }

    // ── ⑤ 通用探测 ─────────────────────────────────────────
    [Theory]
    [InlineData("/v1/models")]
    [InlineData("v1/models")] // 无前导斜杠也应正常
    public async Task ProbeAsync_GET任意路径(string path)
    {
        var (client, h) = Make();
        using var resp = await client.ProbeAsync(path, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("http://localhost:8081/v1/models", Assert.Single(h.Requests).Url);
    }

    // ── ⑥ BaseUrl 尾斜杠容错 ───────────────────────────────
    [Fact]
    public async Task BaseUrl_尾斜杠_不产生双斜杠()
    {
        var (client, h) = Make("http://localhost:8081/");
        using var doc = await client.GetPropsAsync(CancellationToken.None);
        Assert.Equal("http://localhost:8081/props", Assert.Single(h.Requests).Url);
    }
}
