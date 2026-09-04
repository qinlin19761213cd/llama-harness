namespace LlamaHarness;

/// <summary>
/// 主日志区渲染（RichTextBox 按行独立着色 + 防抖批量消费）。可来自任意线程。
/// 日志先入队列，UI 定时器每 150ms 批量消费（一次 AppendText + 逐行着色），减少重绘闪烁。
/// </summary>
public sealed class LogView : UserControl
{
    /// <summary>日志字符上限（约数万行）：防止长期运行无限增长拖慢 UI。</summary>
    private const int MaxLogChars = 400_000;

    private readonly Queue<(string line, string entry)> _logQueue = new();
    private readonly System.Windows.Forms.Timer _logFlushTimer = new() { Interval = 150 };
    private bool _isFlushing; // M-13 修复：防止 Flush() 重入（AppendText 触发事件导致）

    /// <summary>主日志 RichTextBox（页签宿主/清空等外部引用此实例）。</summary>
    public RichTextBox TxtLog { get; } = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = RichTextBoxScrollBars.Vertical,
        WordWrap = false,
        BorderStyle = BorderStyle.None, // 无边框，消除白边
        BackColor = UiTheme.C_TextBg,
        ForeColor = UiTheme.C_TextFg,
        Font = UiTheme.GetFont("Consolas", 9F),
    };

    /// <summary>
    /// 问题 17 修复：日志级别过滤（默认 ShowAll 向后兼容）。
    /// 文件层 Append 仍写入所有级别（磁盘审计需完整），仅 UI 显示层按 Level 过滤。
    /// Warn/Debug 场景下可屏蔽普通 Info 降低视觉噪声。
    /// </summary>
    public LogLevelFilter LevelFilter { get; set; } = LogLevelFilter.ShowAll;

    /// <summary>
    /// 问题 18 修复：自动滚动到最新一行（默认 true，与旧行为一致）。
    /// 排查历史日志时若用户手动上滚，可临时置 false 让 UI 停在当前位置；排查完再置 true 恢复跟随。
    /// </summary>
    public bool AutoFollow { get; set; } = true;

    public bool HasPending { get { lock (_logQueue) return _logQueue.Count > 0; } }
    public LogView()
    {
        Dock = DockStyle.Fill;
        Controls.Add(TxtLog);
    }

    /// <summary>追加一行带时间戳的日志并按级别着色（正常绿/警告黄/错误红），自动滚到底部。可来自任意线程。
    /// 防抖：日志先入队列，UI 定时器每 150ms 批量消费，减少重绘闪烁。</summary>
    public void Append(string line)
    {
        LogFile.Append(line); // 文件持久化 + 轮切 + 警告/错误独立输出
        var entry = $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}";
        lock (_logQueue) _logQueue.Enqueue((line, entry));
        // 注意：禁止在此（后台线程）Start/Stop 定时器——Win32 SetTimer 绑定调用线程的消息循环，
        // 跨线程 Start 会静默失败导致 UI 显示永久停摆。定时器常驻运行，Flush 空队列时直接返回。
    }

    /// <summary>清空队列与显示（防止残留旧日志追加）。</summary>
    public void Clear()
    {
        lock (_logQueue) _logQueue.Clear(); // 清空队列，防止残留旧日志追加
        TxtLog.Clear();
    }

    /// <summary>启动防抖定时器（UI 线程，OnShown 调用；常驻不 Stop/Start，避免跨线程 SetTimer 绑定错误消息循环）。</summary>
    public void Start()
    {
        _logFlushTimer.Tick += (_, _) => Flush();
        _logFlushTimer.Start();
    }

    /// <summary>停止定时器并刷出队列中剩余日志（关闭前调用，避免最后几条丢失）。</summary>
    public void StopAndFlush()
    {
        _logFlushTimer.Stop();
        if (HasPending) Flush();
    }

    /// <summary>批量消费日志队列：一次 AppendText + 逐行着色，大幅减少 RichTextBox 重绘次数。（UI 线程）
    /// M-13 修复：使用 _isFlushing 标志防止重入（AppendText 触发事件导致）。</summary>
    public void Flush()
    {
        // M-13：防重入——如果已经在 Flush 中，直接返回（避免 AppendText 触发事件导致递归）
        if (_isFlushing) return;

        // 问题 17：读取当前过滤级别（快照值，避免消费过程中被 UI 线程其他操作切换）
        var levelFilter = LevelFilter;

        List<(string line, string entry)> batch;
        lock (_logQueue)
        {
            if (_logQueue.Count == 0) return; // 无新日志，直接返回（定时器常驻）
            batch = new List<(string line, string entry)>();
            // 问题 17：按级别过滤——不显示的条目丢弃不入 UI（文件层已落盘）
            while (_logQueue.Count > 0)
            {
                var item = _logQueue.Dequeue();
                if (PassFilter(LogFile.Classify(item.line), levelFilter))
                    batch.Add(item);
            }
        }

        // 全部被过滤 → 无内容需显示，直接返回（不设 _isFlushing 避免 M-13 残留）
        if (batch.Count == 0) return;

        // M-13 回归修复：防重入标志仅在真正消费队列时设置（原实现空队列 return 时 _isFlushing
        // 永久残留 true，导致后续所有 Flush 直接返回、UI 日志永久停摆）。空队列路径不设标志。
        _isFlushing = true;

        try
        {
            // E-9：全部 entry 拼接后单次 AppendText（替代 N 次独立追加，减少布局触发/重绘）
            var all = string.Concat(batch.Select(b => b.entry));
            TxtLog.AppendText(all);

            // 字符上限截断
            if (TxtLog.TextLength > MaxLogChars)
            {
                TxtLog.SelectionStart = 0;
                TxtLog.SelectionLength = TxtLog.TextLength / 2;
                TxtLog.SelectedText = "";
            }

            // 逐行着色：从末尾往前累加 entry.Length 定位每行起点
            int pos = TxtLog.TextLength;
            for (int i = batch.Count - 1; i >= 0; i--)
            {
                var (line, entry) = batch[i];
                pos -= entry.Length;
                int start = Math.Max(0, pos);
                TxtLog.SelectionStart = start;
                TxtLog.SelectionLength = entry.Length;
                TxtLog.SelectionColor = LogFile.Classify(line) switch
                {
                    LogFile.Level.Warn => Color.Gold,
                    LogFile.Level.Error => Color.Red,
                    _ => Color.LightGreen,
                };
            }

            // 问题 18：AutoFollow=false 时不强制滚到底部（用户可能正在翻阅历史）
            if (AutoFollow)
            {
                TxtLog.SelectionStart = TxtLog.TextLength;
                TxtLog.SelectionLength = 0;
                TxtLog.ScrollToCaret();
            }
        }
        catch
        {
            // 显示层异常不得杀死日志管道（文件层已持久化），吞掉继续
        }
        finally
        {
            // M-13：确保无论成功/异常都清除重入标志
            _isFlushing = false;
        }
    }

    /// <summary>
    /// 问题 17：判定一行日志是否通过当前过滤级别。
    /// LogFile.Level 只有 Warn/Error/Info 三档（无 Debug），
    /// LevelFilter 提供 5 档可选视图；无对应关系的组合默认放行以保兼容。
    /// </summary>
    private static bool PassFilter(LogFile.Level level, LogLevelFilter filter)
    {
        return (level, filter) switch
        {
            // 全部显示
            (_, LogLevelFilter.ShowAll) => true,
            // 仅错误
            (LogFile.Level.Error, LogLevelFilter.ErrorsOnly) => true,
            (_, LogLevelFilter.ErrorsOnly) => false,
            // 错误 + 警告
            (LogFile.Level.Error, LogLevelFilter.ErrorAndWarn) => true,
            (LogFile.Level.Warn, LogLevelFilter.ErrorAndWarn) => true,
            (_, LogLevelFilter.ErrorAndWarn) => false,
            // 警告及以上
            (LogFile.Level.Error, LogLevelFilter.WarnAndAbove) => true,
            (LogFile.Level.Warn, LogLevelFilter.WarnAndAbove) => true,
            (_, LogLevelFilter.WarnAndAbove) => false,
            // 全部非 Info（等价 WarnAndAbove）
            (LogFile.Level.Info, LogLevelFilter.NonInfo) => false,
            (_, LogLevelFilter.NonInfo) => true,
            _ => true,
        };
    }
}

/// <summary>
/// 问题 17 修复：日志显示级别过滤枚举。
/// ShowAll 为默认，与历史行为兼容；其余档位仅供 UI 按需切换使用。
/// </summary>
public enum LogLevelFilter
{
    ShowAll,       // 全部显示（默认）
    ErrorsOnly,    // 仅显示 Error
    ErrorAndWarn,  // 显示 Error + Warn
    WarnAndAbove,  // 显示 Warn 及以上
    NonInfo,       // 隐藏普通 Info（等价 WarnAndAbove，命名面向"过滤噪声"场景）
}
