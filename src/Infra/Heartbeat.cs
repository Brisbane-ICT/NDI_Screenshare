// Heartbeat file writer: hang detection for the out-of-process supervisor.
//
// install/check-heartbeat.ps1 reads the file this class maintains and kills the
// process when the timestamp is stale (>30s), so the "NDI Screenshare" scheduled
// task (restart-on-failure, see install/setup-supervisor.ps1) relaunches a hung
// instance within about a minute. Writes every 2s; never throws.

using System.IO;

namespace NdiScreenshare.Infra;

public sealed class Heartbeat : IDisposable
{
    /// <summary>Shared path used by both the app and check-heartbeat.ps1.</summary>
    public static string HeartbeatPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NDI_Screenshare", "heartbeat.txt");

    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private bool _disposed;

    /// <summary>Starts (or restarts) the 2s heartbeat writer. Never throws.</summary>
    public void Start()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                StopTimerLocked();
                Beat();
                _timer = new System.Threading.Timer(_ => Beat(), null,
                    TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            }
        }
        catch
        {
            // Never throw; a missing heartbeat just makes the supervisor
            // treat the process as hung, which is the safe direction.
        }
    }

    /// <summary>Stops the writer. Never throws.</summary>
    public void Stop()
    {
        try
        {
            lock (_gate)
            {
                StopTimerLocked();
            }
        }
        catch
        {
            // Never throw.
        }
    }

    public void Dispose()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                StopTimerLocked();
            }
        }
        catch
        {
            // Never throw.
        }

        GC.SuppressFinalize(this);
    }

    private void StopTimerLocked()
    {
        try
        {
            _timer?.Dispose();
        }
        catch
        {
            // Never throw.
        }
        finally
        {
            _timer = null;
        }
    }

    private static void Beat()
    {
        try
        {
            string path = HeartbeatPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // UTC ISO-8601 so the PowerShell supervisor can parse it
            // unambiguously with [datetime]::Parse / Get-Date.
            File.WriteAllText(path, DateTime.UtcNow.ToString("o"));
        }
        catch
        {
            // Never throw from the timer callback.
        }
    }
}
