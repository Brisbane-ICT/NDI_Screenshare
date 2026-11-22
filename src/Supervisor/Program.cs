// Invisible never-stop supervisor for NDI Screenshare.
//
// This is a WinExe that NEVER creates a window and NEVER allocates a console.
// It must stay that way: no Console calls, no MessageBox, no WinForms.
// All status goes to supervisor.log (best effort, never throws outward).
//
// Modes:
//   Supervisor.exe                 run the supervise loop forever (10s cadence)
//   Supervisor.exe --check-once    single pass, then exit (for the per-minute task)
//   Supervisor.exe --install       register scheduled tasks + start loop, exit
//   Supervisor.exe --uninstall     stop loop/app, remove tasks, exit
//
// Tasks are registered through the Task Scheduler COM API (Schedule.Service)
// so no schtasks.exe / powershell.exe / wscript.exe is ever involved â€”
// launching those from Task Scheduler is what flashed console windows.
// Every task action executes a WinExe binary, which can never show a window.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace NdiScreenshare.Supervisor;

internal static class Program
{
    private const string AppProcessName = "NDI_Screenshare";
    private const string AppExeName = "NDI_Screenshare.exe";
    private const string SupervisorMutexName = "NDI_Screenshare_Supervisor_SingleInstance";
    // Pre-0.1.7 installers registered root-folder scheduled tasks that ran
    // powershell.exe every minute (the console flash). Root-folder tasks need
    // admin rights to touch, so unelevated installs cannot remove them -
    // deletion here is best-effort and succeeds on admin installs.
    // 0.1.7+ creates NO scheduled tasks at all.
    private static readonly string[] LegacyRootTasks = new[]
    {
        "NDI Screenshare",
        "NDI Screenshare Supervisor",
        "NDI Screenshare Heartbeat",
    };
    private const int MaxHeartbeatAgeSeconds = 30;
    private const int NoHeartbeatGraceSeconds = 120;

    private static Mutex? _mutex;

    static int Main(string[] args)
    {
        try
        {
            bool install = args.Any(a => string.Equals(a, "--install", StringComparison.OrdinalIgnoreCase));
            bool uninstall = args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase));
            bool checkOnce = args.Any(a => string.Equals(a, "--check-once", StringComparison.OrdinalIgnoreCase));

            if (install) { Install(); return 0; }
            if (uninstall) { Uninstall(); return 0; }

            // Loop and check-once both need single-instance: a second copy just exits.
            _mutex = new Mutex(true, SupervisorMutexName, out bool fresh);
            if (!fresh) return 0;

            if (checkOnce) { SuperviseOnce(); return 0; }

            SupLog("Supervisor loop started.");
            while (true)
            {
                try { SuperviseOnce(); }
                catch (Exception ex) { SupLog("Loop error (continuing): " + ex.Message); }
                Thread.Sleep(TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception ex)
        {
            try { SupLog("Fatal (exiting): " + ex); } catch { }
            return 0; // never fail callers (installer/uninstall must not break)
        }
    }

    // ---- One supervision pass: app alive? heartbeat fresh? ----

    private static void SuperviseOnce()
    {
        string? exe = ResolveAppExe();
        var procs = Process.GetProcessesByName(AppProcessName);

        if (procs.Length == 0)
        {
            if (exe == null)
            {
                SupLog("App not running and exe path unknown; waiting for next pass.");
                return;
            }
            SupLog("App not running; starting " + exe);
            StartHidden(exe);
            return;
        }

        // App is up: is it actually streaming (heartbeat fresh)?
        string hbPath = HeartbeatPath();
        bool hung = false;
        string reason = "";
        DateTime stamp;
        if (TryReadHeartbeat(hbPath, out stamp))
        {
            double age = (DateTime.UtcNow - stamp.ToUniversalTime()).TotalSeconds;
            if (age > MaxHeartbeatAgeSeconds)
            {
                hung = true;
                reason = $"heartbeat age {(int)age}s > {MaxHeartbeatAgeSeconds}s";
            }
        }
        else
        {
            DateTime oldest = procs
                .Select(p => { try { return p.StartTime; } catch { return DateTime.Now; } })
                .OrderBy(d => d).First();
            if ((DateTime.Now - oldest).TotalSeconds > NoHeartbeatGraceSeconds)
            {
                hung = true;
                reason = $"no heartbeat while up >{NoHeartbeatGraceSeconds}s";
            }
        }

        if (!hung) return;
        if (exe == null)
        {
            SupLog($"Hung app detected ({reason}) but exe path unknown; leaving it alone.");
            return;
        }
        SupLog($"Hung app detected ({reason}); restarting {exe}.");
        try { foreach (var p in Process.GetProcessesByName(AppProcessName)) { try { p.Kill(); } catch { } } } catch { }
        Thread.Sleep(TimeSpan.FromSeconds(2));
        if (Process.GetProcessesByName(AppProcessName).Length == 0)
        {
            SupLog("Relaunching app.");
            StartHidden(exe);
        }
        else
        {
            SupLog("App still alive after kill; leaving it for the next pass.");
        }
    }

