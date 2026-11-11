@echo off
setlocal enabledelayedexpansion

set "SCRIPT_DIR=%~dp0"

rem ---- find the first .csproj sitting next to this bat file ----
set "CSPROJ_PATH="
for %%F in ("%SCRIPT_DIR%*.csproj") do (
    if not defined CSPROJ_PATH set "CSPROJ_PATH=%%~fF"
)

if not defined CSPROJ_PATH (
    echo No .csproj file found next to this bat file: %SCRIPT_DIR%
    pause
    exit /b 1
)

rem ---- walk upward until we find publish.ps1 (lives at the repo root) ----
set "DIR=%SCRIPT_DIR%"

:findps1
if exist "%DIR%publish.ps1" goto foundps1
pushd "%DIR%.." >nul 2>&1
if errorlevel 1 goto notfound
set "PARENT=%CD%\"
popd >nul
if /I "%PARENT%"=="%DIR%" goto notfound
set "DIR=%PARENT%"
goto findps1

:notfound
echo Could not find publish.ps1 anywhere above %SCRIPT_DIR%
pause
exit /b 1

:foundps1
set "PS1_PATH=%DIR%publish.ps1"

echo ==^> Publishing %CSPROJ_PATH%
echo ==^> via %PS1_PATH% ...

powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1_PATH%" -CsprojPath "%CSPROJ_PATH%"

echo.
echo ==^> Done. Press any key to close this window...
pause >nul
endlocal
