@echo off
setlocal
cd /d "%~dp0"

echo [1/2] Checking .NET SDK...
where dotnet >nul 2>nul
if errorlevel 1 (
    echo.
    echo [ERROR] dotnet command was not found.
    echo Install the .NET 8 SDK and the ".NET desktop development" workload in Visual Studio Installer.
    pause
    exit /b 1
)

if not exist "%~dp0JsonAssetDataEditor.csproj" (
    echo.
    echo [ERROR] JsonAssetDataEditor.csproj was not found next to this batch file.
    echo Keep build_release.bat in the same DataEditor folder as JsonAssetDataEditor.csproj.
    pause
    exit /b 1
)

echo [2/2] Publishing Windows x64 single EXE...
dotnet publish "%~dp0JsonAssetDataEditor.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o "%~dp0Publish"

if errorlevel 1 (
    echo.
    echo [ERROR] Publish failed. Check the build errors above.
    pause
    exit /b 1
)

if not exist "%~dp0Publish\JsonAssetDataEditor.exe" (
    echo.
    echo [ERROR] Publish finished, but JsonAssetDataEditor.exe was not found.
    pause
    exit /b 1
)

echo.
echo [OK] Publish complete:
echo "%~dp0Publish\JsonAssetDataEditor.exe"
pause
