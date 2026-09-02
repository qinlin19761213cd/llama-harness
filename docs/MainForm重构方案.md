# MainForm.cs 改造方案（Presenter 分层重构）

> 项目：LlamaHarness（WinForms / .NET）
> 目标文件：`LlamaHarness/MainForm.cs`（2362 行）
> 原则：**行为 100% 不变、每步可编译、每步可回退**。本次只给方案，未改动任何源码。

---

## 一、现状诊断

### 1.1 职责全景

MainForm.cs 单类承载了 **10 类职责**，方法行数证据如下（≥15 行方法实测统计，共 2362 行）：

| # | 职责 | 关键成员 | 实测行数 |
|---|------|----------|---------|
| 1 | 全局配色 + 控件工厂 | `C_*` 16 个常量、`MakeBtn`/`MakeGrid`/`MakeCardPanel`/`MakeRaw*`/`ApplyStatsGridStyle`/`ApplyBlackCheck`/`LoadIcon` | ~200 |
| 2 | 控件字段声明 | 50+ 个参数控件、8 个操作按钮、10+ 状态标签、日志/统计/槽位控件 | ~180 |
| 3 | UI 构建 | `BuildUi`(73) `BuildLeftPanel`(159) `BuildTabArea`(225) `BuildStatusPanel`(117) `BuildConfigPanel`(136) `BuildStatsPanel`(34) `BuildTitleBlock`(34) `ShowDocInPanel`(32) `SelectTab`(15) | ~1000 |
| 4 | Markdown 渲染 | `RenderMarkdownToRichTextBox`(108) `StripMdInline` | ~115 |
| 5 | 配置 <-> UI 双向同步 | `WriteConfigToUi`(51) `SyncUiToConfig`(53) `AutoFindExe`(15) | ~125 |
| 6 | 事件接线 + 事件处理 | `WireEvents`(89) `OnStartClick` `OnExport/ImportConfigClick` `OnClearCacheClick` `OnAutoModeEdited` 等 | ~215 |
| 7 | 手动刷新 + llama.cpp 监控卡片 | `OnManualRefresh`(67) `UpdateSlotsCard`(24) `UpdatePropsCard`(64) `UpdateMetricsCard`(21) `ToggleRaw`(15) `EnsureMonitorCollector` | ~240 |
| 8 | 调度器事件 -> UI（跨线程） | `OnSchedulerStatus` `OnThinkingModeChanged` `OnPhaseChanged`(19) `RefreshSlotGrid`(16) `RefreshSlotMgmtGrid`(30) `AppendSlotLog`(26) `OnSlotMgmtCellChanged`(23) | ~165 |
| 9 | 统计区 | `OnRoundUpdated`(24) `OnRoundRemoved`(16) `OnSessionReset`(17) `UpdateSummary`(25) `UpdateRestoreCard`(24) `FillStatRow`(15) | ~125 |
| 10 | 状态机 + 日志渲染 + 生命周期 | `ApplyPhase`(54) `OnFormClosing`(35) `AppendLog` `OnLogFlush`(52) | ~170 |

### 1.2 关键病灶

- **`BuildConfigPanel()` 136 行**：参数行装配（40 行）+ ToolTip 批量注册（~30 行）+ 样式遍历，纯构建逻辑与配置域语义耦合。
- **`OnManualRefresh()` 67 行**：把"本地采集（CPU/内存/显存）+ HTTP 采集（三接口）+ 三卡片渲染 + 右侧摘要 + 崩溃告警状态机"五件事塞进一个方法，且混有 UI 线程判断（`IsDisposed`）与业务状态（`_crashAlertShown`）。
- **`WireEvents()` 89 行**：参数控件 30+ 个事件逐一 `+= OnParamEdited`，纯样板，且把"控件事件""调度器事件""配置即时同步""思考模式入口"混在一处。
- **字段区 180 行**：控件创建内联在声明处，与布局/逻辑交错，无法独立复用或测试。

---

## 二、目标架构

采用 **Presenter 模式**（WinForms 无数据绑定基础设施，Presenter 比 MVVM 更贴切）。

