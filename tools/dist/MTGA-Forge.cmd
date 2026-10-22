@echo off
rem Starts MTGA with the Forge rules engine.
rem   no arguments : opens MTGA-Forge.exe, the launcher window (single player, host a friend, join a friend)
rem   arguments    : the command-line launcher (tools\launcher.ps1: -Folder, -Source, -PatchOnly, -Repatch, -Firewall, -ClientArgs, -Instance, -Name, -P2P, -Peer, -PeerKey)
if "%~1"=="" (
    start "" "%~dp0MTGA-Forge.exe"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\launcher.ps1" %*
)
exit /b %ERRORLEVEL%
