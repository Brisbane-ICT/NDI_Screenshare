using NdiScreenshare.Infra;
using System.Runtime.InteropServices;

namespace NdiScreenshare;

internal static class Program
{
    private static Mutex? _mutex;
    private static TrayContext? _context;

    // Restart-on-crash AND restart-on-hang via Windows Restart Manager.
    // flags=0 => restart on both crash (non-zero exit / unhandled exception / AV)
    // and hang (no message pumping). The "--restarted" command line forces
    // streaming on relaunch even if the persisted config somehow says stopped.
    // Never throws (P/Invoke guarded); failure just means WER handles the crash.
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegisterApplicationRestart(string? commandLine, int flags);

    [DllImport("kernel32", SetLastError = true)]
    private static extern int UnregisterApplicationRestart();

    [STAThread]
    static void Main(string[] args)
    {
        try { RegisterApplicationRestart("--restarted", 0); }
        catch { /* never throw before startup */ }

        // Crash-restart relaunch: make sure the feed comes back up by itself.
        bool restarted = args.Any(a =>
            string.Equals(a, "--restarted", StringComparison.OrdinalIgnoreCase));
        if (restarted)
        {
            try
            {
                var cfg = AppConfig.Load();
                if (!cfg.Running)
                {
                    cfg.Running = true;
                    cfg.Save();
                }
            }
            catch { /* config errors must not block startup */ }
        }

        // Global handlers: log + trigger a pipeline rebuild, NEVER rethrow.
        // (A native AV inside the NDI DLL itself can't be caught, but every
        // managed fatal still recovers the feed instead of killing the process.)
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                var ex = e.ExceptionObject as Exception ?? new Exception("Unknown fatal");
                Logger.Error(ex, "Fatal");
                _context?.RecoverFromFatal("Fatal", ex);
            }
            catch { /* handlers must never throw */ }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try
            {
                Logger.Error(e.Exception, "UnobservedTask");
                e.SetObserved();
            }
            catch { /* handlers must never throw */ }
        };
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            try
            {
                Logger.Error(e.Exception, "UI thread");
                _context?.RecoverFromFatal("UI thread", e.Exception);
            }
            catch { /* handlers must never throw */ }
        };
        _mutex = new Mutex(true, "NDI_Screenshare_SingleInstance", out bool fresh);
        if (!fresh) return;
        try
        {
            ApplicationConfiguration.Initialize();
            _context = new TrayContext();
            Application.Run(_context);
        }
        catch (Exception ex) { try { Logger.Error(ex, "Fatal"); } catch { } }
        finally
        {
            try { _context = null; } catch { }
            try { _mutex?.ReleaseMutex(); } catch { }
            try { UnregisterApplicationRestart(); } catch { }
        }
    }
}