```
┌──────────────────────────────────────────────────────────────┐
│ MainForm（薄外壳，~120 行）                                    │
│   · 持有 MainFormView + MainFormPresenter                     │
│   · Form 生命周期（OnShown / FormClosing）                    │
│   · 把 SmartScheduler 事件统一转发给 Presenter               │
├──────────────────────────────────────────────────────────────┤
│ MainFormView（View：UI 构建，~1000 行 → partial 3 文件）       │
│   · 全部控件字段 + Build* 方法 + SelectTab/页签管理            │
│   · 向 Presenter 暴露命令方法（internal）：                    │
│     AppendLog / ApplyPhase / RefreshSlotGrid / 各卡片刷新等    │
├──────────────────────────────────────────────────────────────┤
│ MainFormPresenter（事件处理 + 编排，~550 行）                   │
│   · 订阅 View 控件事件（替代 WireEvents）                      │
│   · 订阅 SmartScheduler 事件（跨线程 BeginInvoke 保留）        │
│   · 编排：启动/停止/导入导出/清缓存/手动刷新                   │
├──────────────────────────────────────────────────────────────┤
│ 领域 Controller（各区域独立，~100-240 行）                     │
│   · ConfigBinder          配置<->UI 映射 + AutoFindExe         │
│   · MonitorPanelController 手动刷新 + 三卡片 + ToggleRaw       │
│   · StatsPanelController   统计表格 + 汇总 + Restore 卡片       │
│   · SlotPanelController    槽位两表 + 槽位日志 + 勾选回写       │
│   · StatusPanelController  状态机 ApplyPhase + 右侧面板         │
│   · LogView               日志队列防抖 + 着色渲染               │
├──────────────────────────────────────────────────────────────┤
│ 纯工具（static，无状态）                                       │
│   · UiTheme           配色常量 + 控件工厂（Make* / LoadIcon）  │
│   · MarkdownRenderer  md → RichTextBox 渲染                    │
└──────────────────────────────────────────────────────────────┘
```

**依赖方向**：`MainForm → View/Presenter`；`Presenter → View + 各 Controller`；`Controller → View 控件引用 + AppConfig + SmartScheduler`；工具类无依赖，人人可用。**禁止反向依赖**（View 不感知调度器内部）。

---

## 三、拆分设计（类级规格）

### 3.1 UiTheme（static，新增）

**迁移自**：MainForm 顶部 16 个 `C_*` 颜色常量 + 所有 `static Make*` 工厂。

| 成员 | 来源 |
|------|------|
| `C_Bg..C_Warn` 16 个颜色 | L11-23 |
| `MakeBtn` / `MakeSectionTitle` / `MakeTabBtn` | L1323 / L1386 / L1174 |
| `MakeGrid` / `MakeGridCol` / `MakeCheckCol` / `ApplyStatsGridStyle` | L1347 / L1371 / L1378 / L1361 |
| `MakeCardPanel` / `MakeCardTitle` / `MakeRawButton` / `MakeRawTextBox` | L468 / L1161 / L478 / L493 |
| `ApplyBlackCheck` | L1399 |
| `LoadIcon` + `IconCache` | L704 |

> 注意：`ApplyBlackCheck` 是实例方法（内联事件），迁移时改为 static 无副作用版。

### 3.2 MarkdownRenderer（static，新增）

**迁移自**：`RenderMarkdownToRichTextBox`(L753) + `StripMdInline`(L861)。
依赖 `UiTheme` 取色。`ShowDocInPanel`（读文件 + 组装）归 View。

### 3.3 MainFormView（View，`MainForm.cs` partial 化）

`MainForm` 声明为 `public partial class MainForm : Form`，UI 构建拆到：

- `MainForm.cs`（外壳）：字段引用、ctor、`OnShown`、`OnFormClosing`、事件转发入口。
- `MainForm.Ui.cs`（partial）：全部控件字段 + `BuildUi`/`BuildLeftPanel`/`BuildTabArea`/`BuildStatusPanel`/`BuildConfigPanel`/`BuildStatsPanel`/`BuildTitleBlock`/`ShowDocInPanel`/`SelectTab`/`ApplyContentSplitRatio`。
- `MainForm.View.cs`（partial，可选再拆）：对外命令方法（`internal`），供 Presenter/Controller 调用，如 `AppendLog`、`ApplyPhase`、`RefreshSlotGrid`、卡片刷新、状态标签写入。

