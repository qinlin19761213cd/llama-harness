# 步骤2：SendAndPipeAsync 拆分子流程（TryConnectWithRetryAsync / TryRecoverContextOverflowAsync / PumpResponseAsync）
$ErrorActionPreference = 'Stop'
$p = 'C:\project\lunch\LlamaHarness\SmartScheduler.cs'
$lines = [System.IO.File]::ReadAllLines($p)

# ---- 定位 SendAndPipeAsync 块：注释起点(0-based L896) 到方法结束(配平) ----
$start = -1
for ($i=0; $i -lt $lines.Count; $i++) {
  if ($lines[$i] -match '^\s*/// <summary>转发阶段') { $start = $i; break }
}
if ($start -lt 0) { throw '未找到 SendAndPipeAsync 注释' }
# 方法声明行
$decl = -1
for ($i=$start; $i -lt $lines.Count; $i++) {
  if ($lines[$i] -match 'private async Task SendAndPipeAsync') { $decl = $i; break }
}
# 配平找方法结束（先等第一个 '{' 出现再计数，兼容跨行声明）
$depth = 0; $end = -1; $started = $false
for ($j=$decl; $j -lt $lines.Count; $j++) {
  foreach ($ch in $lines[$j].ToCharArray()) {
    if ($ch -eq '{') { $depth++; $started = $true }
    elseif ($ch -eq '}') { $depth-- }
  }
  if ($started -and $depth -le 0 -and $j -gt $decl) { $end = $j; break }
}
if ($end -lt 0) { throw '方法结束配平失败' }
Write-Host "SendAndPipeAsync 块 L$($start+1)-L$($end+1)（共 $($end-$start+1) 行）"

# ---- 提取三个子区段（1-based 行号 → 0-based index-1）----
# 区段1：连接重试 L904-915（保持缩进，加 return resp）
$segConn = @()
for ($k=903; $k -le 914; $k++) { $segConn += $lines[$k] }
# 区段2：400 自愈 L923-997（去4空格缩进，return;→return true;）
$segOver = @()
for ($k=922; $k -le 996; $k++) {
  $s = $lines[$k]
  if ($s.Length -ge 4 -and $s.Substring(0,4) -eq '    ') { $s = $s.Substring(4) }
  $segOver += $s
}
$segOver = $segOver -join "`r`n"
$segOver = [regex]::Replace($segOver, '(?m)^(\s*)return;\s*$', '$1return true;')
# 区段3：响应管道 L999-1093（去4空格缩进）
$segPump = @()
for ($k=998; $k -le 1092; $k++) {
  $s = $lines[$k]
  if ($s.Length -ge 4 -and $s.Substring(0,4) -eq '    ') { $s = $s.Substring(4) }
  $segPump += $s
}

# ---- 构造新块 ----
$nl = "`r`n"
$newBlock = @(
'    /// <summary>转发阶段：构造后端请求（过滤逐跳头）→ 连接异常 500ms 重试一次 → 400 上下文超限自愈 → 响应管道（崩溃恢复/断点快照清理/客户端断开兜底）。</summary>',
'    private async Task SendAndPipeAsync(',
'        HttpListenerContext ctx, Uri uri, string path, HttpListenerRequest req,',
'        byte[]? bodyBytes, string? finalBody, bool effStreaming, int? routedSlot, string? routedKey, JsonObject? root)',
'    {',
'        using var msg = RequestProcessor.BuildBackendRequest(req, uri, bodyBytes);',
'',
'        HttpResponseMessage resp = await TryConnectWithRetryAsync(msg);',
'        using (resp)',
'        {',
'            var outResp = ctx.Response;',
'',
'            // 400 上下文超限自愈（激进裁剪 + KV 废弃 + 重发）；已处理则返回',
'            if (await TryRecoverContextOverflowAsync(resp, outResp, req, uri, path, root, finalBody, effStreaming, routedSlot, routedKey))',
'                return;',
'',
'            // 响应管道 + 崩溃恢复 + 断点快照清理 + 存档（含客户端断开兜底）',
'            await PumpResponseAsync(resp, outResp, uri, path, finalBody, effStreaming, routedSlot, routedKey);',
'        }',
'    }',
'',
'    /// <summary>连接异常 500ms 重试一次：后端刚重启/连接被重置时稍等重发（SendAndPipeAsync 子流程①）。</summary>',
'    private async Task<HttpResponseMessage> TryConnectWithRetryAsync(HttpRequestMessage msg)',
'    {'
) -join $nl
$newBlock += $nl + (($segConn -join $nl) + $nl + '        return resp;' + $nl + '    }')

$newBlock += $nl + $nl + (@(
'    /// <summary>400 上下文超限自愈（SendAndPipeAsync 子流程②）：读取 errBody → TokenGuard 激进裁剪 → KV 废弃 → 重发。',
'    /// 前置 TokenGuard 是快速预估（BuildMessagesText 不含 tools/Jinja 模板），ReservedPromptOverhead 预留不足时仍可能击穿；',
'    /// 此分支是最后一道防线。返回 true = 已处理（调用方应 return）；false = 未触发自愈（继续正常流程）。</summary>',
'    private async Task<bool> TryRecoverContextOverflowAsync(',
'        HttpResponseMessage resp, HttpListenerResponse outResp, HttpListenerRequest req, Uri uri,',
'        string path, JsonObject? root, string? finalBody, bool effStreaming, int? routedSlot, string? routedKey)',
'    {',
'        if (resp.StatusCode != System.Net.HttpStatusCode.BadRequest || !RequestProcessor.IsChatCompletions(path) || root == null || finalBody == null)',
'            return false;'
) -join $nl)
$newBlock += $nl + $segOver + $nl + '        return false;' + $nl + '    }'

$newBlock += $nl + $nl + (@(
'    /// <summary>响应管道编排（SendAndPipeAsync 子流程③）：设置响应头 → PipeResponseAsync（输出续接/崩溃识别）',
'    /// → 崩溃恢复（keep-alive 保活 + KV 快照接续/全量重放）→ 续接成功清理过期断点快照',
'    /// → 首请求存档 + 每轮条件式后台 save；含客户端断开兜底（catch）与响应关闭（finally）。</summary>',
'    private async Task PumpResponseAsync(',
'        HttpResponseMessage resp, HttpListenerResponse outResp, Uri uri, string path,',
'        string? finalBody, bool effStreaming, int? routedSlot, string? routedKey)',
'    {'
) -join $nl)
$newBlock += $nl + ($segPump -join $nl) + $nl + '    }'

# ---- 替换原块 ----
$newLines = @()
for ($k=0; $k -lt $lines.Count; $k++) {
  if ($k -ge $start -and $k -le $end) { continue }
  $newLines += $lines[$k]
}
# 在替换位置插入新块：先找到 start 之前的空行作为锚（保留一个空行分隔）
$out = @()
$inserted = $false
for ($k=0; $k -lt $newLines.Count; $k++) {
  $out += $newLines[$k]
  if (-not $inserted -and $k -eq ($start-1)) {
    # start 原位置前一行是空行，已保留；这里在它之后插入新块
    $out += ($newBlock -split "`r`n")
    $inserted = $true
  }
}
# 处理边界：若 start-1 不是空行则补空行
$content = ($out -join $nl)
$content = $content -replace "(\r\n){3,}", "`r`n`r`n"
[System.IO.File]::WriteAllText($p, $content, [System.Text.UTF8Encoding]::new($false))
Write-Host ('SmartScheduler.cs 现 {0} 行' -f ($content -split "`r`n").Count)
Write-Host '步骤2 提取完成'
