@echo off
rem Starts MTGA with the Forge rules engine: patches the game copy when needed, runs the server, starts the client.
rem Options are passed to tools\launcher.ps1 (-Folder, -Source, -PatchOnly, -Repatch, -Firewall, -ClientArgs).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\launcher.ps1" %*
exit /b %ERRORLEVEL%