@echo off
setlocal EnableExtensions
title Risk Setup
color 07
cd /d "%~dp0"

set "APP_ROOT=%~dp0"
set "PROJECT=%APP_ROOT%src\risk\Vybe.App.csproj"
if not exist "%PROJECT%" set "PROJECT=%APP_ROOT%src\Vybe.App\Vybe.App.csproj"
if not exist "%PROJECT%" if exist "%APP_ROOT%risk\src\risk\Vybe.App.csproj" (
    set "APP_ROOT=%APP_ROOT%risk\"
    set "PROJECT=%APP_ROOT%src\risk\Vybe.App.csproj"
)
rem Archives sometimes gain extra wrapper folders when extracted. Find the
rem actual project instead of reporting that the setup files are incomplete.
if not exist "%PROJECT%" (
    for /r "%~dp0" %%F in (Vybe.App.csproj) do if not defined FOUND_PROJECT set "FOUND_PROJECT=%%~fF"
    if defined FOUND_PROJECT set "PROJECT=%FOUND_PROJECT%"
)
set "OUT=%LOCALAPPDATA%\Programs\Risk"
set "APP_EXE=%OUT%\Risk.exe"
set "ICON=%OUT%\Assets\app-icon-v2.ico"
set "SDK_VERSION=8.0.426"
set "DOTNET_HOME=%LOCALAPPDATA%\RiskSetup\dotnet"
set "DOTNET_SCRIPT=%TEMP%\risk-dotnet-install.ps1"
set "INSTALL_LOG=%TEMP%\risk-install.log"
set "NO_LAUNCH=0"
if /i "%~1"=="/nolaunch" set "NO_LAUNCH=1"

cls
echo.
echo   RISK  ^|  MUSIC PLAYER
echo   Setup
echo   ----------------------------------------
echo.

if not exist "%PROJECT%" (
    >"%INSTALL_LOG%" echo Project file not found.
    >>"%INSTALL_LOG%" echo Installer location: %~f0
    >>"%INSTALL_LOG%" echo Expected: src\risk\Vybe.App.csproj or src\Vybe.App\Vybe.App.csproj
    >>"%INSTALL_LOG%" echo Checked recursively under: %~dp0
    echo   Could not find the app project in this folder.
    echo   Keep the installer beside the full src folder, then run it again.
    goto failed
)

echo   [1/4] Checking requirements
set "SDK_FOUND=0"
where dotnet >nul 2>&1
if not errorlevel 1 (
    dotnet --list-sdks 2>nul | findstr /b /c:"%SDK_VERSION% " >nul
    if not errorlevel 1 set "SDK_FOUND=1"
)

if "%SDK_FOUND%"=="0" (
    echo         Downloading .NET SDK %SDK_VERSION%...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "$ProgressPreference='SilentlyContinue'; [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile '%DOTNET_SCRIPT%'" >nul 2>&1
    if errorlevel 1 goto failed
    powershell -NoProfile -ExecutionPolicy Bypass -File "%DOTNET_SCRIPT%" -Version %SDK_VERSION% -Architecture x64 -InstallDir "%DOTNET_HOME%" -NoPath > "%INSTALL_LOG%" 2>&1
    if errorlevel 1 goto failed
    set "DOTNET_ROOT=%DOTNET_HOME%"
    set "PATH=%DOTNET_HOME%;%PATH%"
)

echo   [2/4] Preparing app packages
dotnet restore "%PROJECT%" -r win-x64 --verbosity quiet -p:NuGetAudit=false > "%INSTALL_LOG%" 2>&1
if errorlevel 1 goto failed

if exist "%OUT%\Risk.exe" (
    echo   [3/4] Closing Risk and installing the update
    taskkill /f /t /im "Risk.exe" >nul 2>&1
    timeout /t 2 /nobreak >nul
) else (
    echo   [3/4] Installing Risk
)
if exist "%OUT%" rd /s /q "%OUT%" >nul 2>&1
dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true --no-restore --verbosity quiet -p:Platform=x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -p:PublishTrimmed=false -p:DebugType=none -p:DebugSymbols=false -o "%OUT%" > "%INSTALL_LOG%" 2>&1
if errorlevel 1 goto failed
if not exist "%APP_EXE%" goto failed

echo   [4/4] Updating shortcuts and taskbar icon
powershell -NoProfile -ExecutionPolicy Bypass -File "%APP_ROOT%scripts\refresh-shortcuts.ps1" -ExePath "%APP_EXE%" -IconPath "%ICON%" >nul 2>&1

echo.
echo   ----------------------------------------
echo   Risk is ready.
echo   Shortcut: Desktop and Start menu
echo   In-app name: ^<3
echo.
if not "%NO_LAUNCH%"=="1" start "" "%APP_EXE%"
echo   Press any key to close.
pause >nul
exit /b 0

:failed
echo.
echo   Setup could not finish. Check your internet connection
echo   and try again. Details: %INSTALL_LOG%
echo.
echo   Press any key to close.
pause >nul
exit /b 1
