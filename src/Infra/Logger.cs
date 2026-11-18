using System.IO;

namespace NdiScreenshare.Infra;

public static class Logger
{
    private static readonly object _lock = new();
    private const long MaxBytes = 1024 * 1024;

    public static string LogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NDI_Screenshare", "logs", "app.log");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);
    public static void Error(Exception ex, string ctx = "") =>
        Write("ERROR", string.IsNullOrEmpty(ctx) ? ex.ToString() : ctx + ": " + ex);

    private static void Write(string level, string msg)
    {
        try
        {
            var p = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            lock (_lock)
            {
                try
                {
                    if (File.Exists(p) && new FileInfo(p).Length > MaxBytes)
                        File.Delete(p);
                }
                catch { }
                File.AppendAllText(p, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
