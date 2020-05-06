@echo off
title AD Console - %USERDOMAIN%
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0ADConsole.ps1"
if errorlevel 1 (
    echo.
    echo [Error] AD Console exited with an error. See message above.
    pause
)
