$ErrorActionPreference = 'Stop'
# Keep in sync with install/build-installer.ps1: stamp the assembly with VERSION.
$repoRoot = Split-Path -Parent $PSScriptRoot
$version = (Get-Content (Join-Path $repoRoot 'VERSION') -Raw).Trim()
dotnet publish "$PSScriptRoot/../src/TrayApp/TrayApp.csproj" -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:Version=$version -o "$PSScriptRoot/../dist/win-x64"
Write-Host "Published v$version to dist/win-x64"
