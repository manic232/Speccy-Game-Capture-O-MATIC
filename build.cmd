@echo off
rem Builds "Speccy Game Capture O-MATIC.exe" using the C# compiler built into Windows.
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ ^
  /win32icon:assets\app.ico ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll ^
  /out:"Speccy Game Capture O-MATIC.exe" src\*.cs
if errorlevel 1 (
  echo.
  echo Build FAILED.
  exit /b 1
)
echo Built "Speccy Game Capture O-MATIC.exe"
