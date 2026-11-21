' NDI Screenshare — windowless launcher for check-heartbeat.ps1.
' Run by the "NDI Screenshare Heartbeat" scheduled task via:
'   wscript.exe //B //Nologo launch-hidden.vbs
' wscript.exe is windowless, so unlike powershell.exe launched directly
' from Task Scheduler, this never flashes a console window.
' The .ps1 path is resolved relative to this script (same install dir).
Option Explicit
Dim shell, fso, ps1
Set shell = CreateObject("Wscript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
ps1 = fso.BuildPath(fso.GetParentFolderName(WScript.ScriptFullName), "check-heartbeat.ps1")
shell.Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & ps1 & """", 0, False
