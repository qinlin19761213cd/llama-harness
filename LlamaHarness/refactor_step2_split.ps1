# 步骤2：将控件字段区 + UI 构建方法区剪切到 MainForm.Ui.cs（partial）
$ErrorActionPreference = 'Stop'
$p = 'C:\project\lunch\LlamaHarness\MainForm.cs'
$ui = 'C:\project\lunch\LlamaHarness\MainForm.Ui.cs'
$lines = [System.IO.File]::ReadAllLines($p)   # 去行尾；WriteAllLines 自动补 CRLF
$total = $lines.Count
Write-Host "原 MainForm.cs 行数: $total"

# 行号（1-based）→ 索引（0-based）
# 字段区：L14 - L191（// —— 参数控件 ... _lblRestoreHit）
# UI 构建区：L453 - L1242（// ==================== UI 构建 === ... BuildStatsPanel 结束）
$fieldStart = 13; $fieldEnd = 190     # 索引 13..190
$uiStart    = 452; $uiEnd    = 1241   # 索引 452..1241

$fields  = $lines[$fieldStart..$fieldEnd]
$uiBuild = $lines[$uiStart..$uiEnd]

# 校验首尾锚点
if ($fields[0] -notmatch '参数控件') { Write-Host "[FAIL] 字段区起点不符: $($fields[0])"; exit 1 }
if ($fields[-1] -notmatch '_lblRestoreHit') { Write-Host "[FAIL] 字段区终点不符: $($fields[-1])"; exit 1 }
if ($uiBuild[0] -notmatch 'UI 构建') { Write-Host "[FAIL] UI 构建区起点不符: $($uiBuild[0])"; exit 1 }
if ($uiBuild[-1].Trim() -ne '}') { Write-Host "[FAIL] UI 构建区终点不符: $($uiBuild[-1])"; exit 1 }
Write-Host '锚点校验通过'

# MainForm.cs 保留：L1-13（含 _config/_scheduler）+ L192-452 + L1243-末尾
$keepMain = @()
$keepMain += $lines[0..12]
$keepMain += $lines[191..451]
$keepMain += $lines[1242..($total-1)]

# 生成 MainForm.Ui.cs
$uiContent = @()
$uiContent += 'namespace LlamaHarness;'
$uiContent += ''
$uiContent += '/// <summary>'
$uiContent += '/// MainForm 的 UI 构建部分（partial）：全部控件字段 + 界面构建方法。'
$uiContent += '/// 业务逻辑 / 事件处理见 MainForm.cs（后续拆分出 MainFormPresenter 与各区域 Controller）。'
$uiContent += '/// </summary>'
$uiContent += 'public partial class MainForm : Form'
$uiContent += '{'
$uiContent += ''
$uiContent += $fields
$uiContent += $uiBuild
$uiContent += '}'

[System.IO.File]::WriteAllLines($ui, $uiContent, [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllLines($p, $keepMain, [System.Text.UTF8Encoding]::new($false))
Write-Host "MainForm.Ui.cs 行数: $($uiContent.Count)"
Write-Host "MainForm.cs 新行数: $($keepMain.Count)"
