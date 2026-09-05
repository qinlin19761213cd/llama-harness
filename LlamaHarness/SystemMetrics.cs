using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LlamaHarness;

/// <summary>
/// 系统资源采样（只读，无副作用）：
/// - CPU 占用：GetSystemTimes 两次采样取差值（kernel 时间含 idle，busy = (kernel-idle) + user）
/// - 内存：GlobalMemoryStatusEx 取物理内存已用/总量
/// - 显存：nvidia-smi 查询（NVIDIA 卡；找不到工具时返回 null，UI 显示 "—"）
/// </summary>
public sealed class SystemMetrics
{
    private const double BytesPerGb = 1073741824.0;
    private const int ExitProbeTimeoutMs = 5000;
    private const int NvidiaProbeTimeoutMs = 3000;

    // ── CPU 采样状态（lock 保护四字段一致性） ──
    private readonly object _cpuGate = new();
    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _hasSample;
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct FILETIME { public uint lo; public uint hi; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedCommit;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [DllImport("kernel32.dll")]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX pbs);
    }

    /// <summary>nvidia-smi 路径（Lazy：首次使用解析一次，之后全项目共享；找不到为 null 恒定）。</summary>
    private static readonly Lazy<string?> NvSmiPath = new(ResolveNvidiaSmi);

    /// <summary>整机 CPU 占用百分比（基于上次调用的差值）。
    /// [P0 修复] 四字段读写全部加 lock (_cpuGate)，防止多线程并发采样时半更新状态导致 CPU 百分比失真。</summary>
    public double GetCpuPercent()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        ulong i = ToU64(idle), k = ToU64(kernel), u = ToU64(user);
        double pct = 0;
        lock (_cpuGate)
        {
            if (_hasSample)
            {
                ulong idleDelta = i - _prevIdle;
                ulong kernelDelta = k - _prevKernel; // kernel 时间包含 idle
                ulong userDelta = u - _prevUser;
                ulong busy = (kernelDelta > idleDelta ? kernelDelta - idleDelta : 0) + userDelta;
                ulong total = busy + idleDelta;
                pct = total > 0 ? 100.0 * busy / total : 0;
            }
            _prevIdle = i; _prevKernel = k; _prevUser = u; _hasSample = true;
        }
        return pct;
    }

    /// <summary>物理内存（已用 GB, 总量 GB）。</summary>
    public (double usedGb, double totalGb) GetMemory()
    {
        var ms = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref ms)) return (0, 0);
        const double gb = BytesPerGb; // 1024^3
        return ((ms.ullTotalPhys - ms.ullAvailPhys) / gb, ms.ullTotalPhys / gb);
    }

    /// <summary>GPU 显存（"已用MB/总量MB"）；无 nvidia-smi 或查询失败/挂起返回 null。</summary>
    public async Task<string?> GetVramTextAsync()
    {
        var line = await RunNvidiaSmiAsync("--query-gpu=memory.used,memory.total --format=csv,noheader,nounits");
        if (string.IsNullOrWhiteSpace(line)) return null;
        var parts = line.Split(',', 2);
        if (parts.Length < 2) return null;
        return $"{parts[0].Trim()}/{parts[1].Trim()} MB";
    }

    /// <summary>C-006：查询 GPU 已用显存（MB），供休眠后校验显存是否释放；nvidia-smi 不可用/失败返回 null。</summary>
    public static async Task<int?> GetVramUsedMbAsync()
    {
        var line = await RunNvidiaSmiAsync("--query-gpu=memory.used --format=csv,noheader,nounits");
        var v = line?.Trim().Split(',')[0]?.Trim();
        return int.TryParse(v, out var mb) ? mb : null;
    }

    /// <summary>共享 nvidia-smi 查询：3 秒超时执行并取输出首行；路径缺失/失败/挂起返回 null。</summary>
    private static async Task<string?> RunNvidiaSmiAsync(string args)
    {
        var path = NvSmiPath.Value;
        if (path == null) return null;
        try
        {
            var psi = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                Arguments = args,
            };
            using var p = Process.Start(psi)!;
            // nvidia-smi 可能挂起（驱动忙）：3 秒超时放弃本轮并杀进程，防线程堆积
            var lineTask = p.StandardOutput.ReadLineAsync();
            var finished = await Task.WhenAny(lineTask, Task.Delay(NvidiaProbeTimeoutMs));
            if (finished != lineTask)
            {
                try { p.Kill(); } catch { }
                // C-004：Kill 后必须 WaitForExit 回收进程对象，防长期运行句柄缓慢泄漏
                p.WaitForExit(NvidiaProbeTimeoutMs);
                return null;
            }
            string? line = await lineTask;
            p.WaitForExit(ExitProbeTimeoutMs);
            return line;
        }
        catch
        {
            return null; // nvidia-smi 异常（驱动未就绪等），UI 显示 "—"
        }
    }

    /// <summary>在 PATH 和系统目录中查找 nvidia-smi.exe。</summary>
    private static string? ResolveNvidiaSmi()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var c = Path.Combine(dir.Trim(), "nvidia-smi.exe");
                if (File.Exists(c)) return c;
            }
            catch
            {
                // 非法路径跳过
            }
        }
        var sys = @"C:\Windows\System32\nvidia-smi.exe";
        return File.Exists(sys) ? sys : null;
    }

    private static ulong ToU64(Native.FILETIME ft) => ((ulong)ft.hi << 32) | ft.lo;
}
