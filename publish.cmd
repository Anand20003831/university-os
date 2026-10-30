@echo off
rem Publish University OS as a self-contained Windows app.
rem
rem Produces publish\UniversityOS\UniversityOS.exe, which runs by double-click
rem with no .NET installed, no SDK, no terminal and no environment variables.
rem
rem PORTABLE: no username or absolute path is hard-coded. A user-local SDK under
rem %LOCALAPPDATA%\Microsoft\dotnet is preferred WHEN IT EXISTS (a stray 32-bit
rem runtime in "Program Files (x86)" otherwise shadows it); on a machine with a
rem system-wide install, whatever `dotnet` is already on PATH is used instead.
setlocal
cd /d "%~dp0"

if exist "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" (
  set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
  set "PATH=%LOCALAPPDATA%\Microsoft\dotnet;%PATH%"
)

where dotnet >nul 2>&1
if errorlevel 1 (
  echo.
  echo ERROR: the .NET SDK was not found on PATH.
  echo.
  echo This project pins its SDK in global.json ^(10.0.302, roll-forward to any
  echo later 10.0.3xx feature band^). Install it with either:
  echo.
  echo   winget install Microsoft.DotNet.SDK.10
  echo   https://dotnet.microsoft.com/download/dotnet/10.0
  echo.
  echo A user-local install under %%LOCALAPPDATA%%\Microsoft\dotnet is picked up
  echo by this script automatically and needs no PATH changes.
  echo.
  exit /b 1
)

rem Clean first: a stale publish folder can leave orphaned native libraries
rem behind that mask a genuinely missing dependency.
if exist "publish\UniversityOS" rmdir /s /q "publish\UniversityOS"

dotnet publish src\UniversityOS.App ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -o publish\UniversityOS

if errorlevel 1 (
  echo.
  echo PUBLISH FAILED
  exit /b 1
)

echo.
echo Published: %~dp0publish\UniversityOS\UniversityOS.exe
endlocal
