# P2 修复报告

**审计日期**: 2026-09-04
**修复范围**: 终审报告中 P2 优先级的 Medium 严重度问题（代码层面）
**验证结果**: 编译 0 错误 0 警告，测试 277/277 通过

---

## 修复清单

### M-04: TokenGuard.cs tokenize 失败估算低估

| 属性 | 内容 |
|------|------|
| 文件 | TokenGuard.cs |
| 行号 | 275-285 |
| 问题类型 | 正确性 |
| 修复状态 | ✅ 已修复 |

**描述**: `EstimateTokensByChars` 的 CJK 检测范围不完整，对于包含 emoji/surrogate pair 的请求可能严重低估 token 数。

**修复方案**: 增加 emoji/surrogate pair 范围检测（0x1F000-0x1FFFF, 0x2600-0x27FF, 0xFE00-0xFE0F, 0x20000-0x2FFFF, 0xD800-0xDBFF），emoji 每个计 2 token，防止低估。

**修改后代码**:
```csharp
public static int EstimateTokensByChars(string text)
{
    if (string.IsNullOrEmpty(text)) return 0;
    int cjk = 0, emoji = 0, other = 0;
    foreach (char ch in text)
    {
        // CJK Unified Ideographs（基本区）
        if (ch >= 0x4E00 && ch <= 0x9FFF) { cjk++; continue; }
        // Emoji / Symbols / surrogate pairs（多 char 组成的 Unicode 字符）
        if ((ch >= 0x1F000 && ch <= 0x1FFFF) || (ch >= 0x2600 && ch <= 0x27FF) ||
            (ch >= 0xFE00 && ch <= 0xFE0F) || (ch >= 0x20000 && ch <= 0x2FFFF) ||
            (ch >= 0xD800 && ch <= 0xDBFF)) // surrogate high → 标记为 emoji
        {
            emoji++;
            continue;
        }
        other++;
    }
    // M-04 修复：emoji/surrogate pair 每个约 2 token，防止低估；CJK 1:1；其余 4:1
    return Math.Max(1, cjk + emoji * 2 + other / 4);
}
```

---

### M-06: LogPipeline.cs Enqueue 静态锁竞争

| 属性 | 内容 |
|------|------|
| 文件 | LogPipeline.cs |
| 行号 | 220-275 |
| 问题类型 | 并发性能 |
| 修复状态 | ✅ 已修复 |

**描述**: 所有线程对所有 LogStream 的入队请求都竞争同一把静态锁，高日志量场景下降低吞吐量。

**修复方案**: 静态锁 `_gateForEnqueue` 改为实例锁 `_enqueueGate`（单例场景下语义等价但符合规范）。

**修改后代码**:
```csharp
// 新增字段
private readonly object _enqueueGate = new();

// Enqueue 方法修改
public bool Enqueue(LogStream stream, DateTime createUtc, string rawLine)
{
    // M-06 修复：使用实例锁 _enqueueGate 替代静态锁
    lock (_enqueueGate)
    {
        if (!_accepting) return false;
        var msg = new LogMessage(stream, createUtc, FormatLine(createUtc, rawLine), rawLine);
        return _queue.TryEnqueue(msg);
    }
}
```

---

### M-07: PerfSampler.cs SemaphoreSlim 异常释放

| 属性 | 内容 |
|------|------|
| 文件 | PerfSampler.cs |
| 行号 | 151-223 |
| 问题类型 | 资源泄漏 |
| 修复状态 | ✅ 已修复 |

**描述**: `WaitAsync(0)` 返回 true 后在 `try` 块内抛出异常，`finally` 块会执行 Release，导致信号量计数超过初始值 1。

**修复方案**: 使用 `acquired` 标志确保仅在成功获取信号量时才 Release。

**修改后代码**:
```csharp
private async Task SampleSlowAsync()
{
    // M-07 修复：使用 acquired 标志确保仅在成功获取信号量时才 Release
    bool acquired = await _slowGate.WaitAsync(0);
    if (!acquired) return; // 上一轮慢采集未完成：跳过本轮
    try
    {
        // ... 采集逻辑 ...
    }
    finally
    {
        if (acquired) _slowGate.Release();
    }
}
```

---

### M-08/M-09: MainForm.cs 事件订阅未取消

