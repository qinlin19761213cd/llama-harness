using System.Globalization;
using System.Text.RegularExpressions;

namespace LlamaHarness;

/// <summary>
/// llama-server print_timing 日志行实时解析器（纯逻辑，无 UI/进程依赖）。
/// 按 task ID 归组每个 API 请求为一轮记录：
/// 输入 tokens/速度、输出 tokens/速度、投机解码命中率、f_sim_best、总耗时。
/// Feed 来自进程输出线程，Reset 来自 UI 线程，内部锁保证安全；事件在锁外触发。
/// </summary>
public sealed class LlamaStatsParser
{
    /// <summary>一轮（一次 API 请求）的计时统计。</summary>
    public sealed class RoundStats
    {
        public long Id { get; internal set; }          // 会话内唯一标识（UI 行键）
        public int TaskId { get; internal set; }
        public DateTime Time { get; internal set; }
        public double PromptMs { get; internal set; }
        public long PromptTokens { get; internal set; }
        public double EvalMs { get; internal set; }
        public long EvalTokens { get; internal set; }
        public double TotalMs { get; internal set; }
        public bool HasDraft { get; internal set; }
        public long DraftAccepted { get; internal set; }
        public long DraftGenerated { get; internal set; }
        public double? FSimBest { get; internal set; }

        /// <summary>输入（prompt 预填充）速度，tokens/s。</summary>
        public double PromptSpeed => PromptMs > 0 ? PromptTokens / (PromptMs / 1000.0) : 0;
        /// <summary>输出（生成）速度，tokens/s。</summary>
        public double EvalSpeed => EvalMs > 0 ? EvalTokens / (EvalMs / 1000.0) : 0;
    }

    private readonly object _gate = new();
    private readonly Dictionary<int, RoundStats> _byTask = new();
    private readonly LinkedList<int> _taskOrder = new(); // M-14 修复：使用 LinkedList 替代 List，RemoveFirst() 为 O(1)
    private long _idCounter;
    // f_sim_best 出现在 print_timing 之前的槽位选择行（get_availabl），归属给下一个新 task
    private double? _pendingFsim;

    /// <summary>每会话最多保留的轮次数，超出自动丢弃最旧的。</summary>
    public int MaxRounds { get; init; } = 50;

    /// <summary>一轮统计被创建或更新时触发（可能来自非 UI 线程）。</summary>
    public event Action<RoundStats>? RoundUpdated;
    /// <summary>超出上限、最旧轮次被淘汰后触发（可能来自非 UI 线程）。</summary>
    public event Action<RoundStats>? RoundRemoved;
    /// <summary>会话重置后触发（清空全部记录）。</summary>
    public event Action? SessionReset;

    // —— 正则（兼容两种实测格式：旧 "id 0 task 0" / 新 "id 0 | task 0 |"，前缀不敏感）——
    private static readonly Regex TaskIdRe = new(@"print_timing:\s+id\s+\d+\s*(?:\|)?\s*task\s+(\d+)");
    private static readonly Regex PromptRe = new(@"prompt eval time\s*=\s*([\d.]+)\s*ms\s*/\s*(\d+)\s*tokens");
    // 负向后顾排除 "prompt eval time"，避免误匹配
    private static readonly Regex EvalRe = new(@"(?<!prompt )eval time\s*=\s*([\d.]+)\s*ms\s*/\s*(\d+)\s*tokens");
    private static readonly Regex TotalRe = new(@"total time\s*=\s*([\d.]+)\s*ms");
    // draft acceptance 兼容 "= 0.65 52 accepted / 80 generated"（旧）与 "= 0.75 (30 accepted / 40 generated)"（新）
    private static readonly Regex DraftRe = new(@"draft acceptance\s*=\s*[\d.]*\s*\(?\s*(\d+)\s*accepted\s*/\s*(\d+)\s*generated");
    // f_sim_best 为定制构建字段：取其后第一个数值，位置不敏感（单独成行或嵌在其他行均可）
    // [^\d\r\n]*? 只跳过数字与换行之外的字符，防 "f_sim_best (3 samples) 0.85" 误取括号内计数
    private static readonly Regex FSimBestRe = new(@"f_sim_best[^\d\r\n]*?(-?\d+(?:\.\d+)?)");

