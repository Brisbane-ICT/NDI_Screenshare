# NDI Screenshare

Windows tray app that shares a display over NDI.

## Build

```powershell
dotnet build NDI_Screenshare.sln
```

## Run (framework-dependent)

```powershell
dotnet run --project src/TrayApp/TrayApp.csproj
```

Requires the [NDI Runtime](https://ndi.video/tools/) for sending.

## Publish self-contained single file

```powershell
.\install\build-selfcontained.ps1
```

Output goes to `dist/win-x64`.

## Config

`%APPDATA%\NDI_Screenshare\config.json` (display, FPS, scale, source name,
auto-start, running). Logs at `%APPDATA%\NDI_Screenshare\logs\app.log`.

## Never-stop supervisor (crash + hang recovery)

A native crash inside `Processing.NDI.Lib.x64.dll` kills the process instantly
and cannot be caught from managed code, so recovery is layered:

1. **In-process.**
   - Capture/send loops retry forever with capped backoff; repeated errors are
     logged throttled (first + at most once per 30s).
   - A watcher thread ticks every 2s and verifies frames are actually flowing
     (frames-sent counter advancing, capture/send beats fresh). On stall,
     repeated errors, or a dead worker it tears down and rebuilds the
     duplicator + sender from current config with backoff, logging each action.
     (It cannot survive a native AV — that is what layer 2 is for.)
   - A 5s retry timer restarts a dead pipeline with exponential backoff.
   - Global exception handlers log + rebuild the pipeline, never rethrow.
   - `RegisterApplicationRestart("--restarted", 0)` asks Windows to relaunch the
     app on crash *and* hang; the `--restarted` flag forces streaming on.
   - The app auto-starts streaming whenever `config.Running` is true, so a
     crash-restart resumes the feed with no manual Start.
2. **Out-of-process** (`src/Supervisor`, a windowless WinExe installed next to
   the app and launched at logon from a Startup shortcut — no scheduled
   tasks, no PowerShell, no console windows, no admin rights needed):
   - The supervisor loop ticks every 10s: if the app is not running it
     starts it; if `%APPDATA%\NDI_Screenshare\heartbeat.txt` is older than
     30s (or missing while the app is up >120s) the app is hung and is
     killed + relaunched (the single-instance mutex guarantees no
     double-start). It idles at ~0 CPU and logs to `supervisor.log`.
   - Mutual supervision: the app starts `Supervisor.exe` on launch if it
     is not already running, so each side revives the other.
   - Uninstall stops both processes and removes the Startup shortcuts.
   (Pre-0.1.7 installs used root-folder scheduled tasks that flashed a
   console and need admin rights to remove; 0.1.7+ uses none. If an old
   `NDI Screenshare Heartbeat` task still exists, delete it once from an
   elevated shell.)

End-to-end crash recovery: native AV -> process dies -> supervisor loop
restarts it within ~10s (plus Windows Restart Manager as a third net) ->
`--restarted` forces `Running=true` -> streaming resumes. Hang recovery:
beats stop -> heartbeat goes stale -> supervisor kills + relaunches within
~10s. Logon: both Startup shortcuts launch supervisor + app.