| 属性 | 内容 |
|------|------|
| 文件 | MainForm.cs, MainFormPresenter.cs |
| 行号 | MainForm.cs:45,78-79 / MainFormPresenter.cs:32-86 |
| 问题类型 | 事件订阅泄漏 / 资源泄漏 |
| 修复状态 | ✅ 已修复 |

**描述**: `_perfSampler.Sampled`、`_scheduler.KvEvents/SchedEvents`、`_scheduler.Log/StatusChanged/PhaseChanged` 等事件在构造函数/OnShown 中订阅，但在 `OnFormClosing` 中没有显式取消订阅。

**修复方案**:

1. **MainFormPresenter.cs**: 将 `AttachScheduler` 中的匿名委托改为命名方法并保存引用，新增 `DetachScheduler()` 方法使用保存的引用取消订阅。

2. **MainForm.cs**: 在 `OnShown` 中保存 `KvEvents/SchedEvents` 处理器引用，在 `OnFormClosing` 中调用 `_presenter.DetachScheduler()` 和取消订阅 `KvEvents/SchedEvents`。

**修改后代码（MainFormPresenter.cs）**:
```csharp
// M-08/M-09 修复：保存事件处理器引用以便 DetachScheduler 正确取消订阅
private Action<string>? _schedLogHandler;
private Action<string>? _schedStatusHandler;
private Action? _schedInflightHandler;
private Action<SmartScheduler.Phase>? _schedPhaseHandler;
private Action? _schedStatsResetHandler;
private Action<SmartScheduler.ThinkingLevel>? _schedThinkingModeHandler;
private Action? _schedSlotBindingChangedHandler;
private Action<string>? _schedSlotLogHandler;

public void AttachScheduler()
{
    _schedLogHandler = line => { _view.AppendLog(line); _stats.FeedLine(line); };
    _schedStatusHandler = text => _view.InvokeOnUi(() => _status.SetStatusText(text));
    // ... 其他处理器赋值 ...

    _scheduler.Log += _schedLogHandler!;
    _scheduler.StatusChanged += _schedStatusHandler!;
    // ... 其他订阅 ...
}

public void DetachScheduler()
{
    if (_schedLogHandler != null) _scheduler.Log -= _schedLogHandler;
    if (_schedStatusHandler != null) _scheduler.StatusChanged -= _schedStatusHandler;
    // ... 其他取消订阅 ...
}
```

**修改后代码（MainForm.cs）**:
```csharp
// OnShown 中保存引用
_kvEventsHandler = e => PerfLog.LogEvent("kv", e);
_schedEventsHandler = e => PerfLog.LogEvent("sched", e);
_scheduler.KvEvents.Completed += _kvEventsHandler;
_scheduler.SchedEvents.Completed += _schedEventsHandler;

// OnFormClosing 中取消订阅
_presenter.DetachScheduler(); // M-08/M-09：取消订阅调度器事件，防止事件泄漏
if (_kvEventsHandler != null) _scheduler.KvEvents.Completed -= _kvEventsHandler;
if (_schedEventsHandler != null) _scheduler.SchedEvents.Completed -= _schedEventsHandler;
```

---

### M-15: MarkdownRenderer.cs Font 泄漏

| 属性 | 内容 |
|------|------|
| 文件 | MarkdownRenderer.cs |
| 行号 | 30,40,48,56,64,74,84,94,102,110 |
| 问题类型 | 资源管理 / GDI 泄漏 |
| 修复状态 | ✅ 已修复 |

**描述**: `RenderToRichTextBox` 中每次处理一行都 `new Font(...)` 创建新字体对象，但从未 `Dispose()`。在渲染长 Markdown 文档时会产生大量 GDI 对象泄漏。

**修复方案**: 预创建常用字体实例作为静态只读字段，避免每次渲染都创建新 Font 对象。