> partial 仍属同一类型——控件字段天然共享，无需属性转发，这是**改动最小**的落地方式。若后续要严格类型隔离，可把 View 换成独立 `MainFormView : Control` 容器，代价是多一层控件宿主。

### 3.4 MainFormPresenter（独立类，新增）

**迁移自**：`WireEvents`(L1711) 全部接线 + 下列事件处理，替换 ctor 中的调度器事件订阅块（L223-246）。

| 迁移成员 | 说明 |
|---------|------|
| `WireEvents` 的控件事件接线 | 30+ 参数控件 `+= OnParamEdited`；浏览/启动/停止/清日志/清缓存/思考/极速/导入/导出 |
| `OnParamEdited` / `OnIdleEdited` / `OnAutoModeEdited` | 配置即时同步 + 调度器联动 |
| `OnStartClick` / `OnExportConfigClick` / `OnImportConfigClick` / `OnClearCacheClick` | 命令编排（调用 ConfigBinder / scheduler / 日志） |
| 调度器 8 个事件订阅 | `Log`/`StatusChanged`/`PhaseChanged`/`StatsReset`/`ThinkingModeChanged`/`SlotBindingChanged`/`SlotLog` |
| `OnSchedulerStatus` / `OnThinkingModeChanged` / `OnPhaseChanged` / `RefreshSlotBindings` | 跨线程转发（保留 `BeginInvoke`） |

Presenter 持有 `MainForm`（View）引用，调用其 `internal` 命令方法更新 UI；持有 `AppConfig` + `SmartScheduler` 引用。**调度器事件不改，仍由 View 事件转发或 Presenter 直接订阅二选一**（推荐后者，把订阅收敛进 Presenter）。

### 3.5 领域 Controller（各独立类）

| 类 | 迁移成员 | 依赖 |
|----|---------|------|
| `ConfigBinder` | `WriteConfigToUi`(51) `SyncUiToConfig`(53) `AutoFindExe`(15) `UpdatePortControlState` | View 控件 + `AppConfig` |
| `MonitorPanelController` | `OnManualRefresh`(67) `UpdateSlotsCard`(24) `UpdatePropsCard`(64) `UpdateMetricsCard`(21) `ToggleRaw`(15) `EnsureMonitorCollector` | `SystemMetrics` + `LlamaCppMonitorCollector` + 卡片控件 |
| `StatsPanelController` | `OnRoundUpdated`(24) `OnRoundRemoved`(16) `OnSessionReset`(17) `FindStatRow` `FillStatRow` `UpdateSummary`(25) `UpdateRestoreCard`(24) | `LlamaStatsParser` + `_gridStats` + 汇总标签 + `_scheduler.GetRestoreStats()` |
| `SlotPanelController` | `RefreshSlotGrid`(16) `RefreshSlotMgmtGrid`(30) `AppendSlotLog`(26) `OnSlotMgmtCellChanged`(23) `OnSlotLog` | `_scheduler.GetSlotBindings/SetSlot*` + 两表 + `_txtSlotLog` |
| `StatusPanelController` | `ApplyPhase`(54) + `_wakeTime` 运行时长 + 崩溃告警状态(`_crashAlertShown`) | 侧栏/状态控件 + `AppConfig` |
| `LogView` | `AppendLog` `OnLogFlush` `_logQueue` `_logFlushTimer` `MaxLogChars` | `_txtLog` + `LogFile` |

> **崩溃告警（`_crashAlertShown` + `_lblStatus` 变红）** 与"系统资源采集"无因果关系，仅是都发生在手动刷新时——拆出后应让 `MonitorPanelController` 采集完成后**回调** `StatusPanelController.CheckCrashCircuit()`，职责归位。

---

## 四、实施步骤（每步可编译 + 冒烟，行为不变）

### 步骤 1：抽纯工具类（零风险）
新建 `UiTheme.cs`、`MarkdownRenderer.cs`，把颜色/工厂/MD 渲染搬入；MainForm 改为调用。删除原方法。
**验收**：`dotnet build` 通过，启动冒烟各页签样式一致。

