@echo off
REM ============================================================
REM  build.bat - Compile MS Store Package Downloader
REM  Target: .NET Framework 4.8 / Windows Forms
REM ============================================================

setlocal enabledelayedexpansion

echo.
echo  === Microsoft Store Package Downloader - Build Script ===
echo.

REM ── Locate csc.exe ──────────────────────────────────────────────────────────

set "CSC="

if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" (
    set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    goto :found_csc
)

if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe" (
    set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
    goto :found_csc
)

where csc.exe >nul 2>&1
if !errorlevel! == 0 (
    set "CSC=csc.exe"
    goto :found_csc
)

echo  ERROR: Could not locate csc.exe.
echo.
echo  Please run from a Visual Studio Developer Command Prompt, or install
echo  the .NET Framework 4.8 Developer Pack:
echo  https://dotnet.microsoft.com/download/dotnet-framework/net48
echo.
pause
exit /b 1

:found_csc
echo  Using compiler: %CSC%
echo.

REM ── Paths ───────────────────────────────────────────────────────────────────

set "SRCDIR=%~dp0src"
set "OUTDIR=%~dp0bin"
set "OUTEXE=%OUTDIR%\MSStoreDownloader.exe"

if not exist "%OUTDIR%" mkdir "%OUTDIR%"

REM ── Compile ──────────────────────────────────────────────────────────────────

echo  Compiling...
echo.

"%CSC%" /target:winexe /platform:anycpu /optimize+ /out:"%OUTEXE%" /warn:4 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Net.dll /r:System.Web.dll /r:System.Data.dll /r:Microsoft.CSharp.dll "%SRCDIR%\Program.cs" "%SRCDIR%\Logger.cs" "%SRCDIR%\PackageInfo.cs" "%SRCDIR%\PackageGridRow.cs" "%SRCDIR%\StoreClient.cs" "%SRCDIR%\ManifestReader.cs" "%SRCDIR%\DownloadManager.cs" "%SRCDIR%\MainForm.cs"

if %ERRORLEVEL% neq 0 (
    echo.
    echo  BUILD FAILED  exit code %ERRORLEVEL%
    echo.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo  ============================================================
echo   BUILD SUCCEEDED
echo   Output: %OUTEXE%
echo  ============================================================
echo.

set /p RUN=Run the application now? [Y/N]: 
if /i "%RUN%"=="Y" (
    start "" "%OUTEXE%"
)

endlocal
