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
2. **Out-of-process** (`install/setup-supervisor.ps1`, wired into the installer
   `[Run]` postinstall — per-user, no admin, silent-safe):
   - Scheduled task **`NDI Screenshare`**: logon trigger + restart-on-failure
     every 1 minute (9999 attempts ≈ unlimited). A crash exit is non-zero, so
     the task engine relaunches the app within ~a minute.
   - Scheduled task **`NDI Screenshare Heartbeat`**: runs
     `install/check-heartbeat.ps1` every minute. The app writes
     `%APPDATA%\NDI_Screenshare\heartbeat.txt` every 2s; if it is older than
     30s the process is hung and is killed + relaunched (the single-instance
     mutex guarantees no double-start).
   - Uninstall removes both tasks (`setup-supervisor.ps1 -Unregister`).

End-to-end crash recovery: native AV → process dies (non-zero exit) → task
engine relaunches within ~1 min → `--restarted` forces `Running=true` →
streaming resumes. Hang recovery: beats stop → heartbeat goes stale → watchdog
task kills + relaunches within ~1 min.
