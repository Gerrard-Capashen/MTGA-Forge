@echo off
rem Rebuilds the distribution: compiles the server and bridge, builds dist\MTGA-Forge and the zip, runs check-package.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\package.ps1" -BuildServer -Zip %*
if errorlevel 1 (echo. & echo PACKAGE FAILED) else (echo. & echo Package ready in dist\)
pause
