using System.Diagnostics;

namespace LlamaHarness;

/// <summary>
/// llama-server 进程封装：后台静默运行（无黑框），逐行输出事件，退出码事件。
/// </summary>
public sealed class LlamaServerProcess : IDisposable
{
    private Process? _proc;

    /// <summary>当前是否还有存活的进程。</summary>
    public bool IsRunning => _proc is { HasExited: false };

    /// <summary>当前 Process 对象（未启动或已清理时为 null），供外部设置亲和性等。</summary>
    public Process? Current => _proc;

    /// <summary>输出一行日志（stdout/stderr），可能来自非 UI 线程。</summary>
    public event Action<string>? OutputLine;

    /// <summary>进程退出，参数为退出码，可能来自非 UI 线程。</summary>
    public event EventHandler<int>? Exited;

    /// <summary>启动 llama-server。要求当前无存活进程。</summary>
    public void Start(string exePath, string args, string workingDir)
    {
        if (IsRunning)
            throw new InvalidOperationException("已有进程在运行。");

        // 清理上一次的 Process 对象
        _proc?.Dispose();

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            UseShellExecute = false,       // 必须：允许重定向输出
            CreateNoWindow = true,         // 后台静默，杜绝黑框弹窗
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDir,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        _proc = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true,
        };
        _proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) OutputLine?.Invoke(e.Data);
        };
        _proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) OutputLine?.Invoke(e.Data);
        };
        // 局部捕获当前进程对象：极端时序下（快速重启）事件晚于字段替换触发，
        // 读 _proc 字段可能拿到新进程导致退出码错配（审计加固）
        var proc = _proc;
        proc.Exited += (_, _) =>
        {
            int code = 0;
            try { code = proc.ExitCode; } catch { /* 极端情况下取不到 */ }
            Exited?.Invoke(this, code);
        };

        _proc.Start();
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();
    }

    /// <summary>停止：终止整个进程树（含派生子进程）。已退出则忽略。</summary>
    public void Stop()
    {
        var p = _proc;
        if (p is null) return;
        try
        {
            if (p.HasExited) return; // HasExited 检查移入 try：_proc 已 Dispose 时不再抛 ObjectDisposedException
            p.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程可能刚好自行退出，或对象已释放，忽略
        }
        finally
        {
            _proc?.Dispose();
            _proc = null;
        }
    }

    public void Dispose()
    {
        // B-03 修复：先 Stop 终止进程树，避免 Dispose 时进程仍存活导致孤儿进程；Stop 内部已 Kill + Dispose + 置 null
        try { Stop(); } catch { }
        // [P2-L22] Stop() finally 已执行 _proc?.Dispose() + _proc = null；此处为防御性兜底，确保 Stop 抛异常时仍能清理
        // 实际路径：Stop 成功时 _proc 已为 null，此行为 no-op
        if (_proc != null) { try { _proc.Dispose(); } catch { _proc = null; } }
    }
}
