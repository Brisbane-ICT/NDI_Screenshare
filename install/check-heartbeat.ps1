<#
.SYNOPSIS
  Hang watchdog for NDI Screenshare (runs every minute via scheduled task).

.DESCRIPTION
  Reads the heartbeat file the app writes every 2s
  (%APPDATA%\NDI_Screenshare\heartbeat.txt, see src/Infra/Heartbeat.cs).
  If the heartbeat is older than MaxAgeSeconds (default 30), or missing while
  the process has been up long enough to have written one, the process is hung:
  kill it and start a fresh instance. The "NDI Screenshare" scheduled task's
  restart-on-failure then also keeps a copy running, and the app's single-
  instance mutex guarantees the two can never double-start.

  Never throws to the caller: always exits 0 so the scheduled task stays
  healthy and silent installs are unaffected. Activity is appended to
  %APPDATA%\NDI_Screenshare\logs\supervisor.log (best effort).

.PARAMETER MaxAgeSeconds
  Heartbeat older than this counts as hung. Default 30.

.PARAMETER HeartbeatPath
  Override for the heartbeat file location.

.PARAMETER ProcessName
  Process to watch. Default NDI_Screenshare.

.PARAMETER ExePath
  Binary to relaunch after killing a hung process. Auto-detected when omitted;
  if it cannot be resolved the hung process is left alone (killing without a
  relaunch path would trade a hung feed for no feed at all).
#>
param(
  [int]$MaxAgeSeconds = 30,
  [string]$HeartbeatPath = '',
  [string]$ProcessName = 'NDI_Screenshare',
  [string]$ExePath = ''
)

$ErrorActionPreference = 'Stop'

function Write-SupLog([string]$msg) {
  try {
    $dir = Join-Path $env:APPDATA 'NDI_Screenshare\logs'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Add-Content (Join-Path $dir 'supervisor.log') ("$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') [check-heartbeat] $msg")
  } catch { }
}

function Resolve-ExePath([string]$hint, [string]$procName) {
  if ($hint -and (Test-Path $hint)) { return $hint }
  try {
    $p = Get-Process -Name $procName -ErrorAction SilentlyContinue |
      Select-Object -First 1 -ExpandProperty Path
    if ($p -and (Test-Path $p)) { return $p }
  } catch { }
  try {
    $here = Join-Path $PSScriptRoot 'NDI_Screenshare.exe'
    if (Test-Path $here) { return $here }
  } catch { }
  foreach ($root in @($env:LOCALAPPDATA, $env:ProgramFiles)) {
    if ($root) {
      foreach ($rel in @('Programs\NDI Screenshare\NDI_Screenshare.exe',
                         'NDI Screenshare\NDI_Screenshare.exe')) {
        $c = Join-Path $root $rel
        if (Test-Path $c) { return $c }
      }
    }
  }
  return $null
}

try {
  if (-not $HeartbeatPath) {
    $HeartbeatPath = Join-Path $env:APPDATA 'NDI_Screenshare\heartbeat.txt'
  }

  $procs = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
  if ($procs.Count -eq 0) {
    # Not running: nothing to watch. (Intentional exits stay exited; the logon
    # task starts the app at next logon, and crash exits are restarted by the
    # task engine's restart-on-failure.)
    exit 0
  }

  $hung = $false
  $reason = ''
  $hb = Get-Item $HeartbeatPath -ErrorAction SilentlyContinue
  if ($hb) {
    try {
      $stamp = [datetime]::Parse(
        (Get-Content $HeartbeatPath -Raw -ErrorAction Stop).Trim(),
        [cultureinfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::RoundtripKind)
      $age = (Get-Date).ToUniversalTime() - $stamp.ToUniversalTime()
      if ($age.TotalSeconds -gt $MaxAgeSeconds) {
        $hung = $true
        $reason = "heartbeat age $([int]$age.TotalSeconds)s > ${MaxAgeSeconds}s"
      }
    } catch {
      # Unparseable heartbeat: treat like a missing one (see below).
      $hb = $null
    }
  }
  if (-not $hung -and -not $hb) {
    # No heartbeat at all: a freshly started process needs ~2s for its first
    # write, so only act when every instance has been up a while (>120s).
    $oldest = ($procs | ForEach-Object {
      try { $_.StartTime } catch { (Get-Date) }
    } | Sort-Object | Select-Object -First 1)
    if (((Get-Date) - $oldest).TotalSeconds -gt 120) {
      $hung = $true
      $reason = 'no heartbeat file while process up >120s'
    }
  }
  if (-not $hung) { exit 0 }

  $exe = Resolve-ExePath $ExePath $ProcessName
  if (-not $exe) {
    Write-SupLog "WARNING: hung process detected ($reason) but exe path unknown; leaving it alone."
    exit 0
  }

  Write-SupLog "Hung process detected ($reason); restarting $exe."
  try {
    Stop-Process -Name $ProcessName -Force -ErrorAction Stop
  } catch {
    Write-SupLog "Stop-Process note: $($_.Exception.Message)"
  }
  Start-Sleep -Seconds 2
  $still = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
  if ($still.Count -eq 0) {
    try {
      Start-Process -FilePath $exe -ErrorAction Stop
      Write-SupLog 'Relaunched app.'
    } catch {
      Write-SupLog "Relaunch failed: $($_.Exception.Message)"
    }
  } else {
    Write-SupLog 'Process still alive after kill; leaving it for the next minute.'
  }
  exit 0
} catch {
  Write-SupLog "ERROR: $($_.Exception.Message)"
  exit 0  # never fail the scheduled task with noise
}
