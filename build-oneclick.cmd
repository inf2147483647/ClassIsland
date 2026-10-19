@echo off
rem ClassIsland one-click build. Real logic lives in build-oneclick.ps1.
setlocal
chcp 65001 >nul
set "PS=powershell.exe"
where pwsh.exe >nul 2>nul && set "PS=pwsh.exe"
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-oneclick.ps1" %*
if errorlevel 1 pause