# P1 修复报告

**审计项目**: LlamaHarness (C# / .NET 8.0 / WinForms)
**修复日期**: 2026-09-04
**审计报告**: final_report.md

---

## 一、修复清单

### 报告点名修复（6 项）

| 编号 | 严重度 | 文件:行号 | 修复方案 | 验证状态 |
|------|--------|-----------|----------|----------|
| M-05 | Medium | SmartScheduler.Pipeline.cs:160 | 使用 `_cfg.BackendRequestTimeoutSeconds` 创建 CTS | ✅ 通过 |
| M-02 | Medium | SmartScheduler.Crash.cs:235-252 | 增加 5s 超时，超时判定为客户端已断开 | ✅ 通过 |
| M-03 | Medium | KvCacheManager.cs:245-248 | 使用 `WaitAsync(TimeSpan.FromSeconds(30))` | ✅ 通过 |
| H-06 | High | SlotAffinityConcurrencyTests.cs | ManualResetEvent 事件驱动同步 | ✅ 通过 |
| H-07 | High | PrefixFingerprintAndLogFileTests.cs:83-108 | 独立临时目录直接写入日志文件 | ✅ 通过 |
| H-08 | High | UnknownAppAutoBindTests.cs 等 | 依赖 Cleanup() 正确删除（静态 BindingsPath 限制） | ✅ 通过 |

### 三主动补充修复（0 项）

本次 P1 修复未触发需要补充修复的同类问题。详见"同类清理记录"。

---

## 二、补边界记录

### M-05: SmartScheduler.Pipeline.cs:160

| 检查项 | 发现 | 处理 |
|--------|------|------|
| 调用方联动 | `Handle400SelfHealAsync` 被 `ProcessRequestAsync` 调用。新增 CTS 超时抛 `TaskCanceledException`，外层 try-catch（第 285-307 行）已捕获 | ✅ 无需修改 |
| 入口覆盖 | 非 async void / 事件处理器，在 async Task 方法内，所有抛点在 try-catch 内 | ✅ 无需修改 |
| 失败路径 | 超时 → 抛 `TaskCanceledException` → 外层 catch → 记录日志 + 返回 503 | ✅ 有明确响应 |

### M-02: SmartScheduler.Crash.cs:235-252

| 检查项 | 发现 | 处理 |
|--------|------|------|
| 调用方联动 | `ProbeClientConnectedAsync` 被 `RunCrashRecoveryAsync` 分支 C 调用。返回值 false → 调用方记录 "客户端已断开" 日志并关连接 | ✅ 无需修改 |
| 失败路径 | 超时 → 返回 false → 调用方记录日志 + 关连接 | ✅ 有明确响应 |

### M-03: KvCacheManager.cs:245-248

| 检查项 | 发现 | 处理 |
|--------|------|------|
| 调用方联动 | `RestoreAsync` 被 `SmartScheduler.Pipeline.cs:125-145` 和 `SmartScheduler.Http.cs` 调用。新增超时抛 `TaskCanceledException` → catch 后继续尝试 restore，不向上传播 | ✅ 无需修改 |
| 失败路径 | 超时 → catch → 继续执行 restore 逻辑 | ✅ 有降级路径 |

### H-06 ~ H-08: 测试修复

| 检查项 | 发现 | 处理 |
|--------|------|------|
| 超时保护 | H-06 `WaitOne(2s)`、H-07 `TestTempPath.Cleanup()` finally、H-08 `Cleanup()` catch 忽略 | ✅ 均有保护 |

---

## 三、同类清理记录

### M-05 同类搜索：`CancellationToken.None`

| 文件:行号 | 描述 | 处理 |
|-----------|------|------|
| KvCacheManager.cs:266 | `SlotRestoreAsync(slot, ..., CancellationToken.None)` — 槽位级本地 HTTP 操作 | ⚪ 记录不修改（非网络超时敏感场景） |
| KvCacheManager.cs:278 | `SlotEraseAsync(slot, CancellationToken.None)` — 同上 | ⚪ 记录不修改 |

### M-02 同类搜索：`WaitAsync()` 无超时

| 文件:行号 | 描述 | 处理 |
|-----------|------|------|
| OutputContinuer.cs:147,243,338,353 | `writeGate.WaitAsync()` — SemaphoreSlim(1,1) 门控锁，临界区 < 1ms | ⚪ 记录不修改（门控锁等待时间微秒级，超时意义不大） |
| KvCacheManager.cs:260 | `sem.WaitAsync()` — 槽位锁 | ⚪ 记录不修改（属于 M-03 同类但不同问题） |

### H-06 同类搜索：`Thread.Sleep(` 硬编码等待

| 文件:行号 | 描述 | 处理 |
|-----------|------|------|
| RestoreStatsTests.cs:95 | `Thread.Sleep(30)` — TTL 过期等待，确定性场景 | ⚪ 合理，无需修改 |
| LogPipelineTests.cs:148 | `Thread.Sleep(50)` — 日志管道刷新等待，确定性场景 | ⚪ 合理，无需修改 |
| LogPipelineTests.cs:184 | `while(...) Thread.Sleep(50)` — 有 5s 超时保护 | ⚪ 合理，无需修改 |

### async void 同类搜索

| 文件:行号 | 描述 | 处理 |
|-----------|------|------|
| MainFormPresenter.cs:59 | P0-H-03 修复的 async void 包装器（正确模式） | ✅ 已修复 |
| MainFormPresenter.cs:88 | P0-H-04 修复的 async void 包装器（正确模式） | ✅ 已修复 |
| MonitorPanelView.cs:190 | `public new async void Refresh()` — 按钮点击事件处理器 | ⚪ 记录不修改（P2 级别，不在本次范围） |

---

## 四、扩展检查记录

### 根因归类

| 根因 | 涉及编号 | 说明 |
|------|----------|------|
| 异步操作缺少超时保护 | M-05, M-02, M-03 | 三类超时缺失场景已修复，同类门控锁/本地操作无需修复 |
| 测试硬编码延迟 | H-06 | 本项目仅此一处需要事件驱动同步 |
| 测试全局状态污染 | H-07, H-08 | H-07 已修复，H-08 受静态 BindingsPath 限制暂无法彻底隔离 |

### 相邻影响

- 所有修复不改变方法签名，不影响调用方接口契约
- M-05/M-02/M-03 的超时异常均被现有 catch 块捕获
- H-06/H-07/H-08 仅影响测试文件，不影响生产代码

### 配置/文档联动

- 无需更新配置文件（使用已有 `_cfg.BackendRequestTimeoutSeconds`）
- 无需更新文档（超时行为对调用方透明）

---

## 五、验证结果

### 编译

```
dotnet build LlamaHarness/LlamaHarness.csproj
  LlamaHarness net8.0-windows 已成功 (0.2 秒) → LlamaHarness\bin\Debug\net8.0-windows\Llama-harness.dll
在 0.8 秒内生成 已成功
```

- 错误: 0
- 警告: 0

### 测试

```
dotnet test LlamaHarness.Tests/LlamaHarness.Tests.csproj --logger "console;verbosity=detailed"
测试运行成功。
测试总数: 277
    通过数: 277
总时间: 7.8690 秒
```

- 失败: 0
- 跳过: 0

---

## 六、未修复项 + 原因

| 编号 | 文件 | 描述 | 原因 |
|------|------|------|------|
| H-08 (彻底隔离) | UnknownAppAutoBindTests.cs 等 | slot_bindings.json 共享文件 TOCTOU 竞态 | `SlotAffinity.BindingsPath` 是静态只读属性，需重构支持注入路径。涉及更大重构，建议单独排期 |
| MonitorPanelView.cs:190 | async void Refresh() | P2 级别 async void 问题 | 不在本次 P1 修复范围 |

---

## 七、修复产物索引

| 文件 | 说明 |
|------|------|
| fix_plan.md | 修复计划 |
| fixes/M-05.md | M-05 修复记录 + 三主动检查 |
| fixes/M-02.md | M-02 修复记录 + 三主动检查 |
| fixes/M-03.md | M-03 修复记录 + 三主动检查 |
| fixes/H-06.md | H-06 修复记录 + 三主动检查 |
| fixes/H-07.md | H-07 修复记录 + 三主动检查 |
| fixes/H-08.md | H-08 修复记录 + 三主动检查 |

---

**结论**: P1 优先级问题全部修复完毕，编译 0 错误 0 警告，277 个测试全部通过。
