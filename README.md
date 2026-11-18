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
