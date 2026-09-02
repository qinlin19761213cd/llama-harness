using System.Text.Json.Nodes;
using LlamaHarness;
using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// 网关改写函数行为基线（批次 1 后指向 DOM 版）：
/// 锁定 InjectThinkingMode / InjectNSlots / EnsureStreamTrue / DetectToolLoop 的输入→输出行为，
/// 保证 E-1 单 DOM 管道重构前后行为等价。
/// </summary>
public class GatewayRewriteBaselineTests
{
    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    // ---------- EnsureStreamTrue（DOM 版 + 字符串降级版） ----------

    [Fact]
    public void EnsureStreamTrue_Dom_SetsStreamTrue()
    {
        var obj = Parse(@"{""model"":""m"",""stream"":false}");
        RequestProcessor.EnsureStreamTrue(obj);
        Assert.True(obj["stream"]!.GetValue<bool>());
    }

    [Fact]
    public void EnsureStreamTrue_StringFallback_FalseToTrue()
    {
        var result = RequestProcessor.EnsureStreamTrue(@"{""model"":""m"",""stream"":false,""messages"":[]}");
        Assert.NotNull(result);
        Assert.True(Parse(result!)["stream"]!.GetValue<bool>());
    }

    [Fact]
    public void EnsureStreamTrue_StringFallback_NoFieldInjects()
    {
        var result = RequestProcessor.EnsureStreamTrue(@"{""model"":""m""}");
        Assert.NotNull(result);
        Assert.True(Parse(result!)["stream"]!.GetValue<bool>());
    }

    // ---------- InjectNSlots（DOM 版） ----------

    [Fact]
    public void InjectNSlots_AddsWhenMissing()
    {
        var obj = Parse(@"{""messages"":[]}");
        Assert.True(ThinkingMode.InjectNSlots(obj, 3));
        Assert.Equal(3, obj["n_slots"]![0].AsValue().GetValue<int>());
    }

    [Fact]
    public void InjectNSlots_RespectsExistingClientValue()
    {
        var obj = Parse(@"{""n_slots"":[1],""messages"":[]}");
        Assert.False(ThinkingMode.InjectNSlots(obj, 3)); // 已有 n_slots：不覆盖
        Assert.Equal(1, obj["n_slots"]![0].AsValue().GetValue<int>());
    }

    // ---------- InjectThinkingMode（DOM 版） ----------

    [Fact]
    public void InjectThinkingMode_OffStateInjectsEnableThinkingFalse()
    {
        var obj = Parse(@"{""messages"":[{""role"":""user"",""content"":""hello""}]}");
        var level = SmartScheduler.ThinkingLevel.Off;
        ThinkingMode.InjectThinkingMode(obj, ref level, out _);
        var ctk = obj["chat_template_kwargs"]!.AsObject();
        Assert.False(ctk["enable_thinking"]!.GetValue<bool>());
    }

    [Fact]
    public void InjectThinkingMode_OnInstructionSwitchesToXHighAndStripsText()
    {
        var obj = Parse(@"{""messages"":[{""role"":""user"",""content"":""请帮我开启思考模式并分析这个问题""}]}");
        var level = SmartScheduler.ThinkingLevel.Off;
        ThinkingMode.InjectThinkingMode(obj, ref level, out _);
        Assert.Equal(SmartScheduler.ThinkingLevel.XHigh, level);
        var content = obj["messages"]![0]!.AsObject()["content"]!.GetValue<string>();
        Assert.DoesNotContain("开启思考模式", content);
        var ctk = obj["chat_template_kwargs"]!.AsObject();
        Assert.True(ctk["enable_thinking"]!.GetValue<bool>());
        Assert.Equal("xhigh", ctk["reasoning_effort"]!.GetValue<string>());
    }

    [Fact]
    public void InjectThinkingMode_CleansClientReasoningEffort()
    {
        var obj = Parse(@"{""reasoning_effort"":""high"",""messages"":[{""role"":""user"",""content"":""hi""}]}");
        var level = SmartScheduler.ThinkingLevel.Off;
        ThinkingMode.InjectThinkingMode(obj, ref level, out string? fix);
        Assert.Null(obj["reasoning_effort"]); // 客户端自带字段被清洗
        Assert.NotNull(fix);                    // 有清洗说明
    }

    [Fact]
    public void InjectThinkingMode_ArrayContentSkipped()
    {
        // 数组型 content（多模态）：不识别指令、不改写
        var obj = Parse(@"{""messages"":[{""role"":""user"",""content"":[{""type"":""text"",""text"":""开启思考模式""}]}]}");
        var level = SmartScheduler.ThinkingLevel.Off;
        ThinkingMode.InjectThinkingMode(obj, ref level, out _);
        Assert.Equal(SmartScheduler.ThinkingLevel.Off, level); // 未切换
    }

    // ---------- DetectToolLoop（DOM 版） ----------

    [Fact]
    public void DetectToolLoop_LastMessageRoleTool_ReturnsTrue()
    {
        var obj = Parse(@"{""messages"":[{""role"":""user"",""content"":""q""},{""role"":""tool"",""content"":""r""}]}");
        Assert.True(RequestProcessor.DetectToolLoop(obj));
    }

    [Fact]
    public void DetectToolLoop_HistoryHasToolButLastIsAssistant_ReturnsFalse()
    {
        // 历史残留 tool 消息不作为依据（防循环结束后永久误锁）
        var obj = Parse(@"{""messages"":[{""role"":""tool"",""content"":""r""},{""role"":""assistant"",""content"":""a""}]}");
        Assert.False(RequestProcessor.DetectToolLoop(obj));
    }

    [Fact]
    public void DetectToolLoop_NoMessages_ReturnsFalse()
    {
        Assert.False(RequestProcessor.DetectToolLoop(Parse(@"{""model"":""m""}")));
    }
}
