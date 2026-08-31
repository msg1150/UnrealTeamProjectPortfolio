@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] .NET 8 SDK가 필요합니다.
    pause
    exit /b 1
)
dotnet build JsonAssetDataEditor.csproj -c Debug
pause
