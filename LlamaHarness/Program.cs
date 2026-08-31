namespace LlamaHarness;

/// <summary>程序入口：单实例守卫 + 全局异常兜底 + 启动主窗口</summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Per-Monitor V2 高 DPI（必须在创建任何窗口之前设置，否则缩放异常）
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // 单实例：防止两个实例同时唤醒 llama-server（显存双倍占用）
        using var singleton = new Mutex(true, @"Local\LlamaHarness", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("LlamaHarness 已在运行。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // UI 线程未处理异常：捕获并提示（WinForms 默认可能静默吞掉）
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        // 后台线程未处理异常：写日志文件（此处无法依赖 UI）
        AppDomain.CurrentDomain.UnhandledException += OnBackgroundUnhandledException;

        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            MessageBox.Show($"未处理的异常：\n{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            LogCrash(ex);
        }
    }

    /// <summary>后台线程未处理异常处理器：崩溃信息写入 logs/unhandled.log。</summary>
    private static void OnBackgroundUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        => LogCrash(e.ExceptionObject as Exception ?? new Exception("未知异常类型")); // .NET Core 中属性名为 ExceptionObject 且类型为 object

    /// <summary>尽力把崩溃信息追加到项目目录下 logs/unhandled.log。</summary>
    private static void LogCrash(Exception ex)
    {
        try
        {
            AppPaths.EnsureLogDir();
            File.AppendAllText(AppPaths.UnhandledLog,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch
        {
            // 尽力而为，忽略
        }
    }
}
