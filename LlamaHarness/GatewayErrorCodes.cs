namespace LlamaHarness;

/// <summary>
/// 网关错误码常量（B8/P1-6 审计修复）：统一错误响应格式 {"error":{"code":"...","message":"..."}} 的 code 字符串集中定义，
/// 消除跨文件魔法值拼写漂移；新增错误码在此追加，禁止在调用点手写字符串。
/// </summary>
public static class GatewayErrorCodes
{
    /// <summary>上下文超限（Token Guard 拒绝 / 400 自愈兜底）。</summary>
    public const string ContextOverflow = "CONTEXT_OVERFLOW";
    /// <summary>服务休眠释放中（唤醒竞态）。</summary>
    public const string ServiceSleeping = "SERVICE_SLEEPING";
    /// <summary>服务待机/未运行。</summary>
    public const string ServiceNotRunning = "SERVICE_NOT_RUNNING";
    /// <summary>请求体编码非 UTF-8。</summary>
    public const string UnsupportedMediaType = "UNSUPPORTED_MEDIA_TYPE";
    /// <summary>请求体超限。</summary>
    public const string RequestBodyTooLarge = "REQUEST_BODY_TOO_LARGE";
    /// <summary>请求处理超时。</summary>
    public const string RequestTimeout = "REQUEST_TIMEOUT";
    /// <summary>调度器停止中。</summary>
    public const string ServiceUnavailable = "SERVICE_UNAVAILABLE";
    /// <summary>后端未就绪。</summary>
    public const string BackendNotReady = "BACKEND_NOT_READY";
    /// <summary>请求路径非法 / 非本机目标。</summary>
    public const string InvalidRequest = "INVALID_REQUEST";
    /// <summary>400 自愈重发连接失败。</summary>
    public const string SelfHealReconnectFailed = "SELF_HEAL_RECONNECT_FAILED";
}
