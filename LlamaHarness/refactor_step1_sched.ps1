# 步骤1：从 SmartScheduler.cs 删除 17 个已迁出静态方法 + 替换全部调用点
$ErrorActionPreference = 'Stop'
$base = 'C:\project\lunch\LlamaHarness'
$sched = Join-Path $base 'SmartScheduler.cs'

# ---------- 1. 删除方法（行数组 + 注释回溯 + 括号配平 + 单行方法判定） ----------
$lines = [System.IO.File]::ReadAllLines($sched)

# 待删方法名（含重载：EnsureStreamTrue 两个）
$toRemove = @(
  'DetermineInitialThinkingMode','EffortOf','LabelOf','InjectThinkingMode','InjectNSlots',
  'PickFreePort','PickWarmSlot',
  'ReadRequestBodyAsync','BuildBackendRequest','WriteJsonAsync','WriteError',
  'IsInferenceRequest','IsChatCompletions','DetectToolLoop','PrefixHash','ContentLen','EnsureStreamTrue'
)

foreach ($name in $toRemove) {
  $removed = $true
  while ($removed) {
    $removed = $false
    for ($i=0; $i -lt $lines.Count; $i++) {
      if ($lines[$i] -match "^\s*(public|private|internal|protected)\s+static\s+(async\s+)?[\w<>,\[\]\.\?\s]+$name\s*\(") {
        # 回溯注释起点
        $cs = $i
        while ($cs -gt 0 -and ($lines[$cs-1] -match '^\s*///' -or $lines[$cs-1] -match '^\s*//')) { $cs-- }
        # 方法结束
        $end = $i
        if ($lines[$i] -match ';\s*$' -and $lines[$i] -notmatch '\{') {
          # 单行方法（含 ; 无 {）
          $end = $i
        } else {
          $depth = 0
          for ($j=$i; $j -lt $lines.Count; $j++) {
            foreach ($ch in $lines[$j].ToCharArray()) {
              if ($ch -eq '{') { $depth++ } elseif ($ch -eq '}') { $depth-- }
            }
            if ($depth -le 0 -and $j -gt $i) { $end = $j; break }
          }
        }
        # 删除 cs..end
        $new = @()
        for ($k=0; $k -lt $lines.Count; $k++) {
          if ($k -ge $cs -and $k -le $end) { continue }
          $new += $lines[$k]
        }
        $lines = $new
        $removed = $true
        Write-Host "[删除] $name (L$($cs+1)-L$($end+1))"
        break  # 同名重载：重新循环找下一个
      }
    }
  }
}

# 清理多余连续空行（真实换行）
$c = $lines -join "`r`n"
$c = [regex]::Replace($c, "(\r\n){3,}", "`r`n`r`n")

# ---------- 2. SmartScheduler.cs 内调用点替换（负向前瞻，避免重复前缀） ----------
$replaceMap = @{
  'DetermineInitialThinkingMode' = 'ThinkingMode'
  'EffortOf' = 'ThinkingMode'
  'LabelOf' = 'ThinkingMode'
  'InjectThinkingMode' = 'ThinkingMode'
  'InjectNSlots' = 'ThinkingMode'
  'PickFreePort' = 'SchedulerUtils'
  'PickWarmSlot' = 'SchedulerUtils'
  'ReadRequestBodyAsync' = 'RequestProcessor'
  'BuildBackendRequest' = 'RequestProcessor'
  'WriteJsonAsync' = 'RequestProcessor'
  'WriteError' = 'RequestProcessor'
  'IsInferenceRequest' = 'RequestProcessor'
  'IsChatCompletions' = 'RequestProcessor'
  'DetectToolLoop' = 'RequestProcessor'
  'PrefixHash' = 'RequestProcessor'
  'EnsureStreamTrue' = 'RequestProcessor'
}
foreach ($m in $replaceMap.Keys) {
  $pattern = "(?<![\w.])$m\("
  $repl = "$($replaceMap[$m]).$m("
  $before = ([regex]::Matches($c, $pattern)).Count
  if ($before -gt 0) {
    $c = [regex]::Replace($c, $pattern, $repl)
    Write-Host "[替换] $m × $before → $repl"
  }
}
[System.IO.File]::WriteAllText($sched, $c, [System.Text.UTF8Encoding]::new($false))
Write-Host ("SmartScheduler.cs 现 {0} 行" -f ($c -split "`r`n").Count)

# ---------- 3. 外部文件调用点替换 ----------
function FixFile([string]$rel, [hashtable]$map) {
  $p = Join-Path $base $rel
  $c = [System.IO.File]::ReadAllText($p)
  foreach ($k in $map.Keys) {
    if ($c.Contains($k)) { $c = $c.Replace($k, $map[$k]); Write-Host "[外部] ${rel}: $k → $($map[$k])" }
  }
  [System.IO.File]::WriteAllText($p, $c, [System.Text.UTF8Encoding]::new($false))
}
FixFile 'MainForm.cs' @{
  'SmartScheduler.LabelOf(' = 'ThinkingMode.LabelOf('
  'SmartScheduler.DetermineInitialThinkingMode(' = 'ThinkingMode.DetermineInitialThinkingMode('
}
FixFile 'StatusPanelView.cs' @{
  'SmartScheduler.LabelOf(' = 'ThinkingMode.LabelOf('
  'SmartScheduler.DetermineInitialThinkingMode(' = 'ThinkingMode.DetermineInitialThinkingMode('
}
$tests = 'C:\project\lunch\LlamaHarness.Tests'
function FixTest([string]$rel, [hashtable]$map) {
  $p = Join-Path $tests $rel
  $c = [System.IO.File]::ReadAllText($p)
  foreach ($k in $map.Keys) {
    if ($c.Contains($k)) { $c = $c.Replace($k, $map[$k]); Write-Host "[测试] ${rel}: $k → $($map[$k])" }
  }
  [System.IO.File]::WriteAllText($p, $c, [System.Text.UTF8Encoding]::new($false))
}
FixTest 'GatewayRewriteBaselineTests.cs' @{
  'SmartScheduler.EnsureStreamTrue(' = 'RequestProcessor.EnsureStreamTrue('
  'SmartScheduler.InjectNSlots(' = 'ThinkingMode.InjectNSlots('
  'SmartScheduler.InjectThinkingMode(' = 'ThinkingMode.InjectThinkingMode('
  'SmartScheduler.DetectToolLoop(' = 'RequestProcessor.DetectToolLoop('
}
FixTest 'PrefixFingerprintAndLogFileTests.cs' @{
  'SmartScheduler.PrefixHash(' = 'RequestProcessor.PrefixHash('
}
FixTest 'WarmingTests.cs' @{
  'SmartScheduler.PickWarmSlot(' = 'SchedulerUtils.PickWarmSlot('
}

Write-Host '步骤1 全部完成'
