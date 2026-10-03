@echo off
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo ERROR: Windows .NET Framework C# compiler was not found.
  exit /b 1
)

set "TESTEXE=%TEMP%\FastDiskUsage-tests-%RANDOM%-%RANDOM%.exe"
"%CSC%" /nologo /target:exe /main:FastDiskUsage.Tests /out:"%TESTEXE%" /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll Program.cs Tests.cs
if errorlevel 1 exit /b %errorlevel%
"%TESTEXE%"
set "RESULT=%ERRORLEVEL%"
del "%TESTEXE%"
exit /b %RESULT%
