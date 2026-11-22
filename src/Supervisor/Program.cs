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
// so no schtasks.exe / powershell.exe / wscript.exe is ever involved —
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
    private const string TaskApp = "NDI Screenshare";
    private const string TaskSupervisor = "NDI Screenshare Supervisor";
    private const string TaskHeartbeat = "NDI Screenshare Heartbeat";
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
            string? exe = ResolveAppExe();
            string? sup = ResolveOwnExe();
            if (exe == null || sup == null)
            {
                SupLog("Install: could not resolve exe paths; tasks not registered.");
                return;
            }
            string user = WindowsIdentity.GetCurrent().Name;
            RegisterLogonTask(TaskApp, exe, "", "NDI Screenshare: start at logon; restart 1 min after any crash.");
            RegisterLogonTask(TaskSupervisor, sup, "", "NDI Screenshare supervisor: keeps the feed alive; restart 1 min after any crash.");
            RegisterHeartbeatTask(sup);
            SupLog($"Install complete. app={exe} supervisor={sup}");
            // Start the loop now so protection is immediate (no logoff needed).
            // The loop starts the app itself if it is not running.
            StartHidden(sup);
        }
        catch (Exception ex) { SupLog("Install error: " + ex.Message); }
    }

    private static void Uninstall()
    {
        try
        {
            try { foreach (var p in Process.GetProcessesByName("Supervisor")) { try { if (p.Id != Environment.ProcessId) p.Kill(); } catch { } } } catch { }
            try { foreach (var p in Process.GetProcessesByName(AppProcessName)) { try { p.Kill(); } catch { } } } catch { }
            DeleteTask(TaskHeartbeat);
            DeleteTask(TaskSupervisor);
            DeleteTask(TaskApp);
            SupLog("Uninstall complete.");
        }
        catch (Exception ex) { SupLog("Uninstall error: " + ex.Message); }
    }

    // ---- Task Scheduler COM (no CLI tools) ----

    private static object Scheduler()
    {
        Type? t = Type.GetTypeFromProgID("Schedule.Service");
        if (t == null) throw new InvalidOperationException("Schedule.Service COM not available.");
        dynamic svc = Activator.CreateInstance(t)!;
        svc.Connect();
        return svc;
    }

    private static void RegisterLogonTask(string name, string exePath, string args, string description)
    {
        dynamic svc = Scheduler();
        dynamic root = svc.GetFolder("\\");
        try { root.DeleteTask(name, 0); } catch { }
        dynamic def = svc.NewTask(0);
        def.RegistrationInfo.Description = description;
        def.RegistrationInfo.Author = "NDI Screenshare";
        dynamic trigger = def.Triggers.Create(9); // TASK_TRIGGER_LOGON
        trigger.UserId = WindowsIdentity.GetCurrent().Name;
        dynamic action = def.Actions.Create(0); // TASK_ACTION_EXEC
        action.Path = exePath;
        action.Arguments = args ?? "";
        def.Settings.RestartCount = 9999;
        def.Settings.RestartInterval = "PT1M";
        def.Settings.StartWhenAvailable = true;
        def.Settings.AllowStartIfOnBatteries = true;
        def.Settings.DontStopIfGoingOnBatteries = true;
        def.Settings.ExecutionTimeLimit = "PT0S";
        def.Settings.MultipleInstances = 1; // ignore new instance while running
        def.Principal.UserId = WindowsIdentity.GetCurrent().Name;
        def.Principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN (per-user, no password)
        def.Principal.RunLevel = 0; // least privilege
        root.RegisterTaskDefinition(name, def, 6, null, null, 3, null); // 6 = CREATE_OR_UPDATE
        SupLog($"Registered task '{name}' -> {exePath} {args}".Trim());
    }

    private static void RegisterHeartbeatTask(string supervisorExe)
    {
        dynamic svc = Scheduler();
        dynamic root = svc.GetFolder("\\");
        try { root.DeleteTask(TaskHeartbeat, 0); } catch { }
        dynamic def = svc.NewTask(0);
        def.RegistrationInfo.Description = "NDI Screenshare: single supervision pass every minute (windowless).";
        def.RegistrationInfo.Author = "NDI Screenshare";
        dynamic trigger = def.Triggers.Create(1); // TASK_TRIGGER_TIME
        trigger.StartBoundary = DateTime.Now.AddMinutes(1).ToString("yyyy-MM-ddTHH:mm:ss");
        trigger.Repetition.Interval = "PT1M";
        trigger.Repetition.Duration = "P3650D";
        trigger.Repetition.StopAtDurationEnd = false;
        dynamic action = def.Actions.Create(0);
        action.Path = supervisorExe;
        action.Arguments = "--check-once";
        def.Settings.StartWhenAvailable = true;
        def.Settings.AllowStartIfOnBatteries = true;
        def.Settings.DontStopIfGoingOnBatteries = true;
        def.Settings.ExecutionTimeLimit = "PT5M";
        def.Settings.MultipleInstances = 1;
        def.Principal.UserId = WindowsIdentity.GetCurrent().Name;
        def.Principal.LogonType = 3;
        def.Principal.RunLevel = 0;
        root.RegisterTaskDefinition(TaskHeartbeat, def, 6, null, null, 3, null);
        SupLog($"Registered task '{TaskHeartbeat}' -> {supervisorExe} --check-once");
    }

    private static void DeleteTask(string name)
    {
        try
        {
            dynamic svc = Scheduler();
            dynamic root = svc.GetFolder("\\");
            root.DeleteTask(name, 0);
            SupLog($"Removed task '{name}'.");
        }
        catch { /* already gone */ }
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
