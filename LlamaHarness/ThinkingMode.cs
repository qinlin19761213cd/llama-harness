using System.Text.Json.Nodes;

// ThinkingLevel 枚举仍定义于 SmartScheduler（公开 API 不变）；本文件用类型别名裸写该嵌套枚举。
using ThinkingLevel = LlamaHarness.SmartScheduler.ThinkingLevel;

namespace LlamaHarness;

/// <summary>
/// 思考模式三档状态机的纯静态逻辑（零实例依赖，可独立单测）：
/// 启动参数基线判定 / 档位标签 / reasoning_effort 映射 / 请求体思考指令拦截与注入 / n_slots 注入。
/// 原属 SmartScheduler（v2.15 重构迁出），方法体逐字迁移，行为等价。
/// </summary>
public static class ThinkingMode
{
    /// <summary>按启动附加参数判定初始思考档位：
    /// --reasoning on → XHigh（深度推理）；--reasoning off 或无该参数 → Off（默认不思考）。
    /// 注意：仅显式 on 才开启思考，避免默认注入深度思考干扰严格 JSON 类请求（如意图分类器）。</summary>
    public static ThinkingLevel DetermineInitialThinkingMode(string extraArgs)
    {
        if (string.IsNullOrWhiteSpace(extraArgs)) return ThinkingLevel.Off; // 无参数 = 默认不思考
        var m = System.Text.RegularExpressions.Regex.Match(
            extraArgs,
            @"--reasoning\s+(on|off)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return (m.Success && m.Groups[1].Value.Equals("on", StringComparison.OrdinalIgnoreCase))
            ? ThinkingLevel.XHigh
            : ThinkingLevel.Off;
    }

    /// <summary>档位 → reasoning_effort 值；Off 返回 null（不注入）。</summary>
    public static string? EffortOf(ThinkingLevel lvl) => lvl switch
    {
        ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        ThinkingLevel.XHigh => "xhigh",
        _ => null,
    };

    /// <summary>档位显示名。</summary>
    public static string LabelOf(ThinkingLevel lvl) => lvl switch
    {
        ThinkingLevel.Off => "极速",
        ThinkingLevel.Low => "轻度推理",
        ThinkingLevel.Medium => "中度推理",
        _ => "深度推理",
    };

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

            // 2. 统一清洗：移除客户端自带的思考相关字段（网关层统一管控）
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

            // 3. 按状态机注入：Off → 显式 enable_thinking=false；Low/Medium/XHigh → reasoning_effort + enable_thinking=true
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
        catch
        {
            // 结构异常：尽力而为，保留已完成的改写（等价旧实现透传语义）
        }
    }

    /// <summary>注入 n_slots 固定槽位路由（llama.cpp 多槽特性）。E-1 DOM 版：原地改树。
    /// 已有 n_slots 时不覆盖（尊重客户端显式指定），返回 false。</summary>
    public static bool InjectNSlots(JsonObject obj, int slot)
    {
        if (obj["n_slots"] != null) return false;
        obj["n_slots"] = new System.Text.Json.Nodes.JsonArray(slot);
        return true;
    }
}
