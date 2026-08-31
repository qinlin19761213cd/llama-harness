using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ThinkingModeHelper = LlamaHarness.ThinkingMode;

namespace LlamaHarness;

/// <summary>
/// 崩溃恢复协调（RunCrashRecoveryAsync/RestartAndReplayAsync/ProbeClientConnectedAsync/RunKeepAliveAsync/TryCrashRecoverAsync）。partial 聚类方法体零改动。
/// </summary>
public partial class SmartScheduler
{
    /// <summary>
    /// bad_alloc 崩溃自动恢复管道（三分支）：
    /// - 分支 A（服务端存活 + 客户端连接可持有）：抢 save 槽位 KV 快照 → SSE keep-alive 保活客户端
    ///   → 内存余量检查 → 快照接续（restore + 回填已生成部分 + 续接指令）或全量重放（严格预算）→ 输出灌入同一条流（客户端无感）
    /// - 分支 B（进程死亡）：重启至多 MaxAutoRestarts 次并等就绪 → 严格预算全量重放（无快照）
    /// - 分支 C（客户端已断开）：不重放；agent 侧重试走现有 KV restore 路径
    /// 熔断器：10 分钟窗口内 ≥3 次确认崩溃 → 停止自动恢复，醒目报错，等待人工介入。
    /// </summary>
    private async Task TryCrashRecoverAsync(
        Uri uri, HttpListenerResponse outResp, string finalBody, string accumulated,
        int? routedSlot, string? routedKey, Action<string>? log)
    {
        // ── 诊断增强：崩溃瞬间记录系统资源（判定主机 RAM 还是显存打满 → 长期方案：降 ctx / 换 mmap / 加内存）──
        var m = new SystemMetrics();
        var (usedGb, totalGb) = m.GetMemory();
        double freeGb = totalGb - usedGb;
        int? vramUsedMb = await SystemMetrics.GetVramUsedMbAsync();
        log?.Invoke($"崩溃恢复触发。崩溃时刻诊断：空闲 RAM {freeGb:F1}/{totalGb:F1} GB，显存 {(vramUsedMb is int v ? $"{v} MB" : "未知")}");

        // ── 熔断器：10 分钟窗口内 ≥3 次确认崩溃 → 停止自动恢复（需人工介入）──
        CrashRecovery.RecordCrash();
        if (!CrashRecovery.AllowRecover())
        {
            log?.Invoke($"熔断器已跳闸：10 分钟内 {CrashRecovery.ConfirmedCount} 次崩溃 ≥ {CrashRecovery.MaxCrashesInWindow}，停止自动恢复。请加内存 / 降上下文后手动重试。");
            RaiseStatus("⚠ 崩溃熔断：自动恢复已停止，需人工介入");
            return;
        }

        // 分支 C（客户端已断开）由各分支内的探测写判定：立即写一行 keep-alive，写失败 = 客户端已断开 → 不重放。

        if (_server.IsRunning)
            await RecoverAliveAsync(uri, outResp, finalBody, accumulated, routedSlot, routedKey, freeGb, log);
        else
            await RestartAndReplayAsync(uri, outResp, finalBody, log);
    }

