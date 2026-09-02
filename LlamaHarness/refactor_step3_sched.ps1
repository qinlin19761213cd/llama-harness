# 步骤3：SmartScheduler.cs partial 文件聚类（方法体零改动纯搬移）
$ErrorActionPreference = 'Stop'
$base = 'C:\project\lunch\LlamaHarness'
$sched = Join-Path $base 'SmartScheduler.cs'
$lines = [System.IO.File]::ReadAllLines($sched)

# ---- 方法名 → 目标文件映射（保持原行号顺序；IsInferenceRequest/IsChatCompletions 已迁走）----
$map = [ordered]@{
  'StartListening'              = 'Http'
  'StopListening'               = 'Http'
  'AcceptLoopAsync'             = 'Http'
  'HandleRequestAsync'          = 'Http'
  'WarnNonStreamOnce'           = 'Http'
  'EnsureRunningAsync'          = 'Lifecycle'
  'WakeUpAsync'                 = 'Lifecycle'
  'WaitReadyAsync'              = 'Lifecycle'
  'RunWarmingAsync'             = 'Lifecycle'
  'SleepNow'                    = 'Lifecycle'
  'SleepNowCoreAsync'           = 'Lifecycle'
  'SaveAllSlotsBeforeStopAsync' = 'Lifecycle'
  'StopNow'                     = 'Lifecycle'
  'OnServerExited'              = 'Lifecycle'
  'VerifyVramReleasedAsync'     = 'Lifecycle'
  'OnTick'                      = 'Lifecycle'
  'SetPhase'                    = 'Lifecycle'
  'Dispose'                     = 'Lifecycle'
  'ForwardAsync'                = 'Pipeline'
  'SendAndPipeAsync'            = 'Pipeline'
  'TryConnectWithRetryAsync'    = 'Pipeline'
  'TryRecoverContextOverflowAsync' = 'Pipeline'
  'PumpResponseAsync'           = 'Pipeline'
  'PipeResponseAsync'           = 'Pipeline'
  'DumpRequest'                 = 'Pipeline'
  'PrepareGatewayAsync'         = 'Gateway'
  'ApplySlotAffinityAsync'      = 'Gateway'
  'ParseAutoPreemptivePrefixes' = 'Gateway'
  'IsAutoPreKey'                = 'Gateway'
  'ParseAutoSnapshotPrefixes'   = 'Gateway'
  'IsAutoSnapshotKey'           = 'Gateway'
  'LogPrefixHash'               = 'Gateway'
  'TryCrashRecoverAsync'        = 'Crash'
  'RecoverAliveAsync'           = 'Crash'
  'RunCrashRecoveryAsync'       = 'Crash'
  'RestartAndReplayAsync'       = 'Crash'
  'ProbeClientConnectedAsync'   = 'Crash'
  'RunKeepAliveAsync'           = 'Crash'
}

