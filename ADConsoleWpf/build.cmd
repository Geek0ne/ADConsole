@echo off
rem ============================================================
rem  ADConsole - build entry
rem  Delegates to build.ps1 so UTF-8 BOM handling stays in one place.
rem ============================================================

setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -Rebuild
if errorlevel 1 (
    echo.
    echo [ERROR] Build failed.
    exit /b 1
)
endlocal
