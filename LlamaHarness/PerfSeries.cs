namespace LlamaHarness;

/// <summary>
/// 定长环形缓冲（线程安全滑动窗口，v2.21）：固定容量、写指针环绕、读快照不破坏写入。
/// 用于性能采样点时间序列（1s 采样 × 3600 点 ≈ 1 小时窗口，内存恒定不增长）。
/// 线程安全：Add/Snapshot/Last/Clear 内部 lock；Add 满容量时覆盖最旧元素，不阻塞、不增长。
/// </summary>
public sealed class PerfSeries<T>
{
    private readonly T?[] _buf;
    private readonly int _capacity;
    private readonly object _gate = new();
    private int _head;   // 下一写入位置（0..capacity-1）
    private int _count;  // 当前有效元素数（0..capacity）

    public PerfSeries(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _buf = new T?[capacity];
    }

    /// <summary>缓冲容量（满后恒定的最大元素数）。</summary>
    public int Capacity => _capacity;

    /// <summary>当前元素数（满容量后恒等于 Capacity）。</summary>
    public int Count
    {
        get { lock (_gate) return _count; }
    }

    /// <summary>追加一个元素；满时覆盖最旧元素。</summary>
    public void Add(T item)
    {
        lock (_gate)
        {
            _buf[_head] = item;
            _head = (_head + 1) % _capacity;
            if (_count < _capacity) _count++;
        }
    }

    /// <summary>按写入时间升序的全量快照（复制，调用方不受后续写入影响）。</summary>
    public T[] Snapshot()
    {
        lock (_gate)
        {
            var result = new T[_count];
            int start = _count < _capacity ? 0 : _head; // 满时从 head 起（最旧元素）
            for (int i = 0; i < _count; i++)
                result[i] = _buf[(start + i) % _capacity]!;
            return result;
        }
    }

    /// <summary>最近 n 个元素（时间升序）；n ≤ 0 返回空；n ≥ Count 返回全量。</summary>
    public T[] Last(int n)
    {
        lock (_gate)
        {
            if (n <= 0) return Array.Empty<T>();
            int count = _count;
            int take = Math.Min(n, count);
            var result = new T[take];
            if (take == 0) return result;
            int start = count < _capacity ? 0 : _head; // 最旧元素位置（未满时从头）
            int from = count - take;                 // 最近 take 个的起始偏移
            for (int i = 0; i < take; i++) result[i] = _buf[(start + from + i) % _capacity]!;
            return result;
        }
    }

    /// <summary>清空缓冲。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_buf, 0, _capacity);
            _head = 0;
            _count = 0;
        }
    }
}
