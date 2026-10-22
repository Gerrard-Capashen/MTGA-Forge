@echo off
rem Rebuilds ForgeBridge and re-runs the Patcher on the already patched client (bridge + patch steps only, a few seconds).
rem Usage: patch-client.bat [target folder]   (default: client-fresh)
set TARGET=%~1
if "%TARGET%"=="" set TARGET=%~dp0client-fresh
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\forge-patch.ps1" -Target "%TARGET%" -Only bridge,patch
if errorlevel 1 (echo. & echo PATCH FAILED) else (echo. & echo Patched %TARGET%)
pause
