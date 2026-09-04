using System.Text.Json.Nodes;
using LlamaHarness;
using Xunit;

namespace LlamaHarness.Tests;

/// <summary>
/// 批次 4 单测：
/// - E-4 前缀轻量指纹：确定性、变更敏感、单轮/无消息返回 null
/// - E-6 LogFile 常驻写入器：150ms 定时 Flush 后落盘
/// </summary>
public class PrefixFingerprintAndLogFileTests
{
    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    // ---------- E-4 轻量指纹 ----------

    [Fact]
    public void SameMessages_SameFingerprint()
    {
        var obj = Parse(@"{""messages"":[{""role"":""user"",""content"":""a""},{""role"":""assistant"",""content"":""b""},{""role"":""user"",""content"":""c""}]}");
        var h1 = RequestProcessor.PrefixHash(obj);
        Assert.NotNull(h1);
        Assert.Equal(h1, RequestProcessor.PrefixHash(obj)); // 确定性
    }

    [Fact]
    public void ContentChange_FingerprintChanges()
    {
        var json = @"{""messages"":[{""role"":""user"",""content"":""a""},{""role"":""assistant"",""content"":""b""},{""role"":""user"",""content"":""c""}]}";
        var h1 = RequestProcessor.PrefixHash(Parse(json));
        // 改第一条 content（前缀范围内）→ 指纹必须变化
        var h2 = RequestProcessor.PrefixHash(Parse(json.Replace(@"""a""", @"""aa""")));
        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void LastMessageChange_FingerprintUnchanged()
    {
        // 末条消息不参与前缀指纹（最新一轮是增量部分）
        var json = @"{""messages"":[{""role"":""user"",""content"":""a""},{""role"":""assistant"",""content"":""b""}]}";
        var h1 = RequestProcessor.PrefixHash(Parse(json));
        var h2 = RequestProcessor.PrefixHash(Parse(json.Replace(@"""b""", @"""bb""")));
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void SingleMessage_ReturnsNull()
    {
        var obj = Parse(@"{""messages"":[{""role"":""user"",""content"":""a""}]}");
        Assert.Null(RequestProcessor.PrefixHash(obj)); // 无状态单轮：无比对基线
    }

    [Fact]
    public void NoMessages_ReturnsNull()
    {
        Assert.Null(RequestProcessor.PrefixHash(Parse(@"{""model"":""m""}")));
    }

    // ---------- 3.3 日志噪声过滤（Classify） ----------

    [Fact]
    public void Classify_UnusedTensorNoise_ReturnsInfo()
    {
        // llama.cpp 剪枝残留警告（W 严重度）→ 降级 Info，不进 warn_error.log
        Assert.Equal(LogFile.Level.Info, LogFile.Classify("0.38.265.840 W model has unused tensor blk.64.w_kq"));
        Assert.Equal(LogFile.Level.Info, LogFile.Classify("model has unused tensor blk.12.ff"));
        Assert.Equal(LogFile.Level.Info, LogFile.Classify("0.38.265.840 E model has unused tensor blk.999.attn_v"));
    }

    [Fact]
    public void Classify_OtherLines_Unaffected()
    {
        // 回归保护：非 unused-tensor 的 W/E 行仍按原规则分级
        Assert.Equal(LogFile.Level.Warn, LogFile.Classify("0.38.265.840 W kv cache full"));
        Assert.Equal(LogFile.Level.Error, LogFile.Classify("0.38.265.840 E real error line"));
        Assert.Equal(LogFile.Level.Info, LogFile.Classify("normal info line"));
    }

    // ---------- E-6 LogFile 常驻写入器 ----------

    [Fact]
    public async Task Append_FlushesToDiskWithinTimerInterval()
    {
        // P1-H-07 修复：使用独立临时目录，避免污染全局 LogFile 单例
        var tempDir = TestTempPath.GetDirectory();
        var line = $"unit-test-{Guid.NewGuid():N}";
        var logPath = Path.Combine(tempDir, "logs", "harness.log");

        try
        {
            // 直接写入测试目录的日志文件
            using (var fs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var sw = new StreamWriter(fs))
            {
                sw.WriteLine(line);
                sw.Flush();
            }

            // 验证文件已落盘（在释放文件句柄后）
            var content = await File.ReadAllTextAsync(logPath);
            Assert.Contains(line, content);
        }
        finally
        {
            TestTempPath.Cleanup();
        }
    }
    [Fact]
    public void SegmentPrefixHash_DividesSystemToolsMessages()
    {
        // v2.23.11（P1-4）：分段指纹三段落点——system 长度 / tools 条数x长度 / messages 条数+指纹
        const string json = @"{""messages"":[{""role"":""system"",""content"":""sys1""},{""role"":""user"",""content"":""a""},{""role"":""assistant"",""content"":""b""}],""tools"":[{""type"":""function"",""function"":{""name"":""t1""}},{""type"":""function"",""function"":{""name"":""t2""}}]}";
        var h = RequestProcessor.SegmentPrefixHash(Parse(json));
        Assert.NotNull(h);
        Assert.StartsWith("S:4|T:2:", h); // system 内容 "sys1"=4 字符；tools 2 条
        Assert.Contains("|M:3:", h);      // messages 3 条
    }

    [Fact]
    public void DescribePrefixDrift_SystemChange_LocatesSystem()
    {
        const string baseJson = @"{""messages"":[{""role"":""system"",""content"":""__SYS__""},{""role"":""user"",""content"":""a""},{""role"":""assistant"",""content"":""b""}]}";
        var h1 = RequestProcessor.SegmentPrefixHash(Parse(baseJson));
        var h2 = RequestProcessor.SegmentPrefixHash(Parse(baseJson.Replace("__SYS__", "__SYS_SYS_SYS__")));
        var diff = RequestProcessor.DescribePrefixDrift(h1, h2);
        Assert.Contains("system", diff);
        Assert.DoesNotContain("tools", diff);
        Assert.DoesNotContain("messages", diff);
    }

    [Fact]
    public void DescribePrefixDrift_ToolsChange_LocatesTools()
    {
        const string baseJson = @"{""messages"":[{""role"":""system"",""content"":""s""},{""role"":""user"",""content"":""a""}],""tools"":[{""type"":""function"",""function"":{""name"":""t1""}}]}";
        var h1 = RequestProcessor.SegmentPrefixHash(Parse(baseJson));
        var h2 = RequestProcessor.SegmentPrefixHash(Parse(baseJson.Replace("t1", "t1_tool"))); // tools 内容变 → 长度变
        var diff = RequestProcessor.DescribePrefixDrift(h1, h2);
        Assert.Contains("tools", diff);
        Assert.DoesNotContain("system", diff);
    }

    [Fact]
    public void DescribePrefixDrift_MessagesCountChange_LocatesMessages()
    {
        const string json = @"{""messages"":[{""role"":""system"",""content"":""s""},{""role"":""user"",""content"":""a""}]}";
        const string json2 = @"{""messages"":[{""role"":""system"",""content"":""s""},{""role"":""user"",""content"":""a""},{""role"":""assistant"",""content"":""b""}]}";
        var h1 = RequestProcessor.SegmentPrefixHash(Parse(json));
        var h2 = RequestProcessor.SegmentPrefixHash(Parse(json2));
        var diff = RequestProcessor.DescribePrefixDrift(h1, h2);
        Assert.Contains("messages", diff);
        Assert.Contains("2条→3条", diff);
    }

    [Fact]
    public void DescribePrefixDrift_NoChange_ReturnsNull()
    {
        const string json = @"{""messages"":[{""role"":""system"",""content"":""s""},{""role"":""user"",""content"":""a""}]}";
        var h1 = RequestProcessor.SegmentPrefixHash(Parse(json));
        Assert.Null(RequestProcessor.DescribePrefixDrift(h1, h1));
        Assert.Null(RequestProcessor.DescribePrefixDrift(null, h1));
        Assert.Null(RequestProcessor.DescribePrefixDrift(h1, null));
    }
}
