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
        var line = $"unit-test-{Guid.NewGuid():N}";
        LogFile.Append(line);
        // 150ms 定时器 Flush；最多等 2s
        var path = Path.Combine(AppContext.BaseDirectory, "logs", "harness.log");
        string? content = null;
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(100);
            if (File.Exists(path))
            {
                // 常驻 StreamWriter 持有文件句柄：读取需共享模式（FileShare.Read|Write）
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Write);
                    using var reader = new StreamReader(fs);
                    content = reader.ReadToEnd();
                    if (content.Contains(line)) break;
                }
                catch (IOException) { /* 轮切瞬间文件被 rename，重试 */ }
            }
        }
        Assert.NotNull(content);
        Assert.Contains(line, content!);
    }
}