    /// <summary>分支 A：服务端存活 + 客户端连接可持有 → 抢 save 快照（抢在 release 前）→ 内存余量检查 → 快照接续或全量重放。
    /// keep-alive 保活 / 分支 C 探测 / 异常兜底由 RunCrashRecoveryAsync 公共骨架提供（审计 O-10）。</summary>
    private Task RecoverAliveAsync(
        Uri uri, HttpListenerResponse outResp, string finalBody, string accumulated,
        int? routedSlot, string? routedKey, double freeGb, Action<string>? log)
        => RunCrashRecoveryAsync(outResp, log, async writeGate =>
        {
            // ── 抢 save 槽位 KV（llama.cpp 崩溃即 release 槽位；抢到 n_saved>0 = 有效快照，否则全量路径）──
            var kv = _kvCache;
            bool snapshotOk = false;
            if (kv != null && routedSlot is int slot && !string.IsNullOrEmpty(routedKey))
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    await kv.SaveAsync(slot, routedKey);
                    int nSaved = kv.SavedTokens(routedKey);
                    if (nSaved > 0)
                    {
                        snapshotOk = true;
                        log?.Invoke($"崩溃快照抢获：{routedKey} → slot{slot}（{sw.Elapsed.TotalSeconds:F1}s，{nSaved} tokens）");
                    }
                    else
                    {
                        log?.Invoke("崩溃快照为空（槽位已 release，n_saved=0）：降级全量重放路径。");
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke($"崩溃快照保存失败：{ex.Message}，降级全量重放路径。");
                }
            }

            // ── 内存余量检查：空闲 RAM < 4GB → 预算收紧 25%（防同点再崩）──
            bool tightBudget = freeGb < TightMemoryFreeGb;
            int budget = _cfg.GetInputBudget();
            if (tightBudget)
            {
                budget = Math.Max(AppConfig.MinInputBudgetTokens, (int)(budget * TightBudgetFactor));
                log?.Invoke($"内存余量不足（空闲 {freeGb:F1} GB < {TightMemoryFreeGb} GB）：重放预算收紧 25% 防再崩。");
            }

            string? replayBody = null;
            bool usedSnapshot = false; // 实际走快照接续路径的标志（末行日志准确反映路径）

            // ── 快照接续：restore 快照 + 回填 assistant（已生成部分）+ 续接指令 ──
            if (snapshotOk && kv != null && routedSlot is int slot2 && !string.IsNullOrEmpty(routedKey))
            {
                bool restored = false;
                try { restored = await kv.RestoreAsync(slot2, routedKey); }
                catch (Exception ex) { log?.Invoke($"快照 restore 异常：{ex.Message}"); }

                if (restored)
                {
                    // accumulated 为空（prefill 阶段崩溃无输出）→ 不构造空 assistant 续接体，原请求直接重放（restore 的 KV 供前缀复用）
                    string? contBody = string.IsNullOrEmpty(accumulated)
                        ? null
                        : OutputContinuer.BuildContinuationBody(finalBody, accumulated);
                    bool useSnapshot = contBody != null || string.IsNullOrEmpty(accumulated);
                    if (!useSnapshot)
                        log?.Invoke("续接体构造失败：降级全量重放路径。");

                    if (useSnapshot)
                    {
                        var target = contBody ?? finalBody;
                        var (ok, guarded, note) = await TokenGuard.GuardAsync(_hc, _backendPort, target, budget);
                        if (!ok)
                        {
                            log?.Invoke($"续接中止：{note}（内存余量不足且上下文无法裁剪）。");
                            return; // 中止并明确报错（客户端流结束，agent 侧重试走现有机制）
                        }
                        if (note != null) log?.Invoke(note);
                        replayBody = guarded ?? target;
                        usedSnapshot = true;
                    }
                }
                else
                {
                    log?.Invoke("快照 restore 失败（槽位忙？）：降级全量重放路径。");
                }
            }

            // ── 全量重放路径（无快照 / restore 失败）：严格预算 TokenGuard 裁剪 + 原请求重发 ──
            if (replayBody == null)
            {
                var (ok, guarded, note) = await TokenGuard.GuardAsync(_hc, _backendPort, finalBody, budget);
                if (!ok)
                {
                    log?.Invoke($"重放中止：{note}（内存余量不足且上下文无法裁剪）。");
                    return;
                }
                if (note != null) log?.Invoke(note);
                replayBody = guarded ?? finalBody;
            }

            log?.Invoke(usedSnapshot ? "崩溃快照接续：restore KV + 回填已生成部分 + 续接指令…" : "全量重放：原请求重发（严格预算）…");
            var (replayCompleted, _) = await OutputContinuer.SendAndPipeStreamAsync(_hc, uri, _backendPort, replayBody, outResp, _cfg, log, writeGate);
            if (!replayCompleted)
                log?.Invoke("重放流再次中断（二次崩溃？）：本次恢复失败，agent 侧重试将走现有机制。");
        });

    /// <summary>崩溃恢复公共骨架（审计 O-10：收敛 A/B 分支重复的 keep-alive 启动 + 分支 C 探测 + 异常兜底 + 收尾样板）：
    /// 立即启动 SSE keep-alive（保活客户端）→ 探测客户端连接（断开即放弃重放）→ 执行分支体 → 统一异常兜底与 keep-alive 收尾。</summary>
    private async Task RunCrashRecoveryAsync(HttpListenerResponse outResp, Action<string>? log, Func<SemaphoreSlim, Task> body)
    {
        // ── SSE keep-alive（立即启动：从崩溃检测时刻起保活客户端，Trae 看到停顿后继续出字）──
        var keepAliveCts = new CancellationTokenSource();
        var writeGate = new SemaphoreSlim(1, 1); // 写门控：keep-alive 与重放管道并发写互斥，防 SSE 行交错
        Task keepAliveTask = RunKeepAliveAsync(outResp, writeGate, keepAliveCts.Token, log);
        try
        {
            // ── 分支 C 探测：客户端已断开 → 不重放（agent 侧重试走现有 KV restore 路径）──
            if (!await ProbeClientConnectedAsync(outResp, writeGate))
            {
                log?.Invoke("客户端已断开：跳过重放（agent 侧重试将走现有 KV restore 路径）。");
                return;
            }
            await body(writeGate);
        }
        catch (Exception ex)
        {
            log?.Invoke($"崩溃恢复异常：{ex.Message}");
        }
        finally
        {
            keepAliveCts.Cancel();
            try { await keepAliveTask; } catch { } // 等在途 keep-alive 写入完成再返回（调用方负责关连接）
        }
    }

    /// <summary>分支 B：进程死亡 → 重启至多 MaxAutoRestarts 次并等就绪 → 严格预算全量重放（无快照，防同点再崩）。
    /// keep-alive 保活 / 分支 C 探测 / 异常兜底由 RunCrashRecoveryAsync 公共骨架提供（审计 O-10）。</summary>
    private Task RestartAndReplayAsync(Uri uri, HttpListenerResponse outResp, string finalBody, Action<string>? log)
        => RunCrashRecoveryAsync(outResp, log, async writeGate =>
        {
            int maxRestarts = Math.Max(0, _cfg.MaxAutoRestarts);
            if (maxRestarts == 0)
            {
                log?.Invoke("进程已死且 MaxAutoRestarts=0（自动重启禁用）：无法自动恢复，请手动启动。");
                return;
            }

            bool restarted = false;
            for (int attempt = 1; attempt <= maxRestarts && !restarted; attempt++)
            {
                log?.Invoke($"崩溃恢复：重启 llama-server（{attempt}/{maxRestarts}）…");
                RaiseStatus($"崩溃恢复：正在重启后端服务（{attempt}/{maxRestarts}）…");
                try
                {
                    await EnsureRunningAsync();
                    restarted = true;
                }
                catch (Exception ex)
                {
                    log?.Invoke($"重启失败（{attempt}/{maxRestarts}）：{ex.Message}");
                }
            }

            if (!restarted)
            {
                log?.Invoke("全部重启失败：无法自动恢复，请手动启动。");
                return;
            }

            // 重启后后端端口可能变化（自动探测空闲端口），重建 URI
            var replayUri = new Uri($"http://localhost:{_backendPort}{uri.AbsolutePath}{uri.Query}");

            // 严格预算全量重放（无快照）：重启后内存状态未知，统一收紧 25% 防同点再崩
            int budget = Math.Max(AppConfig.MinInputBudgetTokens, (int)(_cfg.GetInputBudget() * TightBudgetFactor));
            var (ok, guarded, note) = await TokenGuard.GuardAsync(_hc, _backendPort, finalBody, budget);
            if (!ok)
            {
                log?.Invoke($"重放中止：{note}（上下文无法裁剪到严格预算）。");
                return;
            }
            if (note != null) log?.Invoke(note);

            log?.Invoke("全量重放：原请求重发（严格预算，无快照）…");
            var (replayCompleted, _) = await OutputContinuer.SendAndPipeStreamAsync(_hc, replayUri, _backendPort, guarded ?? finalBody, outResp, _cfg, log, writeGate);
            if (!replayCompleted)
                log?.Invoke("重放流再次中断：本次恢复失败。");
        });

    /// <summary>探测客户端连接是否存活：立即写一行 keep-alive 注释；写失败 = 客户端已断开（分支 C）。</summary>
    private static async Task<bool> ProbeClientConnectedAsync(HttpListenerResponse outResp, SemaphoreSlim writeGate)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(": keepalive\n");
        await writeGate.WaitAsync();
        try
        {
            await outResp.OutputStream.WriteAsync(bytes);
            await outResp.OutputStream.FlushAsync();
            return true;
        }
        catch
        {
            return false; // 写入失败 = 客户端已断开
        }
        finally
        {
            writeGate.Release();
        }
    }

    /// <summary>SSE keep-alive：每 N 秒写一行注释（客户端忽略但连接不断），直到取消或客户端断开。</summary>
    private async Task RunKeepAliveAsync(HttpListenerResponse outResp, SemaphoreSlim writeGate, CancellationToken ct, Action<string>? log)
    {
        var intervalSec = Math.Max(1, _cfg.RecoveryKeepAliveIntervalSeconds);
        var bytes = System.Text.Encoding.UTF8.GetBytes(": keepalive\n");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSec), ct);
                await writeGate.WaitAsync(ct);
                try
                {
                    await outResp.OutputStream.WriteAsync(bytes);
                    await outResp.OutputStream.FlushAsync();
                }
                finally
                {
                    writeGate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止（恢复流程完成）
        }
        catch (Exception ex)
        {
            log?.Invoke($"keep-alive 停止：{ex.Message}");
        }
    }
}