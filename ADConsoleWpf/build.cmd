@echo off
rem ============================================================
rem  ADConsole - build script
rem  Builds the WPF app using the .NET Framework 4.8 MSBuild
rem  that ships with Windows (no Visual Studio required).
rem ============================================================

setlocal
set "MSBUILD=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
set "PROJ=%~dp0ADConsoleWpf.csproj"

if not exist "%MSBUILD%" (
    echo [ERROR] MSBuild not found: %MSBUILD%
    exit /b 1
)

echo Building %PROJ% ...
"%MSBUILD%" "%PROJ%" /t:Rebuild /p:Configuration=Release /p:Platform=x64 /v:minimal /nologo
if errorlevel 1 (
    echo.
    echo [ERROR] Build failed.
    exit /b 1
)

echo.
echo OK. Output: %~dp0bin\Release\ADConsole.exe
endlocal
