# UI 配置面板分组与中文注释优化方案

> 版本 v1.0 · 2026-09-01 · 依据：`MainForm.Ui.cs` BuildConfigPanel() 现状探查

## 1. 现状问题

- **平铺无层次**：`BuildConfigPanel()` 把 36 个配置项（35 AddRow + 模式行）全部塞进**一个** 3 列 TableLayoutPanel（`标签 | 控件 | 按钮`），用户面对一长串参数定位成本高；
- **注释覆盖不全**：已有 23 个控件带 tooltip 中文注释，但仍有 11 个核心参数无注释（exe / 模型 / 端口 / ctx / ngl / parallel / kv / 线程 / 休眠 / P核掩码 / 模式）；
- **无分组语义**：从"模型加载"到"崩溃恢复"混排，无法一眼看出"改哪里属于哪类"。

## 2. 目标

1. 按 **基础参数 / 资源管理 / 高级选项** 三组建立视觉层次（GroupBox 分组 + 中文分组标题）；
2. **全部 36 项参数具备中文注释**（已覆盖 23 条保留逐字不动，补 11 条）；
3. 纯 UI 布局改造：控件字段 / 事件绑定 / Config 双向映射 **零改动**。

## 3. 分组设计

### 3.1 三组划分（36 项，内部保持现状顺序）

| 分组 | 标题 | 包含参数（16/10/10） |
|---|---|---|
| 基础参数 | ▍基础参数 · 模型加载与推理 | exe、模型、端口、ctx、ngl、parallel、kv、线程、load-mode、ubatch、batch、cache-type-k/v、flash-attn、spec-type、spec-draft-n-max（15） |
| 资源管理 | ▍资源管理 · 缓存/快照/显存 | 缓存路径、Cache-RAM、空闲slot缓存、Token Guard、输出预留、Prompt头部开销、request-dump、自动强占、自动快照、休眠(min)（10） |
| 高级选项 | ▍高级选项 · 流式/续接/恢复 | log-queue-full、tb(batch线程)、附加、P核掩码、流式、输出续接、最大续接、续接超时、崩溃恢复、最大重启、模式(智能按需)（11） |

### 3.2 视觉结构（BuildConfigPanel 重构）

```
BuildConfigPanel() → 外层 Panel (Dock=Top, AutoSize)
├── GroupBox「▍基础参数 · 模型加载与推理」          ← 原生分组框（ForeColor 调浅色适配深色主题）
│   └── TableLayoutPanel (3列) + AddRow × 15
├── GroupBox「▍资源管理 · 缓存/快照/显存」
│   └── TableLayoutPanel (3列) + AddRow × 10
└── GroupBox「▍高级选项 · 流式/续接/恢复」
    └── TableLayoutPanel (3列) + AddRow × 11
```

实现要点：
- 抽取 `Control MakeGroup(string title, Action<TableLayoutPanel, AddRowHandler> build)` 局部辅助——每组新建 GroupBox（Dock=Top, AutoSize, Padding）+ 内部 3 列 TableLayoutPanel + 复用现有 AddRow；
- GroupBox 设置 `ForeColor = Color.FromArgb(210,210,210)`、`Font = 9F 加粗`，标题清晰且贴合深色主题；组内控件沿用现有白字/黑勾样式（AddRow 不变）；
- 外层仍返回 Dock=Top AutoSize 面板，挂载到 `_tabConfig`（已 AutoScroll=true，组多也不会裁切）。

## 4. 中文注释（补 11 条 tooltip）

现状 23 条 tooltip（L527-549）逐字保留；以下 11 条新增，文案基于现有代码语义（--参数名 / 默认值，不编造）：

| 控件 | 中文注释（方案） |
|---|---|
| `_txtExe` | llama-server 可执行文件路径（含 llama-server.exe）；无效时自动查找或手动浏览 |
| `_txtModel` | GGUF 模型文件路径（.gguf），留空 = 启动失败前由 AutoFindExe 提示 |
| `_numPort` | 前端 HTTP 监听端口；智能模式后端占用 Port+1，监听中禁改 |
| `_numCtx` | 上下文长度（--ctx-size）；KV 总量 = ctx × parallel，显存紧张时优先降 |
| `_numNgl` | GPU 层数（--n-gpu-layers）；999 = 全量 offload 显存，0 = 纯 CPU |
| `_numParallel` | 并行槽位数（--parallel）；每槽独立上下文，多 agent 并发按此路由 |
| `_chkNoKv` | --no-kv-unified：不启用统一 KV 缓存（unified KV 与分离 KV 场景） |
| `_numThreads` | CPU 线程数（--threads）；默认 = 逻辑核心数 |
| `_numIdleMin` | 智能模式：空闲 N 分钟后自动休眠释放显存，请求自动唤醒 |
| `_txtPcoreMask` | P 核 CPU 亲和掩码（--p-core-mask，十六进制）；留空 = 不限制 |
| `_chkAuto` | 智能按需模式：空闲休眠 + 请求唤醒 + 端口复用，推荐开启 |

## 5. 影响面与风险

| 项 | 影响 |
|---|---|
| 代码改动 | 仅 `MainForm.Ui.cs` 的 `BuildConfigPanel()` 一个方法（布局重构） |
| 控件/事件/映射 | 零改动（AddRow 只是布局；字段、WireEvents、ApplyConfigToUi/SyncConfigFromUi 不碰） |
| 测试 | 111 项全绿不受影响（UI 布局无单测） |
| 布局风险 | 分组后面板变高 → `_tabConfig` 已 AutoScroll=true，可滚动 |

## 6. 验收标准

- [ ] `dotnet build` 0 警告 0 错误；
- [ ] `dotnet test` 111 项全绿；
- [ ] UI 呈现三组分组框 + 中文分组标题，36 项参数全部有 tooltip 中文注释；
- [ ] 控件行为不变（参数编辑/映射/导入导出路径不受影响）；
- [ ] 独立 commit 可回退（`refactor(ui): 配置面板三组分组建 + 11 条中文注释补齐`）。
