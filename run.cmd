@echo off
rem Dev launcher for University OS.
rem
rem PORTABLE: prefers a user-local SDK under %LOCALAPPDATA%\Microsoft\dotnet
rem WHEN IT EXISTS (a stray 32-bit runtime in "Program Files (x86)" otherwise
rem shadows it), and otherwise uses whatever `dotnet` is already on PATH.
setlocal
cd /d "%~dp0"

if exist "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" (
  set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
  set "PATH=%LOCALAPPDATA%\Microsoft\dotnet;%PATH%"
)

where dotnet >nul 2>&1
if errorlevel 1 (
  echo.
  echo ERROR: the .NET SDK was not found on PATH. See global.json for the
  echo pinned version, then install it:
  echo   winget install Microsoft.DotNet.SDK.10
  echo.
  exit /b 1
)

dotnet run --project src\UniversityOS.App
endlocal