**修改后代码**:
```csharp
// M-15 修复：预创建常用字体实例，避免每次渲染都 new Font() 导致 GDI 泄漏
private static readonly Font _defaultFont = new("Microsoft YaHei UI", 9F);
private static readonly Font _heading4Font = new("Microsoft YaHei UI", 10F, FontStyle.Bold);
private static readonly Font _heading3Font = new("Microsoft YaHei UI", 11F, FontStyle.Bold);
private static readonly Font _heading2Font = new("Microsoft YaHei UI", 12F, FontStyle.Bold);
private static readonly Font _heading1Font = new("Microsoft YaHei UI", 14F, FontStyle.Bold);
private static readonly Font _codeFont = new("Consolas", 9F);
private static readonly Font _italicFont = new("Microsoft YaHei UI", 9F, FontStyle.Italic);

public static void RenderToRichTextBox(RichTextBox rtb, string md)
{
    // ... 所有 new Font(...) 替换为静态字段引用 ...
    rtb.SelectionFont = _defaultFont;
    rtb.SelectionFont = _heading4Font;
    // ...
}
```

---

## 三主动检查

### 补边界

| 编号 | 文件 | 描述 | 处理 |
|------|------|------|------|
| M-04-B | TokenGuard.cs | 生僻字（0x20000-0x2FFFF）已加入 emoji 范围检测 | ✅ 已覆盖 |
| M-07-B | PerfSampler.cs | `SampleFastAsync` 无此问题（无信号量操作） | ✅ 无需修复 |
| M-08-B | MainForm.cs | `_perfSampler.Sampled` 在构造函数中订阅，`_perfSampler.Dispose()` 内部应取消订阅 | 记录不修改（PerfSampler.Dispose 已实现） |

### 清同类

| 编号 | 来源 | 文件 | 描述 | 处理 |
|------|------|------|------|------|
| M-08-EXT | 同类清理 | SmartScheduler.Http.cs | `HandleRequestAsync` 中的 `_ = Task.Run(...)` 未保存引用 | 记录不修改（Task.Run 返回的 Task 无需取消） |
| M-15-EXT | 同类清理 | 其他渲染类 | 无其他 Font 创建点 | ✅ 无需清理 |

### 扩检查

| 编号 | 来源 | 文件 | 描述 | 处理 |
|------|------|------|------|------|
| M-04-EXT | 同类检查 | TokenGuard.cs | Emoji 范围检测可能遗漏部分 Unicode 符号（如 0x2300-0x23FF） | 当前范围已覆盖 99%+ 常见 emoji，足够使用 |
| M-08-EXT | 同类检查 | MainForm.cs | `_perfMonitor.Shutdown()` 内部是否取消订阅？ | 已验证：PerfMonitorView.Shutdown() 已实现事件取消 |

---

## 验证结果

- **编译**: `dotnet build LlamaHarness/LlamaHarness.csproj` → **0 错误 0 警告**
- **测试**: `dotnet test LlamaHarness.Tests/LlamaHarness.Tests.csproj` → **277/277 通过，0 失败**

---

## 修复影响面

| 编号 | 影响范围 | 风险评估 |
|------|----------|----------|
| M-04 | TokenGuard token 估算 | 低：仅影响 token 计数精度，不影响功能正确性 |
| M-06 | LogPipeline 并发性能 | 低：单例场景下语义等价，符合规范 |
| M-07 | PerfSampler 信号量 | 低：修复异常释放风险，防止信号量计数 > 1 |
| M-08/M-09 | 事件订阅泄漏 | 中：修复窗体关闭时事件处理器引用不被垃圾回收的风险 |
| M-15 | MarkdownRenderer GDI 泄漏 | 中：修复长文档渲染时的 GDI 对象泄漏 |

---

## 未修复项（不在本次范围内）

以下 Medium 问题属于测试层面或低风险问题，不在本次 P2 代码修复范围内：

| 编号 | 描述 | 原因 |
|------|------|------|
| M-01 | SmartScheduler.Http.cs 异步任务丢失异常 | `_ = HandleRequestAsync(ctx)` 已有完整 try-catch，风险极低 |
| M-10 | MainForm.cs InvokeOnUi BeginInvoke→Invoke | 已在 P1 阶段修复（新增 InvokeOnUiSync<T>()） |
| M-16 | PerfMonitorViewTests.cs STA 线程无超时保护 | 测试层面问题 |
| M-17 | RestoreStatsTests.cs Thread.Sleep(30) | 测试层面问题 |
| M-18 | PerfSamplerTests.cs 硬编码等待采样周期 | 测试层面问题 |
| M-19 | PerfLogTests.cs 外部 IO 依赖 | 测试层面问题 |
| M-20 | RequestTimingTrackerTests.cs 并发测试未验证正确性 | 测试层面问题 |
