@echo off
cd /d "%~dp0"
if not exist disk-usage-win.exe (
  echo disk-usage-win.exe not found. Run build.cmd first.
  exit /b 1
)
start "" disk-usage-win.exe
