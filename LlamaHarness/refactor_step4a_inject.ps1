# 步骤4：剩余大方法继续拆小（InjectThinkingMode / WakeUpAsync / ApplySlotAffinityAsync）
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

# ================= 1. ThinkingMode.cs：InjectThinkingMode =================
$injectNew = @'
    /// <summary>
    /// 思考模式拦截与注入（仅 chat/completions POST 请求体）：
    /// 1. 检测 messages 数组最后一条 user 消息是否含思考/推理指令：
    ///    - 「开启思考模式」→ XHigh（未指定深度时默认深度档）；
    ///    - 「关闭思考模式」→ Off；
    ///    - 「开启轻度推理模式」→ Low；「开启中度推理模式」→ Medium；「开启深度推理模式」→ XHigh。
    ///    命中 → 设置全局档位，剥离指令文本（避免模型把指令当问题回答）。
    /// 2. 统一清洗：移除请求体中客户端自带的 chat_template_kwargs.reasoning_effort / enable_thinking
    ///    （网关代理层统一管控思考参数，不信任客户端自行携带的值）。
    /// 3. 按状态机注入：Off → 显式 enable_thinking=false（Qwen3 混合思考模型默认会思考，
    ///    不显式关闭则仍输出 reasoning_content，导致下游 pi-ai 严格 JSON.parse 报 PI_AI_ERROR）；
    ///    Low/Medium/XHigh → 注入对应 reasoning_effort + enable_thinking=true。
    /// E-1 DOM 版：原地改树，复用调用方持有的同一棵树（无 parse/serialize）。
    /// </summary>
    /// <param name="obj">请求体 DOM（入口一次性解析）</param>
    /// <param name="level">当前全局思考档位（ref：指令命中时更新）</param>
    /// <param name="effortFix">清洗/注入描述（如 "已清洗客户端 reasoning_effort=high"）；null = 无需说明</param>
    public static void InjectThinkingMode(JsonObject obj, ref ThinkingLevel level, out string? effortFix)
    {
        effortFix = null;
        // 注意：不再有无改动的快速路径——Off 态也必须显式注入 enable_thinking=false，
        // 否则 Qwen3 混合思考模型默认仍会输出 reasoning_content（实测 REASONING_LEN≈5800），
        // 思考文本混入 tool-call JSON 后导致 pi-ai 严格 JSON.parse 报 PI_AI_ERROR。
        try
        {
            // ① 末条 user 指令扫描与剥离（命中 → 更新档位 + 剥指令文本）
            ScanAndStripDirective(obj, ref level);

            // ② 统一清洗：移除客户端自带的思考相关字段（网关层统一管控）
            bool cleaned = CleanClientThinkingFields(obj);

            // ③ 按状态机注入：Off → enable_thinking=false；Low/Medium/XHigh → reasoning_effort + enable_thinking=true
            ApplyThinkingState(obj, level, cleaned, ref effortFix);
        }
        catch
        {
            // 结构异常：尽力而为，保留已完成的改写（等价旧实现透传语义）
        }
    }

    /// <summary>末条 user 指令扫描与剥离（InjectThinkingMode 子段①）：
    /// 「开启思考模式」→ XHigh；「关闭思考模式」→ Off；「开启轻度/中度/深度推理模式」→ Low/Medium/XHigh。
    /// 命中 → 更新档位 ref，剥离指令文本（避免模型把指令当问题回答）。</summary>
    private static void ScanAndStripDirective(JsonObject obj, ref ThinkingLevel level)
    {
        if (obj["messages"] is System.Text.Json.Nodes.JsonArray msgs && msgs.Count > 0)
        {
            for (int i = msgs.Count - 1; i >= 0; i--)
            {
                if (msgs[i] is not System.Text.Json.Nodes.JsonObject msgObj) continue;
                // role 提取：JsonNode.ToString() 对字符串节点返回不带引号的原始值
                string? roleStr = msgObj["role"]?.ToString();
                if (!string.Equals(roleStr, "user", StringComparison.OrdinalIgnoreCase)) break;

                // content 提取：仅处理字符串类型（数组/对象跳过）
                var contentNode = msgObj["content"];
                if (contentNode == null) continue;
                // AsObject() 对非对象节点抛异常，用 try-catch 安全判断
                bool isContainer = false;
                try { isContainer = contentNode.AsObject() != null || contentNode.AsArray() != null; } catch { }
                if (isContainer) continue;
                string contentStr = contentNode.ToString();

                bool hitOn = contentStr.Contains("开启思考模式");
                bool hitOff = contentStr.Contains("关闭思考模式");
                bool hitLow = contentStr.Contains("开启轻度推理模式");
                bool hitMid = contentStr.Contains("开启中度推理模式");
                bool hitDeep = contentStr.Contains("开启深度推理模式");
                if (!hitOn && !hitOff && !hitLow && !hitMid && !hitDeep) continue;

                // 剥离全部命中指令，保留其余内容；若消息只剩指令本身，填确认提示避免空消息让模型困惑
                string stripped = contentStr;
                if (hitOn) { level = ThinkingLevel.XHigh; stripped = stripped.Replace("开启思考模式", ""); }
                if (hitOff) { level = ThinkingLevel.Off; stripped = stripped.Replace("关闭思考模式", ""); }
                if (hitLow) { level = ThinkingLevel.Low; stripped = stripped.Replace("开启轻度推理模式", ""); }
                if (hitMid) { level = ThinkingLevel.Medium; stripped = stripped.Replace("开启中度推理模式", ""); }
                if (hitDeep) { level = ThinkingLevel.XHigh; stripped = stripped.Replace("开启深度推理模式", ""); }
                msgObj["content"] = string.IsNullOrWhiteSpace(stripped.Trim())
                    ? "（思考/推理模式已切换，请简短确认）"
                    : stripped.Trim();
                break;
            }
        }
    }

    /// <summary>统一清洗客户端自带的思考字段（InjectThinkingMode 子段②）：
    /// 顶层 "thinking"/"reasoning_effort" + chat_template_kwargs 内 reasoning_effort/enable_thinking；
    /// 清洗后空壳 chat_template_kwargs 一并移除（避免下发无意义字段）。返回是否有清洗动作。</summary>
    private static bool CleanClientThinkingFields(JsonObject obj)
    {
        // DSH 客户端发送的思考字段：顶层 "thinking" / "reasoning_effort" + chat_template_kwargs 内字段
        bool cleaned = false;
        // 顶层字段（DSH 格式）
        if (obj.Remove("thinking")) cleaned = true;
        if (obj.Remove("reasoning_effort")) cleaned = true;
        // chat_template_kwargs 内字段（部分客户端格式）
        if (obj["chat_template_kwargs"] is System.Text.Json.Nodes.JsonObject ctkExisting)
        {
            if (ctkExisting.Remove("reasoning_effort")) cleaned = true;
            if (ctkExisting.Remove("enable_thinking")) cleaned = true;
            // 清洗后若 chat_template_kwargs 为空对象，移除空壳（避免下发无意义字段）
            if (ctkExisting.Count == 0) obj.Remove("chat_template_kwargs");
        }
        return cleaned;
    }

    /// <summary>按状态机注入（InjectThinkingMode 子段③）：Off → 显式 enable_thinking=false；
    /// Low/Medium/XHigh → reasoning_effort + enable_thinking=true；有清洗则写入 effortFix 说明。</summary>
    private static void ApplyThinkingState(JsonObject obj, ThinkingLevel level, bool cleaned, ref string? effortFix)
    {
        // 按状态机注入：Off → 显式 enable_thinking=false；Low/Medium/XHigh → reasoning_effort + enable_thinking=true
        System.Text.Json.Nodes.JsonObject ctk;
        if (obj["chat_template_kwargs"] is System.Text.Json.Nodes.JsonObject existing)
        {
            ctk = existing;
        }
        else
        {
            ctk = new System.Text.Json.Nodes.JsonObject();
            obj["chat_template_kwargs"] = ctk;
        }
        if (level == ThinkingLevel.Off)
        {
            ctk["enable_thinking"] = false; // 关键：混合思考模型必须显式关闭，否则默认仍思考
        }
        else
        {
            ctk["reasoning_effort"] = EffortOf(level);
            ctk["enable_thinking"] = true;
        }

        // 清洗说明（用于日志）
        if (cleaned)
            effortFix = "已清洗客户端思考参数（thinking/reasoning_effort/enable_thinking），按网关状态机重新注入";
    }
'@
Replace-Method (Join-Path $base 'ThinkingMode.cs') 'InjectThinkingMode' $injectNew
