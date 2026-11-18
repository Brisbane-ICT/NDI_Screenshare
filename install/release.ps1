<#
.SYNOPSIS
  One-command ship: rebuild, commit, tag, and publish a GitHub release.

.DESCRIPTION
  1. Rebuilds the versioned installer via install/build-installer.ps1
     (dotnet publish + Inno Setup; falls back to portable zip when ISCC
     is missing — release is skipped in fallback mode unless -AllowZip).
  2. Commits all code (including the versioned setup exe + .sha256,
     which are un-ignored in .gitignore) and pushes main.
  3. Tags v<VERSION>, pushes the tag, and creates (or updates with
     -Force) the GitHub release with the installer attached.

.PARAMETER SkipBuild
  Skip step 1 (reuse the installer already in dist/).

.PARAMETER Force
  If the tag/release already exists, retag and re-upload assets
  (gh release upload --clobber). Default: fail with a hint to bump VERSION.

.PARAMETER AllowZip
  Allow releasing the portable zip when no setup exe was built.
#>
param(
  [switch]$SkipBuild,
  [switch]$Force,
  [switch]$AllowZip
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$version = (Get-Content (Join-Path $repoRoot 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+') { throw "Bad VERSION content: '$version'" }
$tag = "v$version"

# --- 1. Rebuild ---
if (-not $SkipBuild) {
  Write-Host "=== [1/3] Building installer v$version ..."
  powershell -File (Join-Path $PSScriptRoot 'build-installer.ps1')
  if ($LASTEXITCODE -ne 0) { throw "build-installer.ps1 failed (exit $LASTEXITCODE)" }
} else {
  Write-Host "=== [1/3] Skipping build (-SkipBuild)."
}

$setup = Get-ChildItem (Join-Path $repoRoot 'dist') -Filter "*$version*setup*.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $setup) {
  if ($AllowZip) {
    $setup = Get-ChildItem (Join-Path $repoRoot 'dist') -Filter "*$version*.zip" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $setup) { throw "No installer or zip found for v$version in dist/" }
  } else {
    throw "No setup exe found for v$version in dist/ (ISCC may be missing - install Inno Setup, or re-run with -AllowZip)"
  }
}
$assets = @($setup.FullName)
$sha = "$($setup.FullName).sha256"
if (Test-Path $sha) { $assets += $sha }
Write-Host "Assets: $($assets -join ', ')"

# --- 2. Commit all + push ---
Write-Host "=== [2/3] Committing ..."
git add -A
$status = git status --porcelain
if ($status) {
  git commit -m "Release v$version"
  Write-Host "Committed."
} else {
  Write-Host "Nothing to commit (tree clean)."
}
git push origin main
Write-Host "Pushed main."

# --- 3. Tag + GitHub release ---
Write-Host "=== [3/3] Release $tag ..."
$existingTag = git tag -l $tag
if ($existingTag -and -not $Force) {
  throw "Tag $tag already exists. Bump VERSION for a new release, or re-run with -Force to retag."
}
if ($existingTag -and $Force) {
  git tag -d $tag | Out-Null
  git push origin ":refs/tags/$tag"
}
git tag $tag
git push origin $tag

$releaseExists = $false
gh release view $tag --repo Brisbane-ICT/NDI_Screenshare 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) { $releaseExists = $true }

if ($releaseExists -and -not $Force) {
  throw "Release $tag already exists. Re-run with -Force to re-upload assets."
}
if ($releaseExists -and $Force) {
  gh release upload $tag $assets --repo Brisbane-ICT/NDI_Screenshare --clobber
} else {
  $notes = @"
NDI Screenshare $version (Windows x64)

Run the setup exe to install. A tray icon appears; use it to select
the display and start/stop sharing.

PREREQUISITE: free NDI Runtime 6 - https://ndi.video/tools/ndi-core-suite/
"@
  gh release create $tag $assets --repo Brisbane-ICT/NDI_Screenshare `
    --title "NDI Screenshare v$version" --notes $notes
}
Write-Host "Release $tag published."
