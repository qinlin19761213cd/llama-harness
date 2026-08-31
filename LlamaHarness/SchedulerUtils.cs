namespace LlamaHarness;

/// <summary>
/// 调度器通用纯静态工具（零实例依赖）：空闲端口探测 / 预热安全槽位选择。
/// 原属 SmartScheduler（v2.15 重构迁出），方法体逐字迁移，行为等价。
/// </summary>
public static class SchedulerUtils
{
    /// <summary>从 preferred 开始向上扫描，返回第一个可绑定的空闲端口（规避 Hyper-V/WSL2 动态端口保留）。
    /// 注意：探测与 llama-server 实际绑定之间存在极小的 TOCTOU 窗口；若该窗口内端口被抢占，
    /// llama-server 绑定失败会自行退出，WaitReadyAsync 检测到进程退出并上报失败，下次唤醒重新探测——本机单用户场景可接受。</summary>
    public static int PickFreePort(int preferred)
    {
        var upper = Math.Min(preferred + 32, 65535);
        for (int p = preferred; p <= upper; p++)
        {
            try
            {
                var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, p);
                l.Start();
                l.Stop();
                return p;
            }
            catch
            {
                // 端口被占用/保留，继续上探
            }
        }
        throw new InvalidOperationException($"在 {preferred}–{upper} 范围内未找到可用后端端口。");
    }

    /// <summary>3.2：选 dummy 预热的安全槽位——第一个未绑定 KV 快照的槽位；全绑定时返回 -1（跳过预热，防污染已恢复 KV）。public 供测试。</summary>
    public static int PickWarmSlot(int parallel, IEnumerable<int> kvBoundSlots)
    {
        var bound = new HashSet<int>(kvBoundSlots);
        return Enumerable.Range(0, Math.Max(parallel, 0)).FirstOrDefault(s => !bound.Contains(s), -1);
    }
}
