<#
.SYNOPSIS
  Installs (or removes) the per-user "never stop" supervisor for NDI Screenshare.

.DESCRIPTION
  NDI Screenshare must keep streaming without manual intervention. A native crash
  (e.g. access violation inside Processing.NDI.Lib.x64.dll) kills the process
  instantly and cannot be caught from managed code, so recovery is layered:

  1. In-process: the app retries its pipeline forever with backoff (TrayContext),
     registers with the Windows Restart Manager (RegisterApplicationRestart), and
     writes a heartbeat file every 2s (%APPDATA%\NDI_Screenshare\heartbeat.txt).
  2. Out-of-process (this script): registers two per-user scheduled tasks:
       "NDI Screenshare"            - logon trigger that starts the app, with
                                      "restart on failure every 1 minute"
                                      (RestartCount 9999 ~= unlimited). A crash
                                      exit (non-zero, e.g. native AV) is a
                                      failure, so the task engine relaunches the
                                      app within ~a minute.
       "NDI Screenshare Heartbeat"  - runs install/check-heartbeat.ps1 every
                                      minute. If the heartbeat file is older
                                      than 30s the app is hung (message pump /
                                      writer dead) and is killed + restarted.

  Per-user only: no admin rights required. Silent-safe: never prompts, never
  throws to the caller (always exits 0 so Inno Setup postinstall/silent installs
  can never fail because of this script). Run "../install/build-installer.ps1"
  output or the .iss [Run] section invokes it automatically.

.PARAMETER ExePath
  Full path to NDI_Screenshare.exe. Auto-detected when omitted.

.PARAMETER Unregister
  Removes both scheduled tasks instead of creating them (used by uninstall).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File setup-supervisor.ps1 `
    -ExePath "C:\Program Files\NDI Screenshare\NDI_Screenshare.exe"

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File setup-supervisor.ps1 -Unregister
#>
param(
  [string]$ExePath = '',
  [switch]$Unregister
)

$ErrorActionPreference = 'Stop'

$TaskName = 'NDI Screenshare'
$HeartbeatTaskName = 'NDI Screenshare Heartbeat'

function Write-SupLog([string]$msg) {
  try {
    $dir = Join-Path $env:APPDATA 'NDI_Screenshare\logs'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Add-Content (Join-Path $dir 'supervisor.log') ("$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') [setup-supervisor] $msg")
  } catch { }
}

function Resolve-ExePath([string]$hint) {
  if ($hint -and (Test-Path $hint)) { return $hint }
  $candidates = @()
  if ($hint) { $candidates += $hint }
  # Installed next to this script (normal installer layout: {app}\*.ps1 + exe).
  try { $candidates += (Join-Path $PSScriptRoot 'NDI_Screenshare.exe') } catch { }
  # Currently running instance (repair/re-register while the app is up).
  try {
    $p = Get-Process -Name 'NDI_Screenshare' -ErrorAction SilentlyContinue |
      Select-Object -First 1 -ExpandProperty Path
    if ($p) { $candidates += $p }
  } catch { }
  # Well-known per-user / per-machine install locations.
  foreach ($root in @($env:LOCALAPPDATA, $env:ProgramFiles)) {
    if ($root) {
      $candidates += (Join-Path $root 'Programs\NDI Screenshare\NDI_Screenshare.exe')
      $candidates += (Join-Path $root 'NDI Screenshare\NDI_Screenshare.exe')
    }
  }
  foreach ($c in $candidates) {
    if ($c -and (Test-Path $c)) { return $c }
  }
  return $null
}

function Remove-TaskQuiet([string]$name) {
  try {
    $t = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    if ($t) {
      Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction Stop
      Write-SupLog "Removed scheduled task '$name'."
    }
  } catch {
    Write-SupLog "Could not remove task '$name': $($_.Exception.Message)"
  }
}

try {
  if ($Unregister) {
    Remove-TaskQuiet $HeartbeatTaskName
    Remove-TaskQuiet $TaskName
    Write-SupLog 'Unregister complete.'
    exit 0
  }

  $exe = Resolve-ExePath $ExePath
  if (-not $exe) {
    Write-SupLog "WARNING: NDI_Screenshare.exe not found (hint='$ExePath'); tasks not registered."
    exit 0  # silent-safe: never fail the installer
  }

  $checker = $null
  try { $checker = Join-Path $PSScriptRoot 'check-heartbeat.ps1' } catch { }
  if (-not $checker -or -not (Test-Path $checker)) {
    # Fall back to the exe's directory (same layout, different resolution of $PSScriptRoot).
    $alt = Join-Path (Split-Path $exe -Parent) 'check-heartbeat.ps1'
    if (Test-Path $alt) { $checker = $alt }
  }

  $user = $env:USERNAME
  if (-not $user) { $user = [Environment]::UserName }

  # --- Task 1: launch at logon, restart 1 min after any failure (crash = non-zero exit). ---
  $action1 = New-ScheduledTaskAction -Execute $exe
  $trigger1 = New-ScheduledTaskTrigger -AtLogOn -User $user
  $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel LeastPrivilege
  $settings1 = New-ScheduledTaskSettingsSet `
    -RestartCount 9999 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -StartWhenAvailable `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries
  Register-ScheduledTask -TaskName $TaskName `
    -Action $action1 -Trigger $trigger1 -Principal $principal -Settings $settings1 `
    -Description 'NDI Screenshare: start at logon; restart 1 min after any crash.' `
    -Force | Out-Null
  Write-SupLog "Registered task '$TaskName' (logon trigger, restart-on-failure 1min x9999) -> $exe"

  # --- Task 2: hang watchdog, every minute forever. ---
  if ($checker -and (Test-Path $checker)) {
    $action2 = New-ScheduledTaskAction -Execute 'powershell.exe' `
      -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$checker`""
    $trigger2 = New-ScheduledTaskTrigger -Once -At (Get-Date) `
      -RepetitionInterval (New-TimeSpan -Minutes 1) `
      -RepetitionDuration ([TimeSpan]::MaxValue)
    $settings2 = New-ScheduledTaskSettingsSet `
      -StartWhenAvailable `
      -AllowStartIfOnBatteries `
      -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $HeartbeatTaskName `
      -Action $action2 -Trigger $trigger2 -Principal $principal -Settings $settings2 `
      -Description 'NDI Screenshare: kill + restart the app when its heartbeat is stale (>30s).' `
      -Force | Out-Null
    Write-SupLog "Registered task '$HeartbeatTaskName' (every 1 min) -> $checker"
  } else {
    Write-SupLog "WARNING: check-heartbeat.ps1 not found next to setup script; hang-watchdog task skipped."
  }

  Write-SupLog 'Setup complete.'
  exit 0
} catch {
  Write-SupLog "ERROR: $($_.Exception.Message)"
  exit 0  # silent-safe: the installer must never fail because of the supervisor
}
