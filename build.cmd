@echo off
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist "%CSC%" (
  echo ERROR: Windows .NET Framework C# compiler was not found.
  echo Checked the standard built-in Framework 4.x compiler paths.
  exit /b 1
)

echo Using: %CSC%
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /win32manifest:app.manifest /out:disk-usage-win.exe /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll Program.cs
if errorlevel 1 exit /b %errorlevel%

echo.
echo Built: %CD%\disk-usage-win.exe
echo Run it with disk-usage-win.exe or run.cmd
endlocal