# ---- 字符串感知括号配平（兼容跨行声明、字符/字符串/verbatim/插值/注释内的括号）----
function Get-BraceEnd([string[]]$ls, [int]$decl) {
  $depth = 0; $started = $false
  for ($j=$decl; $j -lt $ls.Count; $j++) {
    $line = $ls[$j]; $i = 0; $n = $line.Length
    while ($i -lt $n) {
      $ch = $line[$i]
      # 行注释
      if ($ch -eq '/' -and $i+1 -lt $n -and $line[$i+1] -eq '/') { break }
      # 块注释
      if ($ch -eq '/' -and $i+1 -lt $n -and $line[$i+1] -eq '*') {
        $i += 2
        while ($i+1 -lt $n -and -not ($line[$i] -eq '*' -and $line[$i+1] -eq '/')) { $i++ }
        $i = [Math]::Min($i+2, $n); continue
      }
      # 字符字面量 '...'
      if ($ch -eq "'") {
        $i++
        while ($i -lt $n) {
          if ($line[$i] -eq '\') { $i += 2; continue }
          if ($line[$i] -eq "'") { $i++; break }
          $i++
        }
        continue
      }
      # verbatim 字符串 @"..." 或 @$"..."（先遇 @ 后 "
      if ($ch -eq '@' -and $i+1 -lt $n -and $line[$i+1] -eq '"') {
        $i += 2
        while ($i -lt $n) {
          if ($line[$i] -eq '"' -and $i+1 -lt $n -and $line[$i+1] -eq '"') { $i += 2; continue }
          if ($line[$i] -eq '"') { $i++; break }
          $i++
        }
        continue
      }
      # 普通/插值字符串 "..."（$"..." 先遇 $ 跳过再遇 " 进此分支）
      if ($ch -eq '"') {
        $i++
        while ($i -lt $n) {
          if ($line[$i] -eq '\') { $i += 2; continue }
          if ($line[$i] -eq '"') { $i++; break }
          $i++
        }
        continue
      }
      # 大括号
      if ($ch -eq '{') { $depth++; $started = $true }
      elseif ($ch -eq '}') { $depth-- }
      $i++
    }
    if ($started -and $depth -le 0 -and $j -gt $decl) { return $j }
  }
  return -1
}

# ---- 按行号顺序提取各方法块 ----
# 先定位所有方法声明（含行号），按方法名映射目标
$fileBlocks = @{ 'Http'=@(); 'Gateway'=@(); 'Pipeline'=@(); 'Lifecycle'=@(); 'Crash'=@() }
$keepLines = New-Object System.Collections.Generic.List[int]  # 壳保留的行索引

# 标记需要迁移的行区间
$removed = @{}
foreach ($name in $map.Keys) { $removed[$name] = $null }  # 初始化占位

# 扫描方法声明（按方法名精确匹配声明行）
$found = @{}  # name -> (start, end)
foreach ($name in $map.Keys) {
  for ($i=0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match "^\s*(public|private|internal|protected)\s+(static\s+)?(async\s+)?.*?\b$([regex]::Escape($name))\s*\(") {
      # 回溯注释起点
      $cs = $i
      while ($cs -gt 0 -and ($lines[$cs-1] -match '^\s*///' -or $lines[$cs-1] -match '^\s*//')) { $cs-- }
      $end = Get-BraceEnd $lines $i
      if ($end -lt 0) { throw "方法 $name 配平失败" }
      $found[$name] = @($cs, $end)
      break
    }
  }
  if (-not $found.ContainsKey($name)) { Write-Host "[警告] 未找到方法: $name" }
}

# 按原文件行号顺序输出各文件块
$orderedMethods = @()
foreach ($name in $map.Keys) {
  if ($found.ContainsKey($name)) { $orderedMethods += $name }
}
# 按开始行号排序
$orderedMethods = $orderedMethods | Sort-Object { $found[$_][0] }

# 生成各文件方法块文本（保留原顺序）
foreach ($name in $orderedMethods) {
  $cs = $found[$name][0]; $end = $found[$name][1]
  $target = $map[$name]
  $block = $lines[$cs..$end] -join "`r`n"
  $fileBlocks[$target] += $block
  # 记录迁移区间
  $removed[$name] = @($cs, $end)
}

# ---- 生成 5 个 partial 文件 ----
$header = @(
'using System.Net;',
'using System.Net.Http;',
'using System.Net.Http.Headers;',
'using System.Text;',
'using System.Text.Json.Nodes;',
'using ThinkingModeHelper = LlamaHarness.ThinkingMode;',
'',
'namespace LlamaHarness;'
) -join "`r`n"

$desc = @{
  'Http' = 'HTTP 监听与请求接收（StartListening/StopListening/AcceptLoopAsync/HandleRequestAsync/WarnNonStreamOnce）。partial 聚类方法体零改动。'
  'Gateway' = '网关路由与槽位亲和（PrepareGatewayAsync/ApplySlotAffinityAsync/自动预取与快照前缀解析/前缀指纹日志）。partial 聚类方法体零改动。'
  'Pipeline' = '请求转发与响应管道（ForwardAsync/SendAndPipeAsync 及其子流程/PipeResponseAsync/DumpRequest）。partial 聚类方法体零改动。'
  'Lifecycle' = '生命周期与状态机（EnsureRunningAsync/WakeUpAsync/WaitReadyAsync/RunWarmingAsync/闲置休眠/停止/释放/SetPhase/Dispose）。partial 聚类方法体零改动。'
  'Crash' = '崩溃恢复协调（RunCrashRecoveryAsync/RestartAndReplayAsync/ProbeClientConnectedAsync/RunKeepAliveAsync/TryCrashRecoverAsync）。partial 聚类方法体零改动。'
}
foreach ($f in @('Http','Gateway','Pipeline','Lifecycle','Crash')) {
  $body = ($fileBlocks[$f] -join "`r`n`r`n")
  $content = $header + "`r`n`r`n/// <summary>`r`n/// $($desc[$f])`r`n/// </summary>`r`npublic partial class SmartScheduler`r`n{`r`n" + $body + "`r`n}"
  $fp = Join-Path $base ("SmartScheduler.$f.cs")
  [System.IO.File]::WriteAllText($fp, $content, [System.Text.UTF8Encoding]::new($false))
  Write-Host "[生成] $f.cs（$($fileBlocks[$f].Count) 个方法块）"
}

# ---- 主文件：改 partial + 保留壳（删除迁移区间）----
$removedRanges = @()
foreach ($name in $map.Keys) {
  if ($removed[$name] -ne $null) { $removedRanges += ,$removed[$name] }
}
$newLines = @()
for ($k=0; $k -lt $lines.Count; $k++) {
  $inRemoved = $false
  foreach ($r in $removedRanges) {
    if ($k -ge $r[0] -and $k -le $r[1]) { $inRemoved = $true; break }
  }
  if (-not $inRemoved) { $newLines += $lines[$k] }
}
$c = ($newLines -join "`r`n")
$c = $c.Replace('public class SmartScheduler', 'public partial class SmartScheduler')
$c = [regex]::Replace($c, "(\r\n){3,}", "`r`n`r`n")
[System.IO.File]::WriteAllText($sched, $c, [System.Text.UTF8Encoding]::new($false))
Write-Host ('[壳] SmartScheduler.cs 现 {0} 行' -f ($c -split "`r`n").Count)
Write-Host '步骤3 完成'
