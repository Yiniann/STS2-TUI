@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"

if exist "%~dp0STS2-TUI.exe" (
    "%~dp0STS2-TUI.exe" %*
    set "STS2_EXIT=%ERRORLEVEL%"
    goto :done
)

if exist "%~dp0dist\STS2-TUI.exe" (
    "%~dp0dist\STS2-TUI.exe" %*
    set "STS2_EXIT=%ERRORLEVEL%"
    goto :done
)

where py >nul 2>nul
if %ERRORLEVEL% EQU 0 (
    py -3 "%~dp0python\play.py" %*
    set "STS2_EXIT=%ERRORLEVEL%"
    goto :done
)

echo STS2-TUI.exe was not found.
echo Download the Windows release, or install Python 3 for source development.
set "STS2_EXIT=1"

:done
if not "%STS2_EXIT%"=="0" (
    echo.
    echo STS2-TUI exited with code %STS2_EXIT%.
    pause
)
exit /b %STS2_EXIT%
