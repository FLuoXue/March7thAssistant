namespace March7th.Desktop;

// One outstanding batch: late acknowledgements cannot recapture a released cursor.
// Keep direction changes as separate samples rather than cancelling a quick flick.
internal sealed class MouseFlow
{
    private readonly List<int[]> _samples = new();
    private int _epoch;
    private long _sequence;
    private long _pending;
    private long _sentAt;
    private bool _pendingMoved;
    public bool Enabled { get; private set; }
    public bool Confirmed { get; private set; }

    public void Enable(bool enabled)
    {
        if (Enabled == enabled) return;
        Enabled = enabled;
        Reset();
    }

    public void Reset()
    {
        _epoch++;
        _pending = 0;
        _samples.Clear();
        Confirmed = false;
    }

    public void Add(int dx, int dy)
    {
        if (!Enabled || (dx == 0 && dy == 0)) return;
        if (_samples.Count > 0)
        {
            var previous = _samples[^1];
            if ((long)previous[0] * dx + (long)previous[1] * dy >= 0)
            {
                previous[0] = (int)Math.Clamp((long)previous[0] + dx, -32767, 32767);
                previous[1] = (int)Math.Clamp((long)previous[1] + dy, -32767, 32767);
                return;
            }
        }
        if (_samples.Count == 64) _samples.RemoveAt(0);
        _samples.Add([Math.Clamp(dx, -32767, 32767), Math.Clamp(dy, -32767, 32767)]);
    }

    public object? Take(long now)
    {
        if (!Enabled) return null;
        if (_pending != 0)
        {
            if (now - _sentAt < 250) return null;
            Reset(); // Drop queued movement after a stalled receiver.
        }
        _pending = ++_sequence;
        _sentAt = now;
        _pendingMoved = _samples.Count != 0;
        var samples = _samples.ToArray();
        _samples.Clear();
        return new { op = "mouse", epoch = _epoch, seq = _pending, sent_at = now, samples };
    }

    public void Acknowledge(int epoch, long sequence, bool handled)
    {
        if (!Enabled || epoch != _epoch || sequence != _pending || _pending == 0) return;
        _pending = 0;
        Confirmed = handled && (Confirmed || _pendingMoved);
        if (!handled) _samples.Clear();
    }
}
