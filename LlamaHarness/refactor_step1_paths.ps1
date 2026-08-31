# refactor_step1_paths.ps1
# Step 1: AppPaths unified path entry - replace scattered Path.Combine(BaseDirectory, ...) in 9 files.
# All string literals are ASCII to avoid PS5.1 no-BOM encoding issues; file content (incl. Chinese comments) is preserved.
$ErrorActionPreference = 'Stop'
$root = 'C:\project\lunch\LlamaHarness'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Replace-Once([string]$path, [string]$old, [string]$new) {
    $c = [System.IO.File]::ReadAllText($path)
    if ($c.Contains($old)) {
        $c = $c.Replace($old, $new)
        [System.IO.File]::WriteAllText($path, $c, $utf8)
        Write-Host "OK   $path :: $($old.Substring(0, [Math]::Min(50, $old.Length)))"
    } else {
        Write-Host "MISS $path :: $old"
    }
}

function Find-Line([string[]]$lines, [string]$sub) {
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i].Contains($sub)) { return $i } }
    return -1
}

function Remove-MethodBlock([string]$path, [string]$declAnchor) {
    $lines = [System.Collections.Generic.List[string]]([System.IO.File]::ReadAllLines($path))
    $i = Find-Line $lines $declAnchor
    if ($i -lt 0) { Write-Host "MISS $path :: anchor $declAnchor"; return }
    # Delete [i-1 (doc comment), i+4 (closing brace)] plus one trailing blank line if present
    $end = $i + 5
    $delCount = 6
    if ($end -lt $lines.Count -and $lines[$end].Trim() -eq '') { $delCount = 7 }
    $lines.RemoveRange($i - 1, $delCount)
    [System.IO.File]::WriteAllLines($path, $lines, $utf8)
    Write-Host "DEL  $path :: $declAnchor (removed $delCount lines from line $($i-1+1))"
}

# ---------- 1. AppConfig.cs ----------
Replace-Once (Join-Path $root 'AppConfig.cs') `
    'private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config", "config.json");' `
    'private static string ConfigPath => AppPaths.ConfigJson;'

# ---------- 2. SlotAffinity.cs ----------
Replace-Once (Join-Path $root 'SlotAffinity.cs') `
    'private static readonly string BindingsPath = Path.Combine(AppContext.BaseDirectory, "config", "slot_bindings.json");' `
    'private static readonly string BindingsPath = AppPaths.SlotBindingsJson;'
# internal EnsureConfigDir() call inside Save() -> AppPaths
Replace-Once (Join-Path $root 'SlotAffinity.cs') `
    '        EnsureConfigDir();' `
    '        AppPaths.EnsureConfigDir();'
Remove-MethodBlock (Join-Path $root 'SlotAffinity.cs') 'private static void EnsureConfigDir()'

# ---------- 3. KvCacheManager.cs ----------
Replace-Once (Join-Path $root 'KvCacheManager.cs') `
    'private static readonly string IndexPath = Path.Combine(AppContext.BaseDirectory, "config", "kv_cache_index.json");' `
    'private static readonly string IndexPath = AppPaths.KvCacheIndexJson;'
Replace-Once (Join-Path $root 'KvCacheManager.cs') `
    '            EnsureConfigDir();' `
    '            AppPaths.EnsureConfigDir();'
Remove-MethodBlock (Join-Path $root 'KvCacheManager.cs') 'private static void EnsureConfigDir()'

# ---------- 4. RestoreStats.cs ----------
Replace-Once (Join-Path $root 'RestoreStats.cs') `
    'Path.Combine(AppContext.BaseDirectory, "config", "restore_stats.json")' `
    'AppPaths.RestoreStatsJson'

# ---------- 5. LogFile.cs ----------
Replace-Once (Join-Path $root 'LogFile.cs') `
    'internal static string LogDir => Path.Combine(AppContext.BaseDirectory, "logs");' `
    'internal static string LogDir => AppPaths.LogDir;'

# ---------- 6. LogPipeline.cs (file names -> private consts; keep logDir param injectable) ----------
Replace-Once (Join-Path $root 'LogPipeline.cs') `
    '_mainWriter = new LogStreamWriter(Path.Combine(logDir, "harness.log"));' `
    '_mainWriter = new LogStreamWriter(Path.Combine(logDir, MainLogFile));'
Replace-Once (Join-Path $root 'LogPipeline.cs') `
    '_warnWriter = new LogStreamWriter(Path.Combine(logDir, "warn_error.log"));' `
    '_warnWriter = new LogStreamWriter(Path.Combine(logDir, WarnLogFile));'
Replace-Once (Join-Path $root 'LogPipeline.cs') `
    '_slotWriter = new LogStreamWriter(Path.Combine(logDir, "slot.log"));' `
    '_slotWriter = new LogStreamWriter(Path.Combine(logDir, SlotLogFile));'
Replace-Once (Join-Path $root 'LogPipeline.cs') `
    '_dumpWriter = new LogStreamWriter(Path.Combine(logDir, "request_dump.log"));' `
    '_dumpWriter = new LogStreamWriter(Path.Combine(logDir, DumpLogFile));'
# add the 4 file-name consts just above the constructor
Replace-Once (Join-Path $root 'LogPipeline.cs') `
    'public LogPipeline(string logDir, QueueFullPolicy policy, int joinTimeoutMs = 3000)' `
    @'
    private const string MainLogFile = "harness.log";
    private const string WarnLogFile = "warn_error.log";
    private const string SlotLogFile = "slot.log";
    private const string DumpLogFile = "request_dump.log";

    public LogPipeline(string logDir, QueueFullPolicy policy, int joinTimeoutMs = 3000)
'@

# ---------- 7. Program.cs ----------
Replace-Once (Join-Path $root 'Program.cs') `
    @'
            var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
            if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "unhandled.log"),
'@ `
    @'
            AppPaths.EnsureLogDir();
            File.AppendAllText(AppPaths.UnhandledLog,
'@

# ---------- 8. UiTheme.cs ----------
Replace-Once (Join-Path $root 'UiTheme.cs') `
    'var path = Path.Combine(AppContext.BaseDirectory, "static", "icon", fileName);' `
    'var path = AppPaths.IconFile(fileName);'

# ---------- 9. LlamaFinder.cs (candidate list -> AppPaths.BackendExeCandidates) ----------
$lf = Join-Path $root 'LlamaFinder.cs'
$lines = [System.Collections.Generic.List[string]]([System.IO.File]::ReadAllLines($lf))
$i = Find-Line $lines 'var userProfile = Environment.GetFolderPath'
if ($i -ge 0) {
    # Find end of candidates block (line with '        };')
    $j = $i
    while ($j -lt $lines.Count -and -not $lines[$j].Trim().StartsWith('};')) { $j++ }
    # Replace [i..j] (userProfile + new[] { ... };) with single line
    $replacement = '        var candidates = AppPaths.BackendExeCandidates();'
    $lines.RemoveRange($i, $j - $i + 1)
    $lines.Insert($i, $replacement)
    [System.IO.File]::WriteAllLines($lf, $lines, $utf8)
    Write-Host "OK   $lf :: candidates -> AppPaths.BackendExeCandidates"
} else {
    Write-Host "MISS $lf :: userProfile anchor"
}

Write-Host '--- Step 1 replace done ---'
