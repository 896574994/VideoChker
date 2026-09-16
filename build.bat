@echo off
rem ============================================================
rem  VideoCheck - one-click build script (open-source edition)
rem
rem  IMPORTANT: this file must contain ASCII ONLY.
rem  cmd.exe reads .bat using the system ANSI codepage (GBK on
rem  Chinese Windows); UTF-8 Chinese comments here would be
rem  garbled and could swallow the commands below.
rem  Chinese text belongs in the .md files (which ARE UTF-8).
rem
rem  Builds with the .NET Framework compiler that ships with Windows.
rem  Output: bin\video_checker_gui.exe
rem ============================================================
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ERROR] csc.exe not found. Please install .NET Framework 4.x.
    pause
    exit /b 1
)

if not exist "bin" mkdir "bin"

echo Building (target: .NET Framework 4.x, runs on Windows 7 / 10 / 11) ...
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu ^
    /out:"bin\video_checker_gui.exe" ^
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll ^
    /r:System.Windows.Forms.dll /r:System.Xml.dll /r:System.Security.dll ^
    /r:Microsoft.CSharp.dll ^
    /win32icon:"assets\app.ico" ^
    src\*.cs

if errorlevel 1 (
    echo [ERROR] Build failed. See messages above.
    pause
    exit /b 1
)

echo [OK] bin\video_checker_gui.exe
echo.
echo NEXT STEP: put ffmpeg.exe and ffprobe.exe into bin\ffmpeg\bin\
echo            (this repo does NOT ship ffmpeg binaries -- see README.md)
pause
