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

    // B-02 修复：KV Cache 默认目录改为项目下的本地目录（消除硬编码 g:/temp 盘符）
    public static string KvCacheDir => Path.Combine(BaseDir, "kv_cache");

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
        // 问题 15 修复：Windows 下对新建目录显式授予当前用户 FullControl 权限。
        // 动机：跨用户共享盘（多用户共用开发机/项目盘符）创建目录时，父目录的继承 ACL 可能
        // 导致该用户后续无法读写；显式补一条 FullControl ACE（保留原有 ACE 集合，不覆盖）
        // 兜底。非 Windows（Linux/macOS 使用 POSIX uid/gid 权限，默认 0755 已足够）跳过。
        if (OperatingSystem.IsWindows()) TryGrantCurrentUserFullControl(dir, isDirectory: true);
        return dir;
    }

    /// <summary>确保 config/ 目录存在（幂等）。</summary>
    public static void EnsureConfigDir() => EnsureDir(ConfigDir);

    /// <summary>确保 logs/ 目录存在（幂等）。</summary>
    public static void EnsureLogDir() => EnsureDir(LogDir);

    /// <summary>对已存在文件显式授予当前用户 FullControl（问题 15）。失败静默（保留原有权限）。</summary>
    public static void TryGrantFileFullControl(string filePath)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!File.Exists(filePath)) return;
        TryGrantCurrentUserFullControl(filePath, isDirectory: false);
    }

    /// <summary>对文件/目录追加当前用户 FullControl ACE（保留原有 ACE 集合）。非 Windows 或操作失败时静默跳过。</summary>
    private static void TryGrantCurrentUserFullControl(string path, bool isDirectory)
    {
        try
        {
            var identity = new System.Security.Principal.NTAccount(System.Security.Principal.WindowsIdentity.GetCurrent().Name);
            if (isDirectory)
            {
                var dir = new System.IO.DirectoryInfo(path);
                var sec = dir.GetAccessControl();
                sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(identity,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.AccessControlType.Allow));
                sec.SetAccessRuleProtection(false, preserveInheritance: true);
                dir.SetAccessControl(sec);
            }
            else
            {
                var file = new System.IO.FileInfo(path);
                var sec = file.GetAccessControl();
                sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(identity,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.AccessControlType.Allow));
                sec.SetAccessRuleProtection(false, preserveInheritance: true);
                file.SetAccessControl(sec);
            }
        }
        catch { /* ACL 操作失败静默：权限兜底属增强性质，不阻断主流程 */ }
    }

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
