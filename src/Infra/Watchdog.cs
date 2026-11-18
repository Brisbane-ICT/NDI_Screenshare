namespace NdiScreenshare.Infra;

public sealed class Watchdog : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private readonly int _timeoutMs;
    private readonly Action _onTimeout;
    private long _lastBeat;

    public Watchdog(int timeoutMs, Action onTimeout)
    {
        _timeoutMs = timeoutMs;
        _onTimeout = onTimeout;
        Beat();
        _timer = new System.Threading.Timer(_ => Check(), null, timeoutMs, timeoutMs / 2);
    }

    public void Beat() => Interlocked.Exchange(ref _lastBeat, Environment.TickCount64);

    private void Check()
    {
        try
        {
            if (Environment.TickCount64 - Interlocked.Read(ref _lastBeat) > _timeoutMs)
            {
                Beat();
                _onTimeout();
            }
        }
        catch { }
    }

    public void Dispose() => _timer.Dispose();
}
