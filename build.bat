@echo off
rem Builds with the C# compiler that ships with Windows (.NET Framework 4). Nothing to install.
rem (This file is ASCII-only on purpose: cmd mis-parses batch files that contain Korean text.)
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /out:"%~dp0inmc-kmm.exe" ^
  /win32icon:"%~dp0inmc-kmm.ico" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll "%~dp0Kmm.cs"

if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
echo BUILD OK: %~dp0inmc-kmm.exe