    /// <summary>喂入一行进程输出；print_timing 块更新对应轮次，槽位选择行暂存 f_sim_best。</summary>
    public void Feed(string line)
    {
        bool isTiming = line.Contains("print_timing", StringComparison.Ordinal);

        // f_sim_best 出现在 print_timing 之前的槽位选择行（如 get_availabl），暂存给下一个新 task
        // Contains 前置检查：绝大多数行不含该子串，避免对每行跑正则；Match 只跑一次（原 IsMatch + Match 重复执行）
        if (!isTiming && line.Contains("f_sim_best", StringComparison.Ordinal))
        {
            var fm = FSimBestRe.Match(line);
            if (fm.Success)
            {
                lock (_gate)
                {
                    _pendingFsim = ParseD(fm.Groups[1].Value);
                }
                return;
            }
        }
        if (!isTiming) return; // 快速路径：跳过绝大多数行

        List<RoundStats>? evicted = null;
        RoundStats? round = null;
        lock (_gate)
        {
            var m = TaskIdRe.Match(line);
            int taskId = m.Success ? int.Parse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture) : -1;
            if (taskId >= 0)
            {
                if (!_byTask.TryGetValue(taskId, out round))
                {
                    round = new RoundStats { Id = ++_idCounter, TaskId = taskId, Time = DateTime.Now };
                    _byTask[taskId] = round;
                    _taskOrder.AddLast(taskId);
                    // 新 task 继承暂存的 f_sim_best（时间线上它出现在本请求启动之前）
                    round.FSimBest = _pendingFsim;
                    _pendingFsim = null;

                    // 超出上限：按插入顺序丢弃最旧的轮次
                    while (_taskOrder.Count > MaxRounds)
                    {
                        int old = _taskOrder.First!.Value;
                        _taskOrder.RemoveFirst();
                        evicted ??= new List<RoundStats>();
                        evicted.Add(_byTask[old]);
                        _byTask.Remove(old);
                    }
                }
            }
            if (round != null)
                ApplyLine(round, line);
        }
        if (evicted is not null)
            foreach (var r in evicted)
                RoundRemoved?.Invoke(r);
        if (round != null)
            RoundUpdated?.Invoke(round);
    }

    /// <summary>会话重置：llama-server 重启后 task ID 从 0 重新计数，清空全部记录。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _byTask.Clear();
            _taskOrder.Clear();
            _pendingFsim = null;
        }
        SessionReset?.Invoke();
    }

    /// <summary>当前会话全部轮次快照（按创建顺序）。</summary>
    public List<RoundStats> GetRounds()
    {
        lock (_gate)
        {
            return _byTask.Values.OrderBy(r => r.Id).ToList();
        }
    }

    private static void ApplyLine(RoundStats r, string line)
    {
        var m = PromptRe.Match(line);
        if (m.Success)
        {
            r.PromptMs = ParseD(m.Groups[1].Value);
            r.PromptTokens = ParseL(m.Groups[2].Value);
        }
        m = EvalRe.Match(line);
        if (m.Success)
        {
            r.EvalMs = ParseD(m.Groups[1].Value);
            r.EvalTokens = ParseL(m.Groups[2].Value);
        }
        m = TotalRe.Match(line);
        if (m.Success) r.TotalMs = ParseD(m.Groups[1].Value);
        m = DraftRe.Match(line);
        if (m.Success)
        {
            r.HasDraft = true;
            r.DraftAccepted = ParseL(m.Groups[1].Value);
            r.DraftGenerated = ParseL(m.Groups[2].Value);
        }
        m = FSimBestRe.Match(line);
        if (m.Success) r.FSimBest = ParseD(m.Groups[1].Value);
    }

    private static double ParseD(string s) => double.Parse(s, CultureInfo.InvariantCulture);
    private static long ParseL(string s) => long.Parse(s, CultureInfo.InvariantCulture);
}
