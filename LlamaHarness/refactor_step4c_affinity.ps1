# 步骤4c：ApplySlotAffinityAsync 拆「Tool 链锁定」+「KV 驱逐 save/restore」两子段
$ErrorActionPreference = 'Stop'
$base = 'C:\project\lunch\LlamaHarness'

function Get-BraceEnd([string[]]$ls, [int]$decl) {
  $depth = 0; $started = $false
  for ($j=$decl; $j -lt $ls.Count; $j++) {
    $line = $ls[$j]; $i = 0; $n = $line.Length
    while ($i -lt $n) {
      $ch = $line[$i]
      if ($ch -eq '/' -and $i+1 -lt $n -and $line[$i+1] -eq '/') { break }
      if ($ch -eq '/' -and $i+1 -lt $n -and $line[$i+1] -eq '*') {
        $i += 2
        while ($i+1 -lt $n -and -not ($line[$i] -eq '*' -and $line[$i+1] -eq '/')) { $i++ }
        $i = [Math]::Min($i+2, $n); continue
      }
      if ($ch -eq "'") {
        $i++
        while ($i -lt $n) { if ($line[$i] -eq '\') { $i += 2; continue }; if ($line[$i] -eq "'") { $i++; break }; $i++ }
        continue
      }
      if ($ch -eq '@' -and $i+1 -lt $n -and $line[$i+1] -eq '"') {
        $i += 2
        while ($i -lt $n) { if ($line[$i] -eq '"' -and $i+1 -lt $n -and $line[$i+1] -eq '"') { $i += 2; continue }; if ($line[$i] -eq '"') { $i++; break }; $i++ }
        continue
      }
      if ($ch -eq '"') {
        $i++
        while ($i -lt $n) { if ($line[$i] -eq '\') { $i += 2; continue }; if ($line[$i] -eq '"') { $i++; break }; $i++ }
        continue
      }
      if ($ch -eq '{') { $depth++; $started = $true }
      elseif ($ch -eq '}') { $depth-- }
      $i++
    }
    if ($started -and $depth -le 0 -and $j -gt $decl) { return $j }
  }
  return -1
}

function Replace-Method([string]$path, [string]$methodName, [string]$newBlock) {
  $lines = [System.IO.File]::ReadAllLines($path)
  for ($i=0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match "^\s*(public|private|internal|protected)\s+(static\s+)?(async\s+)?.*?\b$([regex]::Escape($methodName))\s*\(") {
      $cs = $i
      while ($cs -gt 0 -and ($lines[$cs-1] -match '^\s*///' -or $lines[$cs-1] -match '^\s*//')) { $cs-- }
      $end = Get-BraceEnd $lines $i
      if ($end -lt 0) { throw "配平失败: $methodName" }
      $newLines = @()
      for ($k=0; $k -lt $lines.Count; $k++) {
        if ($k -ge $cs -and $k -le $end) { continue }
        $newLines += $lines[$k]
      }
      $out = @()
      for ($k=0; $k -lt $newLines.Count; $k++) {
        $out += $newLines[$k]
        if ($k -eq ($cs-1)) { $out += ($newBlock -split "`r`n") }
      }
      $content = ($out -join "`r`n")
      $content = [regex]::Replace($content, "(\r\n){3,}", "`r`n`r`n")
      [System.IO.File]::WriteAllText($path, $content, [System.Text.UTF8Encoding]::new($false))
      Write-Host "[替换] $(Split-Path $path -Leaf) : $methodName"
      return
    }
  }
  throw "未找到方法 $methodName in $path"
}

$affinityNew = @'
    /// <summary>槽位亲和阶段：指纹绑定（LRU 驱逐 / §4.2 自动强占）→ §4.5 Tool 链锁定 → 驱逐前 KV save → restore 自愈 → n_slots 注入。
    /// E-1：直接操作调用方持有的同一棵 DOM（root=null 时跳过 DOM 步骤，等价旧实现 parse 失败透传）。
    /// 返回（路由槽位、绑定 key、是否执行了 KV restore——restore 后需重跑 TokenGuard 校验）。</summary>
    private async Task<(int? RoutedSlot, string? RoutedKey, bool DidRestore)> ApplySlotAffinityAsync(
        HttpListenerRequest req, SlotAffinity aff, JsonObject? root)
    {
        // §4.2 自动冻结：应用类型前缀在 AutoPreemptiveApps → 绑定强制强占（暂停 LRU 驱逐）
        var autoPre = ParseAutoPreemptivePrefixes();
        var (slot, key, isNew, evicted, evictedSlot, evictedKvCache) = aff.GetSlot(req.Headers, autoPre);
        int? routedSlot = slot;
        string? routedKey = key;

        // ① §4.5 Tool 链会话锁定：末条消息 role=tool → 锁槽位防驱逐；循环结束自动解锁
        HandleToolLoopLock(aff, root, key, slot);

        var kv = _kvCache;

        // ② KV Cache 生命周期：驱逐前 save（仅被驱逐者 KvCache=true）→ restore 自愈（isNew 重绑定 / 进程重启后首次使用）
        bool didRestore = await HandleEvictAndRestoreAsync(kv, evicted, evictedSlot, evictedKvCache, key, slot, isNew);

        if (isNew)
        {
            var evt = $"槽位绑定：{key} → slot{slot}{(evicted != null ? $"（驱逐 {evicted}）" : "")}";
            EmitSlot(evt);
            SlotBindingChanged?.Invoke();
        }
        // E-1：n_slots 注入直接改树（已有 n_slots 时不覆盖，尊重客户端显式指定）
        if (root != null)
            ThinkingModeHelper.InjectNSlots(root, slot);
        return (routedSlot, routedKey, didRestore);
    }

    /// <summary>§4.5 Tool 链会话锁定（ApplySlotAffinityAsync 子段①）：末条消息 role=tool →
    /// 锁槽位防驱逐（强占），循环结束自动解锁。O-15：锁内只做 _toolLockedKeys 集合判定；
    /// aff 调用（自带内部锁 + 文件 I/O）全部移出，消除锁嵌套。</summary>
    private void HandleToolLoopLock(SlotAffinity aff, JsonObject? root, string? key, int slot)
    {
        if (key == null || root == null) return;
        bool inToolLoop = RequestProcessor.DetectToolLoop(root);
        bool didLock = false, didUnlock = false;
        bool alreadyPreemptive = aff.IsPreemptive(key);
        lock (_kvStateGate)
        {
            if (inToolLoop)
            {
                if (!_toolLockedKeys.Contains(key) && !alreadyPreemptive)
                {
                    _toolLockedKeys.Add(key);
                    didLock = true;
                }
            }
            else if (_toolLockedKeys.Remove(key))
            {
                didUnlock = true;
            }
        }
        if (didLock)
        {
            aff.MarkToolLocked(key); // 标记到 SlotAffinity（驱逐优先级：Tool 锁定 > 手动/自动强占）
            aff.SetPreemptive(key, true); // 移出锁外（O-15）
            EmitSlot($"[KV-LOCK] Tool 链会话锁定：{key} → slot{slot}（强占，不驱逐）");
        }
        else if (didUnlock)
        {
            aff.UnmarkToolLocked(key);
            aff.SetPreemptive(key, false);
            EmitSlot($"[KV-UNLOCK] Tool 链结束，解除锁定：{key}");
        }
    }

    /// <summary>KV Cache 生命周期（ApplySlotAffinityAsync 子段②）：驱逐前 save（仅被驱逐者 KvCache=true；
    /// evicted != null 已蕴含 evictedSlot 有效，SlotAffinity 仅驱逐时置位）→ restore 自愈
    /// （① isNew 重绑定；② 进程重启后该 key 首次使用——休眠唤醒 KV 自愈）。
    /// 无论是否命中 restore，都把 key 记入 _servedKeysThisRun：本进程服务过即不再 restore，防误用磁盘旧快照回退内存新状态。
    /// 返回是否执行了 KV restore（restore 后需重跑 TokenGuard 校验）。</summary>
    private async Task<bool> HandleEvictAndRestoreAsync(KvCacheManager? kv, string? evicted, int evictedSlot, bool evictedKvCache, string? key, int slot, bool isNew)
    {
        // 驱逐前 save（仅当被驱逐者的 KvCache=true）
        if (evicted != null && kv != null && evictedKvCache)
        {
            try
            {
                var saveTask = kv.SaveAsync(evictedSlot, evicted);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await saveTask;
                EmitSlot($"KV Cache 保存：{evicted} → slot{evictedSlot}（{sw.Elapsed.TotalSeconds:F1}s）");
            }
            catch (Exception ex)
            {
                EmitSlot($"KV Cache 保存失败：{evicted}（{ex.Message}），降级为全量 prefill。");
            }
        }
        else if (evicted != null && !evictedKvCache)
        {
            EmitSlot($"驱逐 {evicted}（KV Cache 已关闭，不保存）");
        }

        // restore
        bool didRestore = false;
        if (key != null)
        {
            bool firstUseThisRun;
            lock (_kvStateGate) firstUseThisRun = _servedKeysThisRun.Add(key);
            if (kv != null && kv.HasCache(key) && (isNew || firstUseThisRun))
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    bool ok = await kv.RestoreAsync(slot, key);
                    if (ok)
                    {
                        EmitSlot($"[KV-RESTORE] KV Cache 恢复：{key} → slot{slot}（{sw.Elapsed.TotalSeconds:F1}s，跳过全量 prefill）");
                        // §8：restore 后重建前缀哈希基线（旧哈希对应驱逐前状态，避免下次请求误报 MISS）
                        lock (_kvStateGate) _prefixHashes.Remove(key);
                        didRestore = true; // restore 成功：标记需重跑 TokenGuard（saved_n 残留 + 新 prompt 叠加可能击穿窗口）
                    }
                    else
                    {
                        EmitSlot($"KV Cache 恢复失败：{key}（槽位可能忙），降级为全量 prefill。");
                    }
                }
                catch (Exception ex)
                {
                    EmitSlot($"KV Cache 恢复异常：{key}（{ex.Message}），降级为全量 prefill。");
                }
            }
        }
        return didRestore;
    }
'@
Replace-Method (Join-Path $base 'SmartScheduler.Gateway.cs') 'ApplySlotAffinityAsync' $affinityNew
