@echo off
setlocal

set "ROOT=%~dp0"
set "RELEASE=%ROOT%bin\Release\net10.0-windows10.0.26100.0\win-x64\Beans.Windows.Rebuild.exe"
set "DEBUG=%ROOT%bin\Debug\net10.0-windows10.0.26100.0\win-x64\Beans.Windows.Rebuild.exe"

if /I "%~1"=="--debug" (
    if exist "%DEBUG%" (
        start "Beans Music" "%DEBUG%"
        exit /b 0
    )
    echo Debug build not found. Build the project first.
    exit /b 1
)

if exist "%RELEASE%" (
    start "Beans Music" "%RELEASE%"
    exit /b 0
)

if exist "%DEBUG%" (
    start "Beans Music" "%DEBUG%"
    exit /b 0
)

echo No Beans Music build was found.
echo Build the project with the Debug or Release configuration first.
exit /b 1
