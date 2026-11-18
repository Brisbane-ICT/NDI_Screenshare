<#
.SYNOPSIS
  Build a versioned NDI Screenshare installer (or portable fallback).

.DESCRIPTION
  1. Reads VERSION from the repo root.
  2. Publishes src/TrayApp framework-dependent (small download, needs
     .NET 8 Desktop Runtime on the target machine):
       dotnet publish src/TrayApp -c Release -r win-x64 --self-contained false -p:Version=<VERSION>
     For a fully self-contained (larger, no .NET needed) build use:
       -SelfContained switch, or install/build-selfcontained.ps1.
  3. If Inno Setup (ISCC.exe) is found, builds install/ndi-screenshare.iss
     into dist/NDI_Screenshare-<VERSION>-win64-setup.exe.
     Otherwise zips a versioned portable folder
     dist/NDI_Screenshare-<VERSION>-win64/ (+ .zip) with README + SHA256.

.PARAMETER SelfContained
  Publish self-contained instead of framework-dependent (bigger, no .NET install needed).

.PARAMETER Runtime
  Target runtime identifier. Default win-x64.

.PARAMETER SkipPublish
  Skip the dotnet publish step (reuse dist/publish).
#>
param(
  [switch]$SelfContained,
  [string]$Runtime = 'win-x64',
  [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$versionFile = Join-Path $repoRoot 'VERSION'
if (-not (Test-Path $versionFile)) { throw "VERSION file not found at $versionFile" }
$version = (Get-Content $versionFile -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+') { throw "Bad VERSION content: '$version' (expected SemVer like 0.1.0)" }

$publishDir = Join-Path $repoRoot 'dist\publish'
$distDir = Join-Path $repoRoot 'dist'
New-Item -ItemType Directory -Force -Path $publishDir, $distDir | Out-Null

if (-not $SkipPublish) {
  if ($SelfContained) { $scArg = 'true' } else { $scArg = 'false' }
  Write-Host "Publishing TrayApp v$version (self-contained=$scArg, runtime=$Runtime)..."
  dotnet publish "$repoRoot\src\TrayApp\TrayApp.csproj" `
    -c Release -r $Runtime --self-contained $scArg `
    /p:PublishSingleFile=true /p:Version=$version `
    -o $publishDir
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }
  Write-Host "Published to $publishDir"
} else {
  Write-Host "Skipping publish, reusing $publishDir"
}

# --- Try Inno Setup ---
$iscc = $null
$candidates = @(
  (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
  "$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe",
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique
if ($candidates) { $iscc = $candidates | Select-Object -First 1 }

if ($iscc) {
  Write-Host "Building installer with $iscc ..."
  & $iscc "/DMyAppVersion=$version" (Join-Path $PSScriptRoot 'ndi-screenshare.iss')
  if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit $LASTEXITCODE)" }
  Get-ChildItem $distDir -Filter "*$version*setup*.exe" | Format-Table Name, Length
  Write-Host 'Installer build done.'
  exit 0
}

# --- Fallback: versioned portable zip ---
Write-Host 'ISCC not found — creating versioned portable folder + zip instead.'
$folderName = "NDI_Screenshare-$version-win64"
$stageDir = Join-Path $distDir $folderName
if (Test-Path $stageDir) { Remove-Item -Recurse -Force $stageDir }
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
Copy-Item "$publishDir\*" $stageDir -Recurse -Force

$readme = @"
NDI Screenshare $version (portable, Windows x64)
================================================

Run NDI_Screenshare.exe to start. A tray icon appears; use it to
select the display and start/stop sharing.

PREREQUISITE: free NDI Runtime 6
  https://ndi.video/tools/ndi-core-suite/

Notes:
- This portable build is framework-dependent: it needs the
  .NET 8 Desktop Runtime (https://dotnet.microsoft.com/download).
  For a build with .NET bundled, use install/build-selfcontained.ps1
  or build-installer.ps1 -SelfContained.
- To start automatically at logon, place a shortcut to
  NDI_Screenshare.exe in shell:startup.
"@
$readme | Out-File (Join-Path $stageDir 'README.txt') -Encoding utf8

$zipPath = Join-Path $distDir "$folderName.zip"
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
Compress-Archive -Path "$stageDir\*" -DestinationPath $zipPath

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash
"$hash  $folderName.zip" | Out-File "$zipPath.sha256" -Encoding ascii
Write-Host "Portable folder: $stageDir"
Write-Host "Zip:             $zipPath"
Write-Host "SHA256:          $hash"
