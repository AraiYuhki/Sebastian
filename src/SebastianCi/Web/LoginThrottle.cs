namespace SebastianCi.Web;

/// <summary>ログイン失敗の連続を数え、一定時間内に上限を超えたキー（IP・ユーザー名）を一時的にブロックする。</summary>
public sealed class LoginThrottle
{
    private readonly int _maxFailures;
    private readonly TimeSpan _window;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<string, (int Count, DateTime WindowStart)> _failures = new();
    private readonly object _syncRoot = new();

    private readonly int _maxEntries;

    public LoginThrottle(int maxFailures, TimeSpan window, Func<DateTime>? now = null, int maxEntries = 10_000)
    {
        _maxEntries = maxEntries;
        _maxFailures = maxFailures;
        _window = window;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public bool IsBlocked(string key)
    {
        lock (_syncRoot)
        {
            if (!_failures.TryGetValue(key, out var entry)) return false;
            if (_now() - entry.WindowStart > _window)
            {
                _failures.Remove(key);
                return false;
            }

            return entry.Count >= _maxFailures;
        }
    }

    public void RecordFailure(string key)
    {
        lock (_syncRoot)
        {
            DateTime now = _now();
            if (_failures.Count >= _maxEntries) Purge(now);
            if (_failures.Count >= _maxEntries && !_failures.ContainsKey(key)) EvictOldest();

            _failures[key] = _failures.TryGetValue(key, out var entry) && now - entry.WindowStart <= _window
                ? (entry.Count + 1, entry.WindowStart)
                : (1, now);
        }
    }

    public void Reset(string key)
    {
        lock (_syncRoot) { _failures.Remove(key); }
    }

    /// <summary>期限切れを消しても上限に達しているとき、最も古い記録を捨てる（キーを大量に作る攻撃でメモリを使い切らせない）。</summary>
    private void EvictOldest()
    {
        string oldest = _failures.MinBy(pair => pair.Value.WindowStart).Key;
        _failures.Remove(oldest);
    }

    private void Purge(DateTime now)
    {
        foreach (string key in _failures.Where(pair => now - pair.Value.WindowStart > _window)
            .Select(pair => pair.Key).ToList())
        {
            _failures.Remove(key);
        }
    }
}
