namespace LlamaHarness;

/// <summary>
/// 单请求网关时延记录（v2.21 事件模型，仅推理请求）：t_recv → t_ready(唤醒/排队完成) → t_sent(发向后端) → t_complete(响应完成)。
/// 四段时延：WakeWait(唤醒/排队等待) / Gateway(读体+网关预处理) / Backend(后端推理+SSE 转发) / Total(整请求)。
/// 与周期采样 <see cref="PerfPoint"/> 形态不同——事件驱动、请求级，独立记录并进 perf.log。
/// </summary>
public sealed class RequestTiming
{
    /// <summary>请求接收时间。</summary>
    public DateTime Ts { get; init; }
    /// <summary>亲和应用名（未知 = "?"）。</summary>
    public string App { get; init; } = "";
    /// <summary>请求路径。</summary>
    public string Path { get; init; } = "";
    /// <summary>是否成功完成（catch 到异常 = false；客户端断开被吞按 true 计，响应已尽力输出）。</summary>
    public bool Success { get; init; }
    /// <summary>唤醒/排队等待（t_recv → t_ready，含等待后端就绪）。</summary>
    public double WakeWaitMs { get; init; }
    /// <summary>网关预处理（t_ready → t_sent：读请求体 + 槽位路由/TokenGuard/强制流式等）。</summary>
    public double GatewayMs { get; init; }
    /// <summary>后端推理 + SSE 转发（t_sent → t_complete）。</summary>
    public double BackendMs { get; init; }
    /// <summary>整请求总时延（t_recv → t_complete）。</summary>
    public double TotalMs { get; init; }
}

/// <summary>请求时延会话聚合统计（供监控页/分析器读）。</summary>
public sealed class RequestTimingStats
{
    public long Completed { get; init; }
    public long Failed { get; init; }
    public double AvgTotalMs { get; init; }
    public double MaxTotalMs { get; init; }
    public double AvgBackendMs { get; init; }
    /// <summary>总请求数。</summary>
    public long Total => Completed + Failed;
}
