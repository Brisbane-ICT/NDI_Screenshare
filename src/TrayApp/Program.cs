using NdiScreenshare.Infra;

namespace NdiScreenshare;

internal static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    static void Main()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Logger.Error(e.ExceptionObject as Exception ?? new Exception("Unknown fatal"), "Fatal");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Logger.Error(e.Exception, "UI thread");
        _mutex = new Mutex(true, "NDI_Screenshare_SingleInstance", out bool fresh);
        if (!fresh) return;
        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new TrayContext());
        }
        catch (Exception ex) { Logger.Error(ex, "Fatal"); }
        finally { try { _mutex?.ReleaseMutex(); } catch { } }
    }
}