    // ---- Install / uninstall ----

    private static void Install()
    {
        try
        {
            // No scheduled tasks, no COM, no CLI tools — persistence comes
            // from Startup-folder shortcuts (windowless WinExe launches that
            // can never flash a console and need no admin rights). Just make
            // sure the loop is running; it starts the app itself in seconds.
            StartLoopIfNeeded();
            DeleteLegacyRootTasks();
            SupLog("Install complete (loop ensured; persistence via Startup shortcuts).");
        }
        catch (Exception ex) { SupLog("Install error: " + ex.Message); }
    }

    private static void Uninstall()
    {
        try
        {
            try { foreach (var p in Process.GetProcessesByName("Supervisor")) { try { if (p.Id != Environment.ProcessId) p.Kill(); } catch { } } } catch { }
            try { foreach (var p in Process.GetProcessesByName(AppProcessName)) { try { p.Kill(); } catch { } } } catch { }
            DeleteLegacyRootTasks();
            SupLog("Uninstall complete.");
        }
        catch (Exception ex) { SupLog("Uninstall error: " + ex.Message); }
    }

    private static void StartLoopIfNeeded()
    {
        try
        {
            // This --install process exits; the loop must live on in a copy.
            if (Process.GetProcessesByName("Supervisor").Length > 1) return;
            string? sup = ResolveOwnExe();
            if (sup == null) { SupLog("Install: supervisor exe path unknown."); return; }
            StartHidden(sup);
        }
        catch (Exception ex) { SupLog("Install: loop start failed: " + ex.Message); }
    }

    // ---- Helpers (never throw outward) ----

    private static string? ResolveOwnExe()
    {
        try
        {
            string self = Path.Combine(AppContext.BaseDirectory, "Supervisor.exe");
            if (File.Exists(self)) return self;
            var p = Process.GetCurrentProcess();
            string? path = p.MainModule?.FileName;
            if (path != null && File.Exists(path)) return path;
        }
        catch { }
        return null;
    }

    private static string? ResolveAppExe()
    {
        try
        {
            string here = Path.Combine(AppContext.BaseDirectory, AppExeName);
            if (File.Exists(here)) return here;
            var p = Process.GetProcessesByName(AppProcessName).FirstOrDefault();
            try
            {
                string? path = p?.MainModule?.FileName;
                if (path != null && File.Exists(path)) return path;
            }
            catch { }
            foreach (string? root in new[] { Environment.GetEnvironmentVariable("LOCALAPPDATA"), Environment.GetEnvironmentVariable("ProgramFiles") })
            {
                if (string.IsNullOrEmpty(root)) continue;
                foreach (string rel in new[] { @"Programs\NDI Screenshare\" + AppExeName, @"NDI Screenshare\" + AppExeName })
                {
                    string c = Path.Combine(root, rel);
                    if (File.Exists(c)) return c;
                }
            }
        }
        catch { }
        return null;
    }

    private static void StartHidden(string exe)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception ex) { SupLog("Start failed for " + exe + ": " + ex.Message); }
    }

    private static string HeartbeatPath()
    {
        try { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NDI_Screenshare", "heartbeat.txt"); }
        catch { return ""; }
    }

    private static bool TryReadHeartbeat(string path, out DateTime stamp)
    {
        stamp = default;
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            string raw = File.ReadAllText(path).Trim();
            stamp = DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            return true;
        }
        catch { return false; }
    }

    private static void DeleteLegacyRootTasks()
    {
        // Only DeleteTask is used here (proven to work via late-bound COM);
        // task *registration* is deliberately not attempted anywhere.
        try
        {
            Type? t = Type.GetTypeFromProgID("Schedule.Service");
            if (t == null) return;
            dynamic svc = Activator.CreateInstance(t)!;
            svc.Connect();
            dynamic root = svc.GetFolder("\\");
            foreach (string name in LegacyRootTasks)
            {
                try { root.DeleteTask(name, 0); SupLog($"Removed legacy scheduled task '{name}'."); }
                catch { /* needs elevation; an admin install cleans it */ }
            }
        }
        catch (Exception ex) { SupLog("Legacy cleanup note: " + ex.Message); }
    }

    private static void SupLog(string msg)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NDI_Screenshare", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "supervisor.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [supervisor] {msg}{Environment.NewLine}");
        }
        catch { }
    }
}