### 步骤 2：partial 拆分 UI 构建（结构重组）
`MainForm` 声明 partial，UI 构建 + 字段迁至 `MainForm.Ui.cs`。**保持 `BuildUi` 内各 `Build*` 调用顺序不变**（`BuildTabArea` 内创建 `_btnBrowseExe` 等字段的使用顺序）。
**验收**：编译通过，所有页签/折叠/缩放行为一致。

### 步骤 3：抽 ConfigBinder + LogView（内聚域下沉）
把配置双向映射、日志队列渲染分别独立。
**验收**：编译通过，改参数即时生效、日志着色/滚动/上限一致。

### 步骤 4：抽 MainFormPresenter（事件处理离类）
新建 Presenter，迁移 `WireEvents` + 全部事件处理 + 调度器订阅；MainForm 只留：`ctor` 组装、`OnShown` 调 `presenter.Start()`、`OnFormClosing` 调 `presenter.Shutdown()`。
**验收**：编译通过，全流程冒烟（启动/停止/休眠/导入导出/清缓存/手动刷新）。

### 步骤 5：抽区域 Controller（按需）
拆分 `MonitorPanelController` / `StatsPanelController` / `SlotPanelController` / `StatusPanelController`，Presenter 变薄为纯编排。
**验收**：编译通过，回归冒烟；`MainForm.cs` 应降至 ~120-150 行。

### 步骤 6（可选进阶）：区域 UserControl 化
各页签封装 `UserControl`，MainForm 只组装。收益是每页可独立测试/复用；代价是控件字段归属重构、事件链路重接，需另行评估。

---

## 五、风险与红线（重构必须守住）

1. **跨线程 `BeginInvoke` 语义不可丢**：调度器 8 个事件均来自后台线程，迁移时每处保留 `if (!IsHandleCreated) return; BeginInvoke(...)`。Controller 若需跨线程回 UI，注入 `ISynchronizeInvoke`（即 Form）而不是自己跨线程。
2. **日志定时器跨线程禁令**：`_logFlushTimer` 常驻，**禁止**在后台线程 `Start/Stop`（Win32 SetTimer 绑定创建线程的消息循环，跨线程 Start 静默失效导致 UI 停摆）。`LogView` 内保留"定时器常驻 + 空队列早退"逻辑。
3. **初始化顺序不变**：`BuildUi → LoadConfigToUi → UpdatePortControlState → WireEvents → 调度器订阅`。其中 `LoadConfigToUi` 必须在 `WireEvents` 之前（初始回填不触发 `OnParamEdited`）。拆 Presenter 后由 ctor 保证同样顺序。
4. **`_config` 单例共享**：`AppConfig` 是可变共享对象（UI、scheduler、ConfigBinder 三处引用），`SyncUiToConfig` 的调用点（OnParamEdited / 启动 / 停止 / 关闭 / 导入）一个不能少，迁移时逐点核对。
5. **`ApplyPhase` 调用点多**：`OnPhaseChanged`、`OnManualRefresh`（崩溃恢复后重刷）、启动流程内。收敛到 `StatusPanelController.ApplyPhase` 后，所有调用点必须统一改走该入口，避免双份状态源。
6. **不引入新依赖/不改公开 API**：MainForm 外部可见面（Program.cs `new MainForm()`）不变；重构期间禁止顺手改业务逻辑，一切以"行为等价"为验收。
7. **版本管理**：在 git 分支上逐步骤提交；每步独立可 `git revert`。

---

## 六、验收标准

- `dotnet build` 零警告。
- 现有 `LlamaHarness.Tests` 全部通过（虽不覆盖 UI，但保证领域层未被误伤）。
- 手动冒烟清单：启动/唤醒/休眠/停止、思考与极速切换、参数编辑即时生效、配置导入导出、清空缓存、手动刷新（三卡片 + 崩溃告警）、槽位强占/KV 勾选回写、日志着色滚动、关闭确认。
- `MainForm.cs` 从 2362 行降至目标 ~150 行；`MainForm.Ui.cs` + `MainForm.View.cs` 只含构建与纯 UI 命令，不含业务决策。
