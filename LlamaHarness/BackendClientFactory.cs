// ─────────────────────────────────────────────────────────────────────────
// BackendClientFactory.cs —— 后端客户端工厂（v2.26 接口抽象 Step4 骨架）
// ─────────────────────────────────────────────────────────────────────────
// 作用：
//   · 集中创建 IBackendClient 实现，作为"后端选型"的唯一入口点。
//   · 当前唯一实现为 LlamaServerClient（llama.cpp server）；未来接入
//     vLLM / Ollama 等其他后端时，仅需在此按运行时端口/协议做分发，
//     所有调用方（SmartScheduler / TokenGuard / OutputContinuer /
//     KvCacheManager / LlamaCppMonitorCollector）均不感知后端类型差异。
// 设计说明：
//   · 静态工厂（无状态、无 DI 容器依赖），端口可变场景下由调用方每次
//     按 baseUrl 创建；HttpMessageHandler 可选注入用于测试（Mock 拦截）。
// ─────────────────────────────────────────────────────────────────────────
using System.Net.Http;

namespace LlamaHarness;

/// <summary>
/// 后端客户端工厂：按基础地址创建 <see cref="IBackendClient"/> 实现。
/// </summary>
public static class BackendClientFactory
{
    /// <summary>
    /// 创建后端客户端。默认（也是当前唯一）实现为 llama.cpp server 客户端。
    /// </summary>
    /// <param name="baseUrl">后端基础地址，例如 <c>http://127.0.0.1:8081</c>（尾斜杠容错）。</param>
    /// <param name="handler">可选 HttpMessageHandler；测试时可注入 Mock 拦截，生产默认 SocketsHttpHandler 连接池。</param>
    public static IBackendClient Create(string baseUrl, HttpMessageHandler? handler = null)
        => new LlamaServerClient(baseUrl, handler);
}
