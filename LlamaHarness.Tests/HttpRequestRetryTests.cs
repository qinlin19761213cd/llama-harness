using System.Net;
using System.Net.Http;
using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// 转发重试根因回归（v2.23.5，见 SmartScheduler.Pipeline.cs TryConnectWithRetryAsync）：
/// HttpRequestMessage 发送后其 Content 流即被消费，复用同一 msg 二次 SendAsync 会抛
/// InvalidOperationException "The request message was already sent..."——连接异常重试必须
/// 经工厂重建请求（bodyBytes 字节流可重复读取）。本测试锁定该 HttpClient 语义，防回归。
/// </summary>
public class HttpRequestRetryTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _next;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> next) => _next = next;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_next(request));
    }

    [Fact]
    public async Task ReusingSentRequestMessage_ThrowsAlreadySent()
    {
        using var hc = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        using var msg = new HttpRequestMessage(HttpMethod.Post, "http://localhost:1/v1/chat/completions")
        {
            Content = new StringContent("{\"messages\":[]}"),
        };

        using var r1 = await hc.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);

        // 复用同一 msg 二次发送：Content 已消费 → InvalidOperationException（修复前重试路径的真实报错）
        await Assert.ThrowsAsync<InvalidOperationException>(() => hc.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead));
    }

    [Fact]
    public async Task RebuildViaFactory_AllowsRetryAfterTransientFailure()
    {
        int attempts = 0;
        using var hc = new HttpClient(new StubHandler(_ =>
        {
            attempts++;
            if (attempts == 1)
                throw new HttpRequestException("连接被重置（模拟）");
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        // 模拟 TryConnectWithRetryAsync 的工厂重建模式：捕获 HttpRequestException → 重建请求 → 重发成功
        HttpResponseMessage resp;
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, "http://localhost:1/v1/chat/completions")
            { Content = new StringContent("hi") };
            resp = await hc.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (HttpRequestException)
        {
            using var retryMsg = new HttpRequestMessage(HttpMethod.Post, "http://localhost:1/v1/chat/completions")
            { Content = new StringContent("hi") };
            resp = await hc.SendAsync(retryMsg, HttpCompletionOption.ResponseHeadersRead);
        }

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, attempts);
        resp.Dispose();
    }
}
