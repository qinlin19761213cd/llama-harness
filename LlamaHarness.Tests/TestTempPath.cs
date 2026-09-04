using System.Runtime.CompilerServices;

namespace LlamaHarness.Tests;

/// <summary>
/// 测试临时路径工具：为每个测试文件创建独立临时目录，避免跨测试污染。
/// P1-H-07/H-08 修复：LogFile 单例和 slot_bindings.json 共享文件隔离。
/// </summary>
public static class TestTempPath
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, string> _dirs = new();

    /// <summary>获取测试专用临时目录（每个测试文件唯一）。</summary>
    public static string GetDirectory([CallerFilePath] string? filePath = null)
    {
        var key = filePath ?? "<unknown>";
        
        lock (_lock)
        {
            if (!_dirs.TryGetValue(key, out var dir))
            {
                dir = Path.Combine(Path.GetTempPath(), $"LlamaHarness.Test_{Path.GetFileName(key)}_{Guid.NewGuid():N}");
                Directory.CreateDirectory(dir);
                Directory.CreateDirectory(Path.Combine(dir, "config"));
                Directory.CreateDirectory(Path.Combine(dir, "logs"));
                _dirs[key] = dir;
            }
            return dir;
        }
    }

    /// <summary>获取测试专用绑定文件路径。</summary>
    public static string GetBindingsPath([CallerFilePath] string? filePath = null)
        => Path.Combine(GetDirectory(filePath), "config", "slot_bindings.json");

    /// <summary>清理指定测试文件的临时目录。</summary>
    public static void Cleanup([CallerFilePath] string? filePath = null)
    {
        var key = filePath ?? "<unknown>";
        lock (_lock)
        {
            if (_dirs.TryGetValue(key, out var dir))
            {
                _dirs.Remove(key);
                try { Directory.Delete(dir, true); } catch { /* 忽略 */ }
            }
        }
    }
}
