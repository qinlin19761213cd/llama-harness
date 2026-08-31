namespace LlamaHarness;

/// <summary>
/// 全项目路径单一入口（v2.16）：集中 BaseDirectory 下 config/logs/static 目录与文件路径，
/// 消除散落在各类的 Path.Combine(AppContext.BaseDirectory, "config"/"logs"/"static", ...) 与重复 EnsureConfigDir。
/// 新增持久化文件只需在此登记，调用方零手抄路径。
/// </summary>
public static class AppPaths
{
    /// <summary>程序运行目录（exe 所在目录）。</summary>
    public static string BaseDir { get; } = AppContext.BaseDirectory;

    // —— 目录根 ——
    public static string ConfigDir => Path.Combine(BaseDir, "config");
    public static string LogDir => Path.Combine(BaseDir, "logs");
    public static string StaticDir => Path.Combine(BaseDir, "static");

    // —— config/ 下的持久化文件 ——
    public static string ConfigJson => Path.Combine(ConfigDir, "config.json");
    public static string SlotBindingsJson => Path.Combine(ConfigDir, "slot_bindings.json");
    public static string KvCacheIndexJson => Path.Combine(ConfigDir, "kv_cache_index.json");
    public static string RestoreStatsJson => Path.Combine(ConfigDir, "restore_stats.json");

    // —— logs/ 下的日志文件 ——
    public static string HarnessLog => Path.Combine(LogDir, "harness.log");
    public static string WarnErrorLog => Path.Combine(LogDir, "warn_error.log");
    public static string SlotLog => Path.Combine(LogDir, "slot.log");
    public static string RequestDumpLog => Path.Combine(LogDir, "request_dump.log");
    public static string UnhandledLog => Path.Combine(LogDir, "unhandled.log");
    public static string PerfLog => Path.Combine(LogDir, "perf.log"); // 性能日志（v2.21，独立直写 5MB 轮切）

    // —— static/ ——
    /// <summary>static/icon/{fileName} 图标路径。</summary>
    public static string IconFile(string fileName) => Path.Combine(StaticDir, "icon", fileName);

    // —— 目录保证（幂等，替代各文件自带的 EnsureConfigDir 重复实现）——
    /// <summary>确保目录存在（幂等），返回目录路径。</summary>
    public static string EnsureDir(string dir)
    {
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>确保 config/ 目录存在（幂等）。</summary>
    public static void EnsureConfigDir() => EnsureDir(ConfigDir);

    /// <summary>确保 logs/ 目录存在（幂等）。</summary>
    public static void EnsureLogDir() => EnsureDir(LogDir);

    /// <summary>llama-server.exe 常见安装位置候选（LlamaFinder 使用；PATH 搜索仍留在 LlamaFinder 逻辑内）。</summary>
    public static IEnumerable<string> BackendExeCandidates()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            Path.Combine(BaseDir, "llama-server.exe"),
            @"C:\llama.cpp\build\bin\Release\llama-server.exe",
            Path.Combine(userProfile, "llama.cpp", "build", "bin", "Release", "llama-server.exe"),
        };
    }
}
